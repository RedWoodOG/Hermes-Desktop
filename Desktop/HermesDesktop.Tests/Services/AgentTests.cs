using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Hermes.Agent.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.Text.Json;

namespace HermesDesktop.Tests.Services;

/// <summary>
/// Tests for the Agent class — used directly by the new HermesChatService
/// (PR replaced sidecar-based approach with direct in-process agent execution).
/// Agent.ChatAsync is the core method called by HermesChatService.SendAsync.
/// </summary>
[TestClass]
public class AgentTests
{
    private Mock<IChatClient> _mockChatClient = null!;
    private Agent _agent = null!;

    [TestInitialize]
    public void SetUp()
    {
        _mockChatClient = new Mock<IChatClient>(MockBehavior.Strict);
        _agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance);
    }

    // ── Tool Registration ──

    [TestMethod]
    public void RegisterTool_AddsToolToRegistry()
    {
        var tool = CreateMockTool("echo_tool");

        _agent.RegisterTool(tool.Object);

        Assert.IsTrue(_agent.Tools.ContainsKey("echo_tool"));
    }

    [TestMethod]
    public void RegisterTool_MultipleTools_AllRegistered()
    {
        var tool1 = CreateMockTool("tool_a");
        var tool2 = CreateMockTool("tool_b");
        var tool3 = CreateMockTool("tool_c");

        _agent.RegisterTool(tool1.Object);
        _agent.RegisterTool(tool2.Object);
        _agent.RegisterTool(tool3.Object);

        Assert.AreEqual(3, _agent.Tools.Count);
    }

    [TestMethod]
    public void RegisterTool_DuplicateName_OverwritesPrevious()
    {
        var tool1 = CreateMockTool("same_name");
        var tool2 = CreateMockTool("same_name");

        _agent.RegisterTool(tool1.Object);
        _agent.RegisterTool(tool2.Object);

        // Should not throw; only one entry with the same name
        Assert.AreEqual(1, _agent.Tools.Count);
        Assert.AreSame(tool2.Object, _agent.Tools["same_name"]);
    }

    [TestMethod]
    public void Tools_InitiallyEmpty()
    {
        Assert.AreEqual(0, _agent.Tools.Count);
    }

    // ── MaxToolIterations ──

    [TestMethod]
    public void MaxToolIterations_DefaultIsTwentyFive()
    {
        Assert.AreEqual(25, _agent.MaxToolIterations);
    }

    [TestMethod]
    public void MaxToolIterations_CanBeChanged()
    {
        _agent.MaxToolIterations = 10;

        Assert.AreEqual(10, _agent.MaxToolIterations);
    }

    // ── GetToolDefinitions ──

    [TestMethod]
    public void GetToolDefinitions_NoTools_ReturnsEmptyList()
    {
        var defs = _agent.GetToolDefinitions();

        Assert.AreEqual(0, defs.Count);
    }

    [TestMethod]
    public void GetToolDefinitions_ReturnsDefinitionForEachTool()
    {
        _agent.RegisterTool(CreateMockTool("tool_x").Object);
        _agent.RegisterTool(CreateMockTool("tool_y").Object);

        var defs = _agent.GetToolDefinitions();

        Assert.AreEqual(2, defs.Count);
    }

    [TestMethod]
    public void GetToolDefinitions_IncludesToolNameAndDescription()
    {
        var tool = new Mock<ITool>();
        tool.Setup(t => t.Name).Returns("my_tool");
        tool.Setup(t => t.Description).Returns("Does things");
        tool.Setup(t => t.ParametersType).Returns(typeof(EmptyParams));
        _agent.RegisterTool(tool.Object);

        var defs = _agent.GetToolDefinitions();

        Assert.AreEqual(1, defs.Count);
        Assert.AreEqual("my_tool", defs[0].Name);
        Assert.AreEqual("Does things", defs[0].Description);
    }

    [TestMethod]
    public void GetToolDefinitions_UsesProviderNativeSchemaWhenAvailable()
    {
        var tool = new SchemaBackedTool();
        _agent.RegisterTool(tool);

        var defs = _agent.GetToolDefinitions();

        Assert.AreEqual(1, defs.Count);
        Assert.IsTrue(defs[0].Parameters.TryGetProperty("properties", out var properties));
        Assert.IsTrue(properties.TryGetProperty("path", out var path));
        Assert.AreEqual("string", path.GetProperty("type").GetString());
        Assert.IsFalse(properties.TryGetProperty("arguments", out _),
            "Schema-backed tools should expose their native input schema instead of the generic wrapper type.");
    }

    // ── ChatAsync — no tools ──

    [TestMethod]
    public async Task ChatAsync_NoToolsRegistered_CallsCompleteAsync()
    {
        var session = new Session { Id = "test-sess" };
        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Hello from LLM");

        var result = await _agent.ChatAsync("Hi there", session, CancellationToken.None);

        Assert.AreEqual("Hello from LLM", result);
        _mockChatClient.Verify(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ChatAsync_NoTools_AddsUserAndAssistantMessagesToSession()
    {
        var session = new Session { Id = "s1" };
        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("assistant reply");

        await _agent.ChatAsync("user input", session, CancellationToken.None);

        Assert.AreEqual(2, session.Messages.Count);
        Assert.AreEqual("user", session.Messages[0].Role);
        Assert.AreEqual("user input", session.Messages[0].Content);
        Assert.AreEqual("assistant", session.Messages[1].Role);
        Assert.AreEqual("assistant reply", session.Messages[1].Content);
    }

    [TestMethod]
    public async Task ChatAsync_NoTools_PassesSessionHistoryToLLM()
    {
        var session = new Session { Id = "s2" };
        session.AddMessage(new Message { Role = "user", Content = "earlier message" });

        IEnumerable<Message>? captured = null;
        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Message>, CancellationToken>((msgs, _) => captured = msgs)
            .ReturnsAsync("ok");

        await _agent.ChatAsync("new message", session, CancellationToken.None);

        Assert.IsNotNull(captured);
        var msgList = captured.ToList();
        // Should include: "earlier message" + "new message" (added at start of ChatAsync)
        Assert.IsTrue(msgList.Count >= 2, "Session history should be passed to LLM");
        Assert.IsTrue(msgList.Any(m => m.Content == "earlier message"));
        Assert.IsTrue(msgList.Any(m => m.Content == "new message"));
    }

    [TestMethod]
    public async Task ChatAsync_NoTools_ReturnsLlmResponse()
    {
        var session = new Session { Id = "s3" };
        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("The answer is 42");

        var result = await _agent.ChatAsync("What is the answer?", session, CancellationToken.None);

        Assert.AreEqual("The answer is 42", result);
    }

    [TestMethod]
    public async Task ChatAsync_WithToolsRegistered_CallsCompleteWithToolsAsync()
    {
        _agent.RegisterTool(CreateMockTool("some_tool").Object);
        var session = new Session { Id = "s4" };

        _mockChatClient
            .Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse { Content = "done", FinishReason = "stop" });

        var result = await _agent.ChatAsync("do something", session, CancellationToken.None);

        Assert.AreEqual("done", result);
        _mockChatClient.Verify(c => c.CompleteWithToolsAsync(
            It.IsAny<IEnumerable<Message>>(),
            It.IsAny<IEnumerable<ToolDefinition>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ChatAsync_WithTools_ReturnsTextResponseWhenLlmStops()
    {
        _agent.RegisterTool(CreateMockTool("t1").Object);
        var session = new Session { Id = "s5" };

        _mockChatClient
            .Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse { Content = "final response", FinishReason = "stop", ToolCalls = null });

        var result = await _agent.ChatAsync("q", session, CancellationToken.None);

        Assert.AreEqual("final response", result);
    }

    [TestMethod]
    public async Task ChatAsync_WithTools_AddsUserMessageBeforeToolCalls()
    {
        _agent.RegisterTool(CreateMockTool("t2").Object);
        var session = new Session { Id = "s6" };

        _mockChatClient
            .Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse { Content = "answered", FinishReason = "stop" });

        await _agent.ChatAsync("my question", session, CancellationToken.None);

        Assert.IsTrue(session.Messages.Count >= 1);
        Assert.AreEqual("user", session.Messages[0].Role);
        Assert.AreEqual("my question", session.Messages[0].Content);
    }

    [TestMethod]
    public async Task ChatAsync_CancellationToken_PropagatedToLLM()
    {
        var session = new Session { Id = "s7" };
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            await _agent.ChatAsync("any", session, cts.Token));
    }

    /// <summary>
    /// End-to-end tool loop (provider-agnostic): LLM requests a tool → tool executes → LLM returns final text.
    /// Mirrors Anthropic/OpenRouter tool-calling behavior without live HTTP; guards HermesChatService path.
    /// </summary>
    [TestMethod]
    public async Task ChatAsync_TwoTurnToolLoop_ExecutesToolThenReturnsFinalAnswer()
    {
        var tool = CreateMockTool("e2e_tool");
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("tool_result_payload"));
        _agent.RegisterTool(tool.Object);

        var session = new Session { Id = "sess-tool-e2e" };

        var turn1 = new ChatResponse
        {
            Content = "",
            FinishReason = "tool_calls",
            ToolCalls = new List<ToolCall>
            {
                new ToolCall { Id = "call_1", Name = "e2e_tool", Arguments = "{}" }
            }
        };
        var turn2 = new ChatResponse
        {
            Content = "Final answer after tool.",
            FinishReason = "stop",
            ToolCalls = null
        };

        var responses = new Queue<ChatResponse>(new[] { turn1, turn2 });
        _mockChatClient
            .Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var next = responses.Dequeue();
                return Task.FromResult(next);
            });

        var result = await _agent.ChatAsync("Please use e2e_tool.", session, CancellationToken.None);

        Assert.AreEqual("Final answer after tool.", result);
        _mockChatClient.Verify(c => c.CompleteWithToolsAsync(
            It.IsAny<IEnumerable<Message>>(),
            It.IsAny<IEnumerable<ToolDefinition>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));

        tool.Verify(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);

        Assert.IsTrue(session.Messages.Count >= 4, "Expected user + assistant (tool calls) + tool + assistant");
        Assert.AreEqual("user", session.Messages[0].Role);
        Assert.AreEqual("assistant", session.Messages[1].Role);
        Assert.IsNotNull(session.Messages[1].ToolCalls);
        Assert.AreEqual("tool", session.Messages[2].Role);
        Assert.AreEqual("assistant", session.Messages[3].Role);
    }

    // ── Helpers ──

    private static Mock<ITool> CreateMockTool(string name)
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Name).Returns(name);
        mock.Setup(t => t.Description).Returns($"Description of {name}");
        mock.Setup(t => t.ParametersType).Returns(typeof(EmptyParams));
        return mock;
    }

    private sealed class SchemaBackedTool : ITool, IToolSchemaProvider
    {
        public string Name => "schema_tool";
        public string Description => "Uses provider-native schema";
        public Type ParametersType => typeof(EmptyParams);

        public Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok("ok"));

        public JsonElement? GetParameterSchema() =>
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string" }
                },
                required = new[] { "path" }
            });
    }

    /// <summary>Minimal parameter type used to satisfy Agent's schema builder.</summary>
    private sealed class EmptyParams { }
}

/// <summary>
/// Tests for Agent with PluginManager integration.
/// Verifies the plugin lifecycle hooks are invoked during ChatAsync.
/// </summary>
[TestClass]
public class AgentPluginManagerTests
{
    private Mock<IChatClient> _mockChatClient = null!;
    private PluginManager _pluginManager = null!;
    private Mock<IPlugin> _mockPlugin = null!;

    [TestInitialize]
    public void SetUp()
    {
        _mockChatClient = new Mock<IChatClient>(MockBehavior.Strict);
        _pluginManager = new PluginManager(NullLogger<PluginManager>.Instance);

        _mockPlugin = new Mock<IPlugin>();
        _mockPlugin.Setup(p => p.Name).Returns("test-plugin");
        _mockPlugin.Setup(p => p.IsBuiltin).Returns(true);
        _mockPlugin.Setup(p => p.Category).Returns("general");
        _mockPlugin.Setup(p => p.GetTools()).Returns(Array.Empty<ITool>());
        _mockPlugin.Setup(p => p.GetSystemPromptBlockAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _mockPlugin.Setup(p => p.OnTurnStartAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockPlugin.Setup(p => p.OnTurnEndAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _pluginManager.Register(_mockPlugin.Object);
    }

    // ── OnTurnStart ──

    [TestMethod]
    public async Task ChatAsync_WithPluginManager_CallsOnTurnStart()
    {
        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance, pluginManager: _pluginManager);
        var session = new Session { Id = "pm-sess-1" };

        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("response");

        await agent.ChatAsync("hello", session, CancellationToken.None);

        _mockPlugin.Verify(
            p => p.OnTurnStartAsync(It.IsAny<int>(), "hello", It.IsAny<CancellationToken>()),
            Times.Once,
            "OnTurnStartAsync should be called once per ChatAsync invocation");
    }

    [TestMethod]
    public async Task ChatAsync_WithPluginManager_PassesTurnNumberFromSessionMessageCount()
    {
        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance, pluginManager: _pluginManager);
        var session = new Session { Id = "pm-sess-turn" };
        session.AddMessage(new Message { Role = "user", Content = "prior" });
        session.AddMessage(new Message { Role = "assistant", Content = "prior reply" });

        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ok");

        await agent.ChatAsync("new msg", session, CancellationToken.None);

        // Turn number equals session.Messages.Count at the time of calling — 2 prior messages
        _mockPlugin.Verify(
            p => p.OnTurnStartAsync(2, "new msg", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── OnTurnEnd ──

    [TestMethod]
    public async Task ChatAsync_WithPluginManager_CallsOnTurnEnd()
    {
        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance, pluginManager: _pluginManager);
        var session = new Session { Id = "pm-sess-2" };

        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("plugin response");

        await agent.ChatAsync("trigger", session, CancellationToken.None);

        _mockPlugin.Verify(
            p => p.OnTurnEndAsync("trigger", "plugin response", session.Id, It.IsAny<CancellationToken>()),
            Times.Once,
            "OnTurnEndAsync should be called with the user message and response");
    }

    [TestMethod]
    public async Task ChatAsync_WithPluginManager_OnTurnEnd_ReceivesCorrectSessionId()
    {
        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance, pluginManager: _pluginManager);
        var session = new Session { Id = "specific-session-id-xyz" };

        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("done");

        await agent.ChatAsync("msg", session, CancellationToken.None);

        _mockPlugin.Verify(
            p => p.OnTurnEndAsync(It.IsAny<string>(), It.IsAny<string>(), "specific-session-id-xyz", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── System prompt injection ──

    [TestMethod]
    public async Task ChatAsync_WithPluginManager_InjectsSystemPromptBlock()
    {
        _mockPlugin.Setup(p => p.GetSystemPromptBlockAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("You have memory context.");

        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance, pluginManager: _pluginManager);
        var session = new Session { Id = "pm-sess-3" };

        // Snapshot at callback time. We changed Agent.ChatAsync to pop transient
        // system messages in finally so they don't accumulate across turns —
        // capturing the live reference and materializing it later observes
        // post-cleanup state, which would falsely fail. What we actually care
        // about is what got SENT to the LLM, which is the snapshot at the call site.
        List<Message>? captured = null;
        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Message>, CancellationToken>((msgs, _) => captured = msgs.ToList())
            .ReturnsAsync("ok");

        await agent.ChatAsync("question", session, CancellationToken.None);

        Assert.IsNotNull(captured);
        Assert.IsTrue(captured.Any(m => m.Role == "system"),
            "A system message from the plugin should have been sent to the LLM");
    }

    [TestMethod]
    public async Task ChatAsync_WithPluginManager_SystemPromptBlock_ContainsPluginContent()
    {
        _mockPlugin.Setup(p => p.GetSystemPromptBlockAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("custom plugin content here");

        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance, pluginManager: _pluginManager);
        var session = new Session { Id = "pm-content-check" };

        // See sibling test for the "snapshot at callback time" rationale.
        List<Message>? captured = null;
        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Message>, CancellationToken>((msgs, _) => captured = msgs.ToList())
            .ReturnsAsync("ok");

        await agent.ChatAsync("q", session, CancellationToken.None);

        Assert.IsNotNull(captured);
        var sysMsg = captured.FirstOrDefault(m => m.Role == "system");
        Assert.IsNotNull(sysMsg);
        StringAssert.Contains(sysMsg.Content, "custom plugin content here");
    }

    [TestMethod]
    public async Task ChatAsync_WithPluginManager_NullSystemPromptBlock_NothingInjected()
    {
        _mockPlugin.Setup(p => p.GetSystemPromptBlockAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance, pluginManager: _pluginManager);
        var session = new Session { Id = "pm-sess-null-block" };

        IEnumerable<Message>? captured = null;
        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Message>, CancellationToken>((msgs, _) => captured = msgs)
            .ReturnsAsync("ok");

        await agent.ChatAsync("hi", session, CancellationToken.None);

        Assert.IsNotNull(captured);
        var messages = captured.ToList();
        Assert.IsFalse(messages.Any(m => m.Role == "system"),
            "No system message should be injected when plugin returns null block");
    }

    [TestMethod]
    public async Task ChatAsync_WithPluginManager_EmptySystemPromptBlock_NothingInjected()
    {
        _mockPlugin.Setup(p => p.GetSystemPromptBlockAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("");

        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance, pluginManager: _pluginManager);
        var session = new Session { Id = "pm-sess-4" };

        IEnumerable<Message>? captured = null;
        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Message>, CancellationToken>((msgs, _) => captured = msgs)
            .ReturnsAsync("ok");

        await agent.ChatAsync("hi", session, CancellationToken.None);

        Assert.IsNotNull(captured);
        var messages = captured.ToList();
        Assert.IsFalse(messages.Any(m => m.Role == "system"),
            "No system message should be injected for an empty block");
    }

    [TestMethod]
    public async Task ChatAsync_WithPluginManager_WhitespaceSystemPromptBlock_NothingInjected()
    {
        _mockPlugin.Setup(p => p.GetSystemPromptBlockAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("   ");

        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance, pluginManager: _pluginManager);
        var session = new Session { Id = "pm-ws-block" };

        IEnumerable<Message>? captured = null;
        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Message>, CancellationToken>((msgs, _) => captured = msgs)
            .ReturnsAsync("ok");

        await agent.ChatAsync("hi", session, CancellationToken.None);

        Assert.IsNotNull(captured);
        Assert.IsFalse(captured.Any(m => m.Role == "system"),
            "No system message should be injected for a whitespace-only block");
    }

    // ── Exception isolation ──

    [TestMethod]
    public async Task ChatAsync_PluginOnTurnStartThrows_DoesNotCrashAgent()
    {
        _mockPlugin.Setup(p => p.OnTurnStartAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Plugin exploded"));

        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance, pluginManager: _pluginManager);
        var session = new Session { Id = "pm-sess-5" };

        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("still works");

        var result = await agent.ChatAsync("msg", session, CancellationToken.None);

        Assert.AreEqual("still works", result, "Agent should complete successfully even when plugin throws");
    }

    [TestMethod]
    public async Task ChatAsync_PluginSystemPromptThrows_DoesNotCrashAgent()
    {
        _mockPlugin.Setup(p => p.GetSystemPromptBlockAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Prompt block failed"));

        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance, pluginManager: _pluginManager);
        var session = new Session { Id = "pm-sess-6" };

        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("still ok");

        var result = await agent.ChatAsync("msg", session, CancellationToken.None);

        Assert.AreEqual("still ok", result);
    }

    [TestMethod]
    public async Task ChatAsync_PluginOnTurnEndThrows_DoesNotCrashAgent()
    {
        _mockPlugin.Setup(p => p.OnTurnEndAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("End hook failed"));

        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance, pluginManager: _pluginManager);
        var session = new Session { Id = "pm-sess-7" };

        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("completed");

        var result = await agent.ChatAsync("msg", session, CancellationToken.None);

        Assert.AreEqual("completed", result);
    }

    // ── No plugin manager ──

    [TestMethod]
    public async Task ChatAsync_NoPluginManager_StillCompletesSuccessfully()
    {
        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance);
        var session = new Session { Id = "pm-sess-8" };

        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("no plugins needed");

        var result = await agent.ChatAsync("msg", session, CancellationToken.None);

        Assert.AreEqual("no plugins needed", result);
    }

    [TestMethod]
    public async Task ChatAsync_NoPluginManager_NoSystemMessageInjected()
    {
        // Without a plugin manager, no system message should be inserted from plugins
        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance);
        var session = new Session { Id = "no-pm-sys" };

        IEnumerable<Message>? captured = null;
        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Message>, CancellationToken>((msgs, _) => captured = msgs)
            .ReturnsAsync("done");

        await agent.ChatAsync("hi", session, CancellationToken.None);

        Assert.IsNotNull(captured);
        Assert.IsFalse(captured.Any(m => m.Role == "system"),
            "No system message should be present without a plugin manager");
    }

    // ── Multiple plugins ──

    [TestMethod]
    public async Task ChatAsync_MultiplePlugins_AllHooksCalledOnce()
    {
        var plugin2 = new Mock<IPlugin>();
        plugin2.Setup(p => p.Name).Returns("plugin-2");
        plugin2.Setup(p => p.IsBuiltin).Returns(true);
        plugin2.Setup(p => p.Category).Returns("tools");
        plugin2.Setup(p => p.GetTools()).Returns(Array.Empty<ITool>());
        plugin2.Setup(p => p.GetSystemPromptBlockAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        plugin2.Setup(p => p.OnTurnStartAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        plugin2.Setup(p => p.OnTurnEndAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _pluginManager.Register(plugin2.Object);

        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance, pluginManager: _pluginManager);
        var session = new Session { Id = "multi-plugin-sess" };

        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("done");

        await agent.ChatAsync("msg", session, CancellationToken.None);

        _mockPlugin.Verify(p => p.OnTurnStartAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        plugin2.Verify(p => p.OnTurnStartAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockPlugin.Verify(p => p.OnTurnEndAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        plugin2.Verify(p => p.OnTurnEndAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}

/// <summary>
/// Tests for Agent.ActivityLog — tracking tool execution events.
/// </summary>
[TestClass]
public class AgentActivityLogTests
{
    private Mock<IChatClient> _mockChatClient = null!;
    private Agent _agent = null!;

    [TestInitialize]
    public void SetUp()
    {
        _mockChatClient = new Mock<IChatClient>(MockBehavior.Strict);
        _agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance);
    }

    // ── Initial state ──

    [TestMethod]
    public void ActivityLog_InitiallyEmpty()
    {
        Assert.AreEqual(0, _agent.ActivityLog.Count);
    }

    // ── ClearActivityLog ──

    [TestMethod]
    public void ClearActivityLog_EmptiesLog()
    {
        _agent.ActivityLog.Add(new ActivityEntry
        {
            ToolName = "some_tool",
            Status = ActivityStatus.Success
        });

        Assert.AreEqual(1, _agent.ActivityLog.Count);

        _agent.ClearActivityLog();

        Assert.AreEqual(0, _agent.ActivityLog.Count);
    }

    [TestMethod]
    public void ClearActivityLog_OnAlreadyEmptyLog_DoesNotThrow()
    {
        _agent.ClearActivityLog();
        Assert.AreEqual(0, _agent.ActivityLog.Count);
    }

    [TestMethod]
    public void ClearActivityLog_MultipleEntries_RemovesAll()
    {
        for (int i = 0; i < 5; i++)
        {
            _agent.ActivityLog.Add(new ActivityEntry
            {
                ToolName = $"tool_{i}",
                Status = ActivityStatus.Success
            });
        }
        Assert.AreEqual(5, _agent.ActivityLog.Count);

        _agent.ClearActivityLog();

        Assert.AreEqual(0, _agent.ActivityLog.Count);
    }

    [TestMethod]
    public void ClearActivityLog_IsIdempotent()
    {
        _agent.ActivityLog.Add(new ActivityEntry { ToolName = "t", Status = ActivityStatus.Success });

        _agent.ClearActivityLog();
        _agent.ClearActivityLog();

        Assert.AreEqual(0, _agent.ActivityLog.Count);
    }

    // ── ActivityEntryAdded event ──

    [TestMethod]
    public async Task ActivityEntryAdded_Event_FiredDuringToolExecution()
    {
        var tool = new Mock<ITool>();
        tool.Setup(t => t.Name).Returns("event_tool");
        tool.Setup(t => t.Description).Returns("Fires an event");
        tool.Setup(t => t.ParametersType).Returns(typeof(EmptyToolParams));
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("done"));

        _agent.RegisterTool(tool.Object);

        var session = new Session { Id = "activity-sess" };

        var toolCall = new ToolCall { Id = "call-1", Name = "event_tool", Arguments = "{}" };
        var callSequence = new Queue<ChatResponse>(new[]
        {
            new ChatResponse { Content = null, ToolCalls = new List<ToolCall> { toolCall }, FinishReason = "tool_calls" },
            new ChatResponse { Content = "finished", FinishReason = "stop" }
        });

        _mockChatClient
            .Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => callSequence.Dequeue());

        var receivedEntries = new List<ActivityEntry>();
        _agent.ActivityEntryAdded += entry => receivedEntries.Add(entry);

        await _agent.ChatAsync("run tool", session, CancellationToken.None);

        Assert.IsTrue(receivedEntries.Count > 0, "ActivityEntryAdded should have fired at least once");
        Assert.IsTrue(receivedEntries.Any(e => e.ToolName == "event_tool"),
            "Entry for event_tool should have been fired");
    }

    [TestMethod]
    public async Task ActivityEntryAdded_FiredWithRunningStatus_BeforeExecution()
    {
        var executionStarted = false;
        ActivityStatus? statusAtFirstFire = null;

        var tool = new Mock<ITool>();
        tool.Setup(t => t.Name).Returns("status_probe_tool");
        tool.Setup(t => t.Description).Returns("Probe");
        tool.Setup(t => t.ParametersType).Returns(typeof(EmptyToolParams));
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback(() => executionStarted = true)
            .ReturnsAsync(ToolResult.Ok("ok"));

        _agent.RegisterTool(tool.Object);

        _agent.ActivityEntryAdded += entry =>
        {
            if (!executionStarted && statusAtFirstFire is null)
                statusAtFirstFire = entry.Status;
        };

        var session = new Session { Id = "status-probe-sess" };
        var toolCall = new ToolCall { Id = "call-probe", Name = "status_probe_tool", Arguments = "{}" };
        var callSequence = new Queue<ChatResponse>(new[]
        {
            new ChatResponse { Content = null, ToolCalls = new List<ToolCall> { toolCall }, FinishReason = "tool_calls" },
            new ChatResponse { Content = "done", FinishReason = "stop" }
        });

        _mockChatClient
            .Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => callSequence.Dequeue());

        await _agent.ChatAsync("run", session, CancellationToken.None);

        Assert.AreEqual(ActivityStatus.Running, statusAtFirstFire,
            "First ActivityEntryAdded fire should have Running status");
    }

    // ── ActivityLog persistence after tool execution ──

    [TestMethod]
    public async Task ActivityLog_AfterToolExecution_ContainsEntry()
    {
        var tool = new Mock<ITool>();
        tool.Setup(t => t.Name).Returns("log_tool");
        tool.Setup(t => t.Description).Returns("Logged tool");
        tool.Setup(t => t.ParametersType).Returns(typeof(EmptyToolParams));
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("result content"));

        _agent.RegisterTool(tool.Object);

        var session = new Session { Id = "log-sess" };
        var toolCall = new ToolCall { Id = "call-log", Name = "log_tool", Arguments = "{}" };

        var callSequence = new Queue<ChatResponse>(new[]
        {
            new ChatResponse { Content = null, ToolCalls = new List<ToolCall> { toolCall }, FinishReason = "tool_calls" },
            new ChatResponse { Content = "done", FinishReason = "stop" }
        });

        _mockChatClient
            .Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => callSequence.Dequeue());

        await _agent.ChatAsync("run", session, CancellationToken.None);

        Assert.IsTrue(_agent.ActivityLog.Any(e => e.ToolName == "log_tool"),
            "ActivityLog should contain an entry for the executed tool");
    }

    [TestMethod]
    public async Task ActivityLog_SuccessfulTool_HasSuccessStatus()
    {
        var tool = new Mock<ITool>();
        tool.Setup(t => t.Name).Returns("success_tool");
        tool.Setup(t => t.Description).Returns("Always succeeds");
        tool.Setup(t => t.ParametersType).Returns(typeof(EmptyToolParams));
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("success!"));

        _agent.RegisterTool(tool.Object);

        var session = new Session { Id = "status-sess" };
        var toolCall = new ToolCall { Id = "call-s", Name = "success_tool", Arguments = "{}" };

        var callSequence = new Queue<ChatResponse>(new[]
        {
            new ChatResponse { Content = null, ToolCalls = new List<ToolCall> { toolCall }, FinishReason = "tool_calls" },
            new ChatResponse { Content = "done", FinishReason = "stop" }
        });

        _mockChatClient
            .Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => callSequence.Dequeue());

        await _agent.ChatAsync("go", session, CancellationToken.None);

        var entry = _agent.ActivityLog.First(e => e.ToolName == "success_tool");
        Assert.AreEqual(ActivityStatus.Success, entry.Status);
    }

    [TestMethod]
    public async Task ActivityLog_FailedTool_HasFailedStatus()
    {
        var tool = new Mock<ITool>();
        tool.Setup(t => t.Name).Returns("fail_tool");
        tool.Setup(t => t.Description).Returns("Always fails");
        tool.Setup(t => t.ParametersType).Returns(typeof(EmptyToolParams));
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Fail("something went wrong"));

        _agent.RegisterTool(tool.Object);

        var session = new Session { Id = "fail-sess" };
        var toolCall = new ToolCall { Id = "call-f", Name = "fail_tool", Arguments = "{}" };

        var callSequence = new Queue<ChatResponse>(new[]
        {
            new ChatResponse { Content = null, ToolCalls = new List<ToolCall> { toolCall }, FinishReason = "tool_calls" },
            new ChatResponse { Content = "done despite failure", FinishReason = "stop" }
        });

        _mockChatClient
            .Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => callSequence.Dequeue());

        await _agent.ChatAsync("go", session, CancellationToken.None);

        var entry = _agent.ActivityLog.First(e => e.ToolName == "fail_tool");
        Assert.AreEqual(ActivityStatus.Failed, entry.Status);
    }

    [TestMethod]
    public async Task ActivityLog_EntryHasToolCallId()
    {
        var tool = new Mock<ITool>();
        tool.Setup(t => t.Name).Returns("id_tool");
        tool.Setup(t => t.Description).Returns("Check call ID");
        tool.Setup(t => t.ParametersType).Returns(typeof(EmptyToolParams));
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("ok"));

        _agent.RegisterTool(tool.Object);

        var session = new Session { Id = "id-sess" };
        var toolCall = new ToolCall { Id = "unique-call-id-123", Name = "id_tool", Arguments = "{}" };

        var callSequence = new Queue<ChatResponse>(new[]
        {
            new ChatResponse { Content = null, ToolCalls = new List<ToolCall> { toolCall }, FinishReason = "tool_calls" },
            new ChatResponse { Content = "done", FinishReason = "stop" }
        });

        _mockChatClient
            .Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => callSequence.Dequeue());

        await _agent.ChatAsync("run", session, CancellationToken.None);

        // Note: Agent normalizes tool call IDs, so verify the tool name is correct
        var entry = _agent.ActivityLog.First(e => e.ToolName == "id_tool");
        Assert.IsNotNull(entry.ToolCallId, "ActivityEntry should have a ToolCallId");
    }

    [TestMethod]
    public async Task ActivityLog_TwoTools_BothEntiesPresent()
    {
        var tool1 = new Mock<ITool>();
        tool1.Setup(t => t.Name).Returns("alpha_tool");
        tool1.Setup(t => t.Description).Returns("Alpha");
        tool1.Setup(t => t.ParametersType).Returns(typeof(EmptyToolParams));
        tool1.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("a"));

        var tool2 = new Mock<ITool>();
        tool2.Setup(t => t.Name).Returns("beta_tool");
        tool2.Setup(t => t.Description).Returns("Beta");
        tool2.Setup(t => t.ParametersType).Returns(typeof(EmptyToolParams));
        tool2.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("b"));

        _agent.RegisterTool(tool1.Object);
        _agent.RegisterTool(tool2.Object);

        var session = new Session { Id = "two-tool-sess" };
        var call1 = new ToolCall { Id = "c1", Name = "alpha_tool", Arguments = "{}" };
        var call2 = new ToolCall { Id = "c2", Name = "beta_tool", Arguments = "{}" };

        var callSequence = new Queue<ChatResponse>(new[]
        {
            new ChatResponse { Content = null, ToolCalls = new List<ToolCall> { call1, call2 }, FinishReason = "tool_calls" },
            new ChatResponse { Content = "all done", FinishReason = "stop" }
        });

        _mockChatClient
            .Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => callSequence.Dequeue());

        await _agent.ChatAsync("run both", session, CancellationToken.None);

        Assert.IsTrue(_agent.ActivityLog.Any(e => e.ToolName == "alpha_tool"),
            "alpha_tool entry should be in ActivityLog");
        Assert.IsTrue(_agent.ActivityLog.Any(e => e.ToolName == "beta_tool"),
            "beta_tool entry should be in ActivityLog");
    }

    // ── PermissionPromptCallback regression tests ──
    //
    // These pin the contract that PermissionBehavior.Ask invokes
    // PermissionPromptCallback with the toolCall.Arguments string as the
    // third parameter. The PR that added the third argument (audit-the-
    // command-before-running fix for v2.3.1 user feedback #1) is the only
    // path through which the desktop UI sees what's about to execute, so a
    // regression that silently drops the parameter would re-hide command
    // auditing without any compile error.

    [TestMethod]
    public async Task PermissionPromptCallback_OnAsk_ReceivesToolNameMessageAndArguments()
    {
        // PermissionContext.Mode = Default → CheckPermissionsAsync returns
        // Ask("Default: requires permission") for every tool that doesn't
        // match an always_allow / always_deny / always_ask rule. That is the
        // simplest way to force the Ask path without depending on rule DSL.
        var permissions = new Hermes.Agent.Permissions.PermissionManager(
            new Hermes.Agent.Permissions.PermissionContext
            {
                Mode = Hermes.Agent.Permissions.PermissionMode.Default
            },
            NullLogger<Hermes.Agent.Permissions.PermissionManager>.Instance);

        var agent = new Agent(
            _mockChatClient.Object,
            NullLogger<Agent>.Instance,
            permissions: permissions);

        var tool = CreateMockTool("audited_tool");
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("ran"));
        agent.RegisterTool(tool.Object);

        // The exact arguments string the prompt UI must surface.
        const string expectedArgs = "{\"command\":\"whoami\"}";

        string? capturedToolName = null;
        string? capturedMessage = null;
        string? capturedToolArguments = null;
        agent.PermissionPromptCallback = (toolName, message, toolArguments) =>
        {
            capturedToolName = toolName;
            capturedMessage = message;
            capturedToolArguments = toolArguments;
            return Task.FromResult(true); // allow so the loop completes
        };

        var toolCall = new ToolCall
        {
            Id = "call-audit",
            Name = "audited_tool",
            Arguments = expectedArgs
        };
        var callSequence = new Queue<ChatResponse>(new[]
        {
            new ChatResponse { Content = null, ToolCalls = new List<ToolCall> { toolCall }, FinishReason = "tool_calls" },
            new ChatResponse { Content = "done", FinishReason = "stop" }
        });

        _mockChatClient
            .Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => callSequence.Dequeue());

        await agent.ChatAsync("run audited tool", new Session { Id = "audit-sess" }, CancellationToken.None);

        Assert.AreEqual("audited_tool", capturedToolName,
            "Callback should receive the literal tool name being prompted for.");
        Assert.IsFalse(string.IsNullOrEmpty(capturedMessage),
            "Callback should receive a non-empty permission message.");
        Assert.AreEqual(expectedArgs, capturedToolArguments,
            "Callback must receive the raw toolCall.Arguments JSON so the UI can " +
            "show the user the actual command before they approve. Regression in " +
            "this assertion means the agent stopped forwarding the third parameter " +
            "and the desktop dialog will silently fall back to hiding the command.");
    }

    [TestMethod]
    public async Task PermissionPromptCallback_WhenCallbackDenies_ToolIsNotExecuted()
    {
        // Sanity check the deny path — the callback returning false should
        // skip ExecuteAsync entirely and inject a denial tool message.
        var permissions = new Hermes.Agent.Permissions.PermissionManager(
            new Hermes.Agent.Permissions.PermissionContext
            {
                Mode = Hermes.Agent.Permissions.PermissionMode.Default
            },
            NullLogger<Hermes.Agent.Permissions.PermissionManager>.Instance);

        var agent = new Agent(
            _mockChatClient.Object,
            NullLogger<Agent>.Instance,
            permissions: permissions);

        var executed = false;
        var tool = CreateMockTool("denied_tool");
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback(() => executed = true)
            .ReturnsAsync(ToolResult.Ok("should not run"));
        agent.RegisterTool(tool.Object);

        agent.PermissionPromptCallback = (_, _, _) => Task.FromResult(false);

        var toolCall = new ToolCall { Id = "call-deny", Name = "denied_tool", Arguments = "{}" };
        var callSequence = new Queue<ChatResponse>(new[]
        {
            new ChatResponse { Content = null, ToolCalls = new List<ToolCall> { toolCall }, FinishReason = "tool_calls" },
            new ChatResponse { Content = "stopped", FinishReason = "stop" }
        });

        _mockChatClient
            .Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => callSequence.Dequeue());

        await agent.ChatAsync("try denied tool", new Session { Id = "deny-sess" }, CancellationToken.None);

        Assert.IsFalse(executed,
            "Tool ExecuteAsync should not have been invoked when the permission callback returned false.");
        Assert.IsTrue(agent.ActivityLog.Any(e => e.ToolName == "denied_tool" && e.Status == ActivityStatus.Denied),
            "ActivityLog should contain a Denied entry for the user-denied tool call.");
    }

    private static Mock<ITool> CreateMockTool(string name)
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Name).Returns(name);
        mock.Setup(t => t.Description).Returns($"Description of {name}");
        mock.Setup(t => t.ParametersType).Returns(typeof(EmptyToolParams));
        return mock;
    }

    private sealed class EmptyToolParams { }
}

/// <summary>
/// Tests for Agent.StreamChatAsync — streaming tool-calling loop.
/// </summary>
[TestClass]
public class AgentStreamChatTests
{
    private StubStreamingChatClient _stubClient = null!;
    private Agent _agent = null!;

    [TestInitialize]
    public void SetUp()
    {
        _stubClient = new StubStreamingChatClient();
        _agent = new Agent(_stubClient, NullLogger<Agent>.Instance);
    }

    // ── No tools — direct streaming path ──

    [TestMethod]
    public async Task StreamChatAsync_NoTools_YieldsTokens()
    {
        _stubClient.StreamEvents = new StreamEvent[]
        {
            new StreamEvent.TokenDelta("Hello"),
            new StreamEvent.TokenDelta(" world")
        };

        var session = new Session { Id = "stream-1" };
        var events = new List<StreamEvent>();

        await foreach (var evt in _agent.StreamChatAsync("hi", session, CancellationToken.None))
            events.Add(evt);

        var tokens = events.OfType<StreamEvent.TokenDelta>().Select(t => t.Text).ToList();
        CollectionAssert.Contains(tokens, "Hello");
        CollectionAssert.Contains(tokens, " world");
    }

    [TestMethod]
    public async Task StreamChatAsync_NoTools_AddsUserMessageToSession()
    {
        _stubClient.StreamEvents = new StreamEvent[] { new StreamEvent.TokenDelta("response") };

        var session = new Session { Id = "stream-2" };

        await foreach (var _ in _agent.StreamChatAsync("my message", session, CancellationToken.None)) { }

        Assert.IsTrue(session.Messages.Any(m => m.Role == "user" && m.Content == "my message"),
            "User message should be added to session");
    }

    [TestMethod]
    public async Task StreamChatAsync_NoTools_AddsAssistantMessageToSession()
    {
        _stubClient.StreamEvents = new StreamEvent[]
        {
            new StreamEvent.TokenDelta("assistant"),
            new StreamEvent.TokenDelta(" reply")
        };

        var session = new Session { Id = "stream-3" };

        await foreach (var _ in _agent.StreamChatAsync("prompt", session, CancellationToken.None)) { }

        Assert.IsTrue(session.Messages.Any(m => m.Role == "assistant" && m.Content == "assistant reply"),
            "Accumulated response should be saved as assistant message");
    }

    [TestMethod]
    public async Task StreamChatAsync_NoTools_EmptyResponse_SavesEmptyAssistantMessage()
    {
        _stubClient.StreamEvents = Array.Empty<StreamEvent>();

        var session = new Session { Id = "stream-5" };

        await foreach (var _ in _agent.StreamChatAsync("question", session, CancellationToken.None)) { }

        Assert.IsTrue(session.Messages.Any(m => m.Role == "assistant"),
            "An assistant message should be saved even if streaming yielded no tokens");
    }

    [TestMethod]
    public async Task StreamChatAsync_NoTools_AssistantMessageIsEmptyString_WhenNoTokens()
    {
        _stubClient.StreamEvents = Array.Empty<StreamEvent>();

        var session = new Session { Id = "stream-empty-content" };

        await foreach (var _ in _agent.StreamChatAsync("q", session, CancellationToken.None)) { }

        var assistantMsg = session.Messages.FirstOrDefault(m => m.Role == "assistant");
        Assert.IsNotNull(assistantMsg);
        Assert.AreEqual("", assistantMsg.Content);
    }

    [TestMethod]
    public async Task StreamChatAsync_NoTools_NonTokenEvents_ArePassedThrough()
    {
        _stubClient.StreamEvents = new StreamEvent[]
        {
            new StreamEvent.TokenDelta("text"),
            new StreamEvent.MessageComplete("stop")
        };

        var session = new Session { Id = "stream-events" };
        var events = new List<StreamEvent>();

        await foreach (var evt in _agent.StreamChatAsync("msg", session, CancellationToken.None))
            events.Add(evt);

        Assert.IsTrue(events.Any(e => e is StreamEvent.MessageComplete),
            "Non-token events like MessageComplete should be yielded");
    }

    [TestMethod]
    public async Task StreamChatAsync_NoTools_ProviderIdleTimeout_YieldsStructuredError()
    {
        _agent.StreamWatchdogTimeout = TimeSpan.FromMilliseconds(25);
        _stubClient.DelayBeforeFirstEvent = TimeSpan.FromMilliseconds(250);
        _stubClient.StreamEvents = new StreamEvent[] { new StreamEvent.TokenDelta("late") };

        var session = new Session { Id = "stream-watchdog" };
        var events = new List<StreamEvent>();

        await foreach (var evt in _agent.StreamChatAsync("msg", session, CancellationToken.None))
            events.Add(evt);

        var error = events.OfType<StreamEvent.StreamError>().SingleOrDefault();
        Assert.IsNotNull(error);
        Assert.AreEqual(ProviderErrorCode.ProviderTimeout, error.Code);
        Assert.IsFalse(events.OfType<StreamEvent.TokenDelta>().Any(t => t.Text == "late"));
    }

    // ── With registered tools — tool loop path ──

    [TestMethod]
    public async Task StreamChatAsync_WithRegisteredTools_UsesCompleteWithToolsForToolLoop()
    {
        var tool = new Mock<ITool>();
        tool.Setup(t => t.Name).Returns("stream_tool");
        tool.Setup(t => t.Description).Returns("A streamed tool");
        tool.Setup(t => t.ParametersType).Returns(typeof(StreamEmptyParams));
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("tool output"));

        _agent.RegisterTool(tool.Object);

        var toolCall = new ToolCall { Id = "sc-call-1", Name = "stream_tool", Arguments = "{}" };

        var callCount = 0;
        _stubClient.OnCompleteWithTools = (msgs, tools, ct) =>
        {
            callCount++;
            if (callCount == 1)
                return Task.FromResult(new ChatResponse { ToolCalls = new List<ToolCall> { toolCall }, FinishReason = "tool_calls" });
            return Task.FromResult(new ChatResponse { Content = "stream final", FinishReason = "stop" });
        };

        var session = new Session { Id = "stream-4" };
        var tokenTexts = new List<string>();

        await foreach (var evt in _agent.StreamChatAsync("run tool", session, CancellationToken.None))
        {
            if (evt is StreamEvent.TokenDelta td)
                tokenTexts.Add(td.Text);
        }

        Assert.IsTrue(tokenTexts.Any(t => t.Contains("stream final")),
            "Final response text should be yielded as a TokenDelta");
        Assert.AreEqual(2, callCount, "CompleteWithToolsAsync should be called twice (tool loop + final)");
    }

    [TestMethod]
    public async Task StreamChatAsync_WithTools_AddsUserMessageToSession()
    {
        var tool = new Mock<ITool>();
        tool.Setup(t => t.Name).Returns("user_msg_tool");
        tool.Setup(t => t.Description).Returns("Check user msg");
        tool.Setup(t => t.ParametersType).Returns(typeof(StreamEmptyParams));
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("ok"));

        _agent.RegisterTool(tool.Object);

        _stubClient.OnCompleteWithTools = (_, _, _) =>
            Task.FromResult(new ChatResponse { Content = "final", FinishReason = "stop" });

        var session = new Session { Id = "stream-user-msg" };

        await foreach (var _ in _agent.StreamChatAsync("check this", session, CancellationToken.None)) { }

        Assert.IsTrue(session.Messages.Any(m => m.Role == "user" && m.Content == "check this"),
            "User message should be added to session when tools are registered");
    }

    [TestMethod]
    public async Task StreamChatAsync_WithTools_ToolExecuted_AddedToSession()
    {
        var tool = new Mock<ITool>();
        tool.Setup(t => t.Name).Returns("sess_tool");
        tool.Setup(t => t.Description).Returns("Session tool");
        tool.Setup(t => t.ParametersType).Returns(typeof(StreamEmptyParams));
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("result here"));

        _agent.RegisterTool(tool.Object);

        var toolCall = new ToolCall { Id = "sc2", Name = "sess_tool", Arguments = "{}" };
        var callCount = 0;
        _stubClient.OnCompleteWithTools = (_, _, _) =>
        {
            callCount++;
            return callCount == 1
                ? Task.FromResult(new ChatResponse { ToolCalls = new List<ToolCall> { toolCall }, FinishReason = "tool_calls" })
                : Task.FromResult(new ChatResponse { Content = "done", FinishReason = "stop" });
        };

        var session = new Session { Id = "stream-sess-tool" };

        await foreach (var _ in _agent.StreamChatAsync("go", session, CancellationToken.None)) { }

        Assert.IsTrue(session.Messages.Any(m => m.Role == "tool"),
            "Tool result message should be added to session");
    }

    // ── Cancellation ──

    [TestMethod]
    public async Task StreamChatAsync_CancellationRequested_ThrowsOperationCanceled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _stubClient.ThrowOnStream = new OperationCanceledException();

        var session = new Session { Id = "stream-cancel" };

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in _agent.StreamChatAsync("msg", session, cts.Token)) { }
        });
    }

    private sealed class StreamEmptyParams { }

    /// <summary>Stub IChatClient with configurable streaming behavior.</summary>
    private sealed class StubStreamingChatClient : IChatClient
    {
        public IReadOnlyList<StreamEvent> StreamEvents { get; set; } = Array.Empty<StreamEvent>();
        public TimeSpan DelayBeforeFirstEvent { get; set; } = TimeSpan.Zero;
        public Exception? ThrowOnStream { get; set; }
        public Func<IEnumerable<Message>, IEnumerable<ToolDefinition>, CancellationToken, Task<ChatResponse>>? OnCompleteWithTools { get; set; }

        public Task<string> CompleteAsync(IEnumerable<Message> messages, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<ChatResponse> CompleteWithToolsAsync(
            IEnumerable<Message> messages,
            IEnumerable<ToolDefinition> tools,
            CancellationToken ct)
        {
            if (OnCompleteWithTools is not null)
                return OnCompleteWithTools(messages, tools, ct);
            return Task.FromResult(new ChatResponse { Content = "", FinishReason = "stop" });
        }

        public IAsyncEnumerable<string> StreamAsync(IEnumerable<Message> messages, CancellationToken ct)
            => throw new NotImplementedException();

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            string? systemPrompt,
            IEnumerable<Message> messages,
            IEnumerable<ToolDefinition>? tools = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (ThrowOnStream is not null)
                throw ThrowOnStream;

            if (DelayBeforeFirstEvent > TimeSpan.Zero)
                await Task.Delay(DelayBeforeFirstEvent, ct);

            foreach (var evt in StreamEvents)
            {
                ct.ThrowIfCancellationRequested();
                yield return evt;
            }
        }
    }
}
