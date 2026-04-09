using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace HermesDesktop.Tests.Services;

/// <summary>
/// Invariant harness for Agent.ChatAsync behavior.
/// These tests codify guarantees we must preserve while decomposing the agent loop.
/// </summary>
[TestClass]
public class AgentInvariantTests
{
    [TestMethod]
    public async Task ChatAsync_ToolLoop_PreservesMessageOrderingInvariant()
    {
        var chatClient = new Mock<IChatClient>(MockBehavior.Strict);
        var agent = new Agent(chatClient.Object, NullLogger<Agent>.Instance);
        var session = new Session { Id = "inv-order-1" };

        var tool = new Mock<ITool>(MockBehavior.Strict);
        tool.SetupGet(t => t.Name).Returns("echo_tool");
        tool.SetupGet(t => t.Description).Returns("Echo");
        tool.SetupGet(t => t.ParametersType).Returns(typeof(EmptyParams));
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("tool-output"));
        agent.RegisterTool(tool.Object);

        chatClient
            .SetupSequence(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse
            {
                Content = "running tool",
                ToolCalls = new List<ToolCall>
                {
                    new()
                    {
                        Id = "tc-1",
                        Name = "echo_tool",
                        Arguments = "{}"
                    }
                },
                FinishReason = "tool_calls"
            })
            .ReturnsAsync(new ChatResponse
            {
                Content = "done",
                FinishReason = "stop",
                ToolCalls = null
            });

        var result = await agent.ChatAsync("hello", session, CancellationToken.None);

        Assert.AreEqual("done", result);
        Assert.AreEqual(4, session.Messages.Count);
        Assert.AreEqual("user", session.Messages[0].Role);
        Assert.AreEqual("assistant", session.Messages[1].Role);
        Assert.AreEqual("tool", session.Messages[2].Role);
        Assert.AreEqual("assistant", session.Messages[3].Role);
        Assert.AreEqual("tc-1", session.Messages[2].ToolCallId);
        Assert.AreEqual("echo_tool", session.Messages[2].ToolName);
    }

    [TestMethod]
    public async Task ChatAsync_UnknownToolCall_ProducesToolFailureInvariant()
    {
        var chatClient = new Mock<IChatClient>(MockBehavior.Strict);
        var agent = new Agent(chatClient.Object, NullLogger<Agent>.Instance);
        var session = new Session { Id = "inv-unknown-1" };

        // Register any tool so Agent enters tool-calling mode.
        var knownTool = new Mock<ITool>(MockBehavior.Strict);
        knownTool.SetupGet(t => t.Name).Returns("known_tool");
        knownTool.SetupGet(t => t.Description).Returns("Known");
        knownTool.SetupGet(t => t.ParametersType).Returns(typeof(EmptyParams));
        agent.RegisterTool(knownTool.Object);

        chatClient
            .SetupSequence(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse
            {
                Content = "attempting unknown tool",
                ToolCalls = new List<ToolCall>
                {
                    new()
                    {
                        Id = "tc-ghost",
                        Name = "ghost_tool",
                        Arguments = "{}"
                    }
                },
                FinishReason = "tool_calls"
            })
            .ReturnsAsync(new ChatResponse
            {
                Content = "completed",
                FinishReason = "stop",
                ToolCalls = null
            });

        var result = await agent.ChatAsync("run", session, CancellationToken.None);

        Assert.AreEqual("completed", result);
        Assert.IsTrue(
            session.Messages.Any(m =>
                m.Role == "tool" &&
                m.ToolName == "ghost_tool" &&
                m.Content.Contains("Unknown tool: ghost_tool", StringComparison.Ordinal)),
            "Unknown tool calls must be reflected back as tool failure messages.");
    }

    [TestMethod]
    public async Task ChatAsync_MaxToolIterations_EmitsFallbackAssistantInvariant()
    {
        var chatClient = new Mock<IChatClient>(MockBehavior.Strict);
        var agent = new Agent(chatClient.Object, NullLogger<Agent>.Instance)
        {
            MaxToolIterations = 1
        };
        var session = new Session { Id = "inv-max-1" };

        var tool = new Mock<ITool>(MockBehavior.Strict);
        tool.SetupGet(t => t.Name).Returns("loop_tool");
        tool.SetupGet(t => t.Description).Returns("Loop");
        tool.SetupGet(t => t.ParametersType).Returns(typeof(EmptyParams));
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("ok"));
        agent.RegisterTool(tool.Object);

        chatClient
            .Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse
            {
                Content = "continue",
                ToolCalls = new List<ToolCall>
                {
                    new()
                    {
                        Id = "tc-loop",
                        Name = "loop_tool",
                        Arguments = "{}"
                    }
                },
                FinishReason = "tool_calls"
            });

        var result = await agent.ChatAsync("loop", session, CancellationToken.None);

        Assert.IsTrue(
            result.StartsWith("I've reached the maximum number of tool call iterations.", StringComparison.Ordinal),
            "When max iterations is reached, Agent must emit the fallback completion message.");
        Assert.AreEqual("assistant", session.Messages[^1].Role);
        Assert.AreEqual(result, session.Messages[^1].Content);
        chatClient.Verify(c => c.CompleteWithToolsAsync(
            It.IsAny<IEnumerable<Message>>(),
            It.IsAny<IEnumerable<ToolDefinition>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class EmptyParams { }
}
