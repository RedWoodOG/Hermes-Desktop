using System.Runtime.CompilerServices;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace HermesDesktop.Tests.Services;

/// <summary>
/// End-to-end shape tests for the agent loop's outgoing wire payload.
/// Drives <see cref="Agent.ChatAsync"/> and <see cref="Agent.StreamChatAsync"/>
/// with sessions that contain stacked <c>role:"system"</c> entries and
/// asserts that the message list reaching <see cref="IChatClient"/> has
/// exactly one leading system message and zero mid-list system messages —
/// the load-bearing invariant strict OpenAI-compatible servers (vLLM with
/// Qwen / Llama-3 chat templates, llama.cpp strict templates, TGI, several
/// LMStudio strict-template models) require.
///
/// These tests complement the bridge-contract tests by exercising the
/// real Agent loop, not just the IChatClient bridge. They catch regressions
/// where a future call site is added in <see cref="Agent"/> that forgets
/// to coalesce, or where a future plugin/subsystem injects a stray
/// <c>role:"system"</c> in a way the Agent doesn't notice.
/// </summary>
[TestClass]
public class AgentSystemMessageShapeTests
{
    private static void AssertSingleLeadingSystem(IReadOnlyList<Message> messages, params string[] expectedContentFragments)
    {
        Assert.IsTrue(messages.Count > 0, "Outgoing message list must not be empty.");
        Assert.AreEqual("system", messages[0].Role,
            "Wire payload must begin with a single coalesced system message; strict OpenAI-compatible servers reject otherwise.");

        for (int i = 1; i < messages.Count; i++)
        {
            Assert.AreNotEqual("system", messages[i].Role,
                $"Mid-list system message at index {i} would trip strict chat-template enforcement (vLLM/Qwen, llama.cpp strict, TGI). Content: {messages[i].Content}");
        }

        foreach (var fragment in expectedContentFragments)
        {
            StringAssert.Contains(messages[0].Content, fragment,
                $"Coalesced system block must contain layered content '{fragment}'.");
        }
    }

    private static void SeedStackedSystemMessages(Session session)
    {
        // Mirrors what AgentContextAssembler / PluginManager / MemoryManager /
        // SoulService produce in the wild: multiple discrete role:"system"
        // entries inserted at session.Messages[0] across the assembly path.
        session.Messages.Insert(0, new Message { Role = "system", Content = "[soul] you are helpful" });
        session.Messages.Insert(0, new Message { Role = "system", Content = "[plugins] turn budget=10" });
        session.Messages.Insert(0, new Message { Role = "system", Content = "[memory] user prefers terse replies" });
    }

    // ── No-tools ChatAsync: routes through CompleteAsync ──

    [TestMethod]
    public async Task ChatAsync_NoTools_StackedSystemMessages_WirePayloadHasSingleLeadingSystem()
    {
        var chatClient = new Mock<IChatClient>(MockBehavior.Strict);
        IEnumerable<Message>? captured = null;

        chatClient.Setup(c => c.CompleteAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Message>, CancellationToken>((msgs, _) => captured = msgs.ToList())
            .ReturnsAsync("LLM answer");

        var agent = new Agent(chatClient.Object, NullLogger<Agent>.Instance);
        var session = new Session { Id = "shape-no-tools" };
        SeedStackedSystemMessages(session);

        var result = await agent.ChatAsync("hello", session, CancellationToken.None);

        Assert.AreEqual("LLM answer", result);
        Assert.IsNotNull(captured);
        var observed = captured!.ToList();

        AssertSingleLeadingSystem(observed, "[soul]", "[plugins]", "[memory]");
    }

    // ── With-tools ChatAsync: routes through CompleteWithToolsAsync (every iteration) ──

    [TestMethod]
    public async Task ChatAsync_WithTools_EveryIterationWirePayloadHasSingleLeadingSystem()
    {
        var chatClient = new Mock<IChatClient>(MockBehavior.Strict);
        var capturedPerCall = new List<List<Message>>();

        chatClient.Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Message>, IEnumerable<ToolDefinition>, CancellationToken>((msgs, _, _) =>
                capturedPerCall.Add(msgs.ToList()))
            .ReturnsAsync(() =>
            {
                // First call: ask for a tool. Second call: stop.
                return capturedPerCall.Count == 1
                    ? new ChatResponse
                    {
                        Content = "running",
                        ToolCalls = new List<ToolCall>
                        {
                            new() { Id = "tc-1", Name = "echo_tool", Arguments = "{}" }
                        },
                        FinishReason = "tool_calls"
                    }
                    : new ChatResponse { Content = "done", FinishReason = "stop", ToolCalls = null };
            });

        var tool = new Mock<ITool>(MockBehavior.Strict);
        tool.SetupGet(t => t.Name).Returns("echo_tool");
        tool.SetupGet(t => t.Description).Returns("Echo");
        tool.SetupGet(t => t.ParametersType).Returns(typeof(EmptyParams));
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("tool-output"));

        var agent = new Agent(chatClient.Object, NullLogger<Agent>.Instance);
        agent.RegisterTool(tool.Object);

        var session = new Session { Id = "shape-with-tools" };
        SeedStackedSystemMessages(session);

        var result = await agent.ChatAsync("hello", session, CancellationToken.None);

        Assert.AreEqual("done", result);
        Assert.AreEqual(2, capturedPerCall.Count, "Tool loop should call provider twice (request tool, then stop).");

        // Both wire payloads — initial and post-tool-result — must coalesce.
        AssertSingleLeadingSystem(capturedPerCall[0], "[soul]", "[plugins]", "[memory]");
        AssertSingleLeadingSystem(capturedPerCall[1], "[soul]", "[plugins]", "[memory]");
    }

    // ── StreamChatAsync no-tools: routes through StreamAsync(string?, ...) ──

    [TestMethod]
    public async Task StreamChatAsync_NoTools_WirePayloadHasSingleLeadingSystem_AndSystemPromptThreaded()
    {
        var chatClient = new Mock<IChatClient>(MockBehavior.Strict);
        IEnumerable<Message>? capturedMessages = null;
        string? capturedSystemPrompt = null;

        chatClient.Setup(c => c.StreamAsync(
                It.IsAny<string?>(),
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string?, IEnumerable<Message>, IEnumerable<ToolDefinition>?, CancellationToken>(
                (sp, msgs, _, _) =>
                {
                    capturedSystemPrompt = sp;
                    capturedMessages = msgs.ToList();
                })
            .Returns(EmptyStream());

        var agent = new Agent(chatClient.Object, NullLogger<Agent>.Instance);
        var session = new Session { Id = "shape-stream-no-tools" };
        SeedStackedSystemMessages(session);

        await foreach (var _ in agent.StreamChatAsync("hello", session, CancellationToken.None))
        {
            // drain
        }

        Assert.IsNotNull(capturedMessages);
        AssertSingleLeadingSystem(capturedMessages!.ToList(), "[soul]", "[plugins]", "[memory]");

        // AnthropicClient streaming requires non-null systemPrompt; OpenAiClient
        // ignores it but it must be threaded through so the bridge works for both.
        Assert.IsNotNull(capturedSystemPrompt,
            "Streaming bridge must thread rendered system into systemPrompt so AnthropicClient can populate top-level system field.");
        StringAssert.Contains(capturedSystemPrompt!, "[soul]");
        StringAssert.Contains(capturedSystemPrompt!, "[plugins]");
        StringAssert.Contains(capturedSystemPrompt!, "[memory]");
    }

    // ── StreamChatAsync with-tools: routes through CompleteWithToolsAsync per iteration ──

    [TestMethod]
    public async Task StreamChatAsync_WithTools_EveryIterationWirePayloadHasSingleLeadingSystem()
    {
        var chatClient = new Mock<IChatClient>(MockBehavior.Strict);
        var capturedPerCall = new List<List<Message>>();

        chatClient.Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Message>, IEnumerable<ToolDefinition>, CancellationToken>((msgs, _, _) =>
                capturedPerCall.Add(msgs.ToList()))
            .ReturnsAsync(() =>
            {
                return capturedPerCall.Count == 1
                    ? new ChatResponse
                    {
                        Content = "running",
                        ToolCalls = new List<ToolCall>
                        {
                            new() { Id = "tc-1", Name = "echo_tool", Arguments = "{}" }
                        },
                        FinishReason = "tool_calls"
                    }
                    : new ChatResponse { Content = "done", FinishReason = "stop", ToolCalls = null };
            });

        var tool = new Mock<ITool>(MockBehavior.Strict);
        tool.SetupGet(t => t.Name).Returns("echo_tool");
        tool.SetupGet(t => t.Description).Returns("Echo");
        tool.SetupGet(t => t.ParametersType).Returns(typeof(EmptyParams));
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("tool-output"));

        var agent = new Agent(chatClient.Object, NullLogger<Agent>.Instance);
        agent.RegisterTool(tool.Object);

        var session = new Session { Id = "shape-stream-with-tools" };
        SeedStackedSystemMessages(session);

        await foreach (var _ in agent.StreamChatAsync("hello", session, CancellationToken.None))
        {
            // drain
        }

        Assert.AreEqual(2, capturedPerCall.Count);
        AssertSingleLeadingSystem(capturedPerCall[0], "[soul]", "[plugins]", "[memory]");
        AssertSingleLeadingSystem(capturedPerCall[1], "[soul]", "[plugins]", "[memory]");
    }

    // ── Defense-in-depth: a stray mid-list system in session.Messages must be hoisted ──

    [TestMethod]
    public async Task ChatAsync_StrayMidConversationSystem_HoistedIntoLeadingSystemNotPropagated()
    {
        // This is the regression bait. Today's known injection sites all
        // Insert(0, ...) before the user message — the leading system is
        // "naturally" at index 0. But if a future plugin or subsystem
        // appends a role:"system" message later in the conversation
        // (after user/assistant turns), the coalescer must still hoist it.
        var chatClient = new Mock<IChatClient>(MockBehavior.Strict);
        IEnumerable<Message>? captured = null;

        chatClient.Setup(c => c.CompleteAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Message>, CancellationToken>((msgs, _) => captured = msgs.ToList())
            .ReturnsAsync("ok");

        var agent = new Agent(chatClient.Object, NullLogger<Agent>.Instance);
        var session = new Session { Id = "shape-stray" };

        session.AddMessage(new Message { Role = "user", Content = "earlier turn" });
        session.AddMessage(new Message { Role = "assistant", Content = "earlier reply" });
        // Stray mid-list system — exactly the shape strict servers reject.
        session.AddMessage(new Message { Role = "system", Content = "[stray] late-injected directive" });

        await agent.ChatAsync("next message", session, CancellationToken.None);

        Assert.IsNotNull(captured);
        var observed = captured!.ToList();
        AssertSingleLeadingSystem(observed, "[stray]");
    }

    // ── Helpers ──

    private static async IAsyncEnumerable<StreamEvent> EmptyStream(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class EmptyParams { }
}
