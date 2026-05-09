using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Hermes.Agent.Permissions;
using Hermes.Agent.Transcript;
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

    [TestMethod]
    public async Task ChatAsync_ToolLoop_RecordsToolLifecycleInTimeline()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "hermes-agent-timeline-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var timeline = new TimelineStore(tempDir);
            var chatClient = new Mock<IChatClient>(MockBehavior.Strict);
            var agent = new Agent(chatClient.Object, NullLogger<Agent>.Instance, timeline: timeline);
            var session = new Session { Id = "timeline-tool-1", Platform = "desktop" };
            var turn = await timeline.StartTurnAsync(session.Id, "desktop", "hello", null, null, null, CancellationToken.None);

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

            await agent.ChatAsync("hello", session, CancellationToken.None);

            var items = await timeline.LoadTurnItemsAsync(turn.TurnId, CancellationToken.None);
            var toolItems = items.Where(item => item.ToolCallId == "tc-1").ToList();

            Assert.AreEqual(3, toolItems.Count);
            Assert.AreEqual(TurnItemKind.ToolCall, toolItems[0].Kind);
            Assert.AreEqual(TurnItemStatus.Pending, toolItems[0].Status);
            Assert.AreEqual(TurnItemKind.ToolCall, toolItems[1].Kind);
            Assert.AreEqual(TurnItemStatus.Running, toolItems[1].Status);
            Assert.AreEqual(TurnItemKind.ToolResult, toolItems[2].Kind);
            Assert.AreEqual(TurnItemStatus.Completed, toolItems[2].Status);
            Assert.AreEqual("echo_tool", toolItems[2].ToolName);
            Assert.AreEqual("True", toolItems[2].Metadata["success"]);
            Assert.IsTrue(toolItems[2].Metadata.ContainsKey("durationMs"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChatAsync_UserDeniedTool_RecordsDeniedTimelineResultWithoutRunningTool()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "hermes-agent-timeline-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var timeline = new TimelineStore(tempDir);
            var permissions = new PermissionManager(
                new PermissionContext { Mode = PermissionMode.Default },
                NullLogger<PermissionManager>.Instance);
            var chatClient = new Mock<IChatClient>(MockBehavior.Strict);
            var agent = new Agent(chatClient.Object, NullLogger<Agent>.Instance, permissions: permissions, timeline: timeline);
            var session = new Session { Id = "timeline-denied-1", Platform = "desktop" };
            var turn = await timeline.StartTurnAsync(session.Id, "desktop", "deny it", null, null, null, CancellationToken.None);

            var tool = new Mock<ITool>(MockBehavior.Strict);
            tool.SetupGet(t => t.Name).Returns("denied_tool");
            tool.SetupGet(t => t.Description).Returns("Denied");
            tool.SetupGet(t => t.ParametersType).Returns(typeof(EmptyParams));
            agent.RegisterTool(tool.Object);
            agent.PermissionPromptCallback = (_, _, _) => Task.FromResult(false);

            chatClient
                .SetupSequence(c => c.CompleteWithToolsAsync(
                    It.IsAny<IEnumerable<Message>>(),
                    It.IsAny<IEnumerable<ToolDefinition>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse
                {
                    ToolCalls = new List<ToolCall>
                    {
                        new() { Id = "deny-1", Name = "denied_tool", Arguments = "{}" }
                    },
                    FinishReason = "tool_calls"
                })
                .ReturnsAsync(new ChatResponse { Content = "stopped", FinishReason = "stop" });

            await agent.ChatAsync("deny it", session, CancellationToken.None);

            var toolItems = (await timeline.LoadTurnItemsAsync(turn.TurnId, CancellationToken.None))
                .Where(item => item.ToolCallId == "deny-1")
                .ToList();

            Assert.AreEqual(2, toolItems.Count);
            Assert.AreEqual(TurnItemStatus.Pending, toolItems[0].Status);
            Assert.AreEqual(TurnItemKind.ToolResult, toolItems[1].Kind);
            Assert.AreEqual(TurnItemStatus.Failed, toolItems[1].Status);
            Assert.AreEqual("denied", toolItems[1].Metadata["phase"]);
            tool.Verify(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChatAsync_FailedTool_RecordsFailedTimelineResultWithRedactedContent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "hermes-agent-timeline-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var timeline = new TimelineStore(tempDir);
            var chatClient = new Mock<IChatClient>(MockBehavior.Strict);
            var agent = new Agent(chatClient.Object, NullLogger<Agent>.Instance, timeline: timeline);
            var session = new Session { Id = "timeline-failed-1", Platform = "desktop" };
            var turn = await timeline.StartTurnAsync(session.Id, "desktop", "run secret tool", null, null, null, CancellationToken.None);

            var tool = new Mock<ITool>(MockBehavior.Strict);
            tool.SetupGet(t => t.Name).Returns("secret_tool");
            tool.SetupGet(t => t.Description).Returns("Secret");
            tool.SetupGet(t => t.ParametersType).Returns(typeof(EmptyParams));
            var fakeSecret = string.Concat("sk", "-", "abcdefghijklmnopqrstuvwxyz");
            tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ToolResult.Fail($"failed with token {fakeSecret}"));
            agent.RegisterTool(tool.Object);

            chatClient
                .SetupSequence(c => c.CompleteWithToolsAsync(
                    It.IsAny<IEnumerable<Message>>(),
                    It.IsAny<IEnumerable<ToolDefinition>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse
                {
                    ToolCalls = new List<ToolCall>
                    {
                        new() { Id = "fail-1", Name = "secret_tool", Arguments = "{}" }
                    },
                    FinishReason = "tool_calls"
                })
                .ReturnsAsync(new ChatResponse { Content = "handled", FinishReason = "stop" });

            await agent.ChatAsync("run secret tool", session, CancellationToken.None);

            var toolItems = (await timeline.LoadTurnItemsAsync(turn.TurnId, CancellationToken.None))
                .Where(item => item.ToolCallId == "fail-1")
                .ToList();
            var resultItem = toolItems.Single(item => item.Kind == TurnItemKind.ToolResult);

            Assert.AreEqual(TurnItemStatus.Failed, resultItem.Status);
            Assert.AreEqual("False", resultItem.Metadata["success"]);
            Assert.IsTrue(resultItem.Metadata.ContainsKey("durationMs"));
            StringAssert.Contains(resultItem.ContentSummary, "[REDACTED]");
            Assert.IsFalse(resultItem.ContentSummary.Contains(fakeSecret, StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class EmptyParams { }
}
