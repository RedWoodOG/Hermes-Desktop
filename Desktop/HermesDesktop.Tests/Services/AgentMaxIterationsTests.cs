using Hermes.Agent.Agents;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace HermesDesktop.Tests.Services;

/// <summary>
/// Regression tests for PR #45 — agent.max_turns configurability.
///
/// The core bug: Agent.MaxToolIterations was hardcoded to 25 while the UI
/// advertised a configurable "Max Turns" default of 90, so the setting
/// silently did nothing. These tests prove the loop actually respects the
/// configured bound and that the wiring surfaces from AgentContext and
/// AgentRequest correctly.
/// </summary>
[TestClass]
public class AgentMaxIterationsTests
{
    private Mock<IChatClient> _mockChatClient = null!;

    [TestInitialize]
    public void SetUp()
    {
        _mockChatClient = new Mock<IChatClient>(MockBehavior.Strict);
    }

    [TestMethod]
    public async Task Loop_StopsAfterMaxToolIterations()
    {
        // Always return a tool call — the loop would never terminate naturally.
        var tool = new Mock<ITool>();
        tool.Setup(t => t.Name).Returns("looper");
        tool.Setup(t => t.Description).Returns("loops forever");
        tool.Setup(t => t.ParametersType).Returns(typeof(EmptyParams));
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("ack"));

        _mockChatClient
            .Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ChatResponse
            {
                Content = "",
                FinishReason = "tool_calls",
                ToolCalls = new List<ToolCall>
                {
                    new ToolCall { Id = "call_x", Name = "looper", Arguments = "{}" }
                }
            });

        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance)
        {
            MaxToolIterations = 3
        };
        agent.RegisterTool(tool.Object);

        var session = new Session { Id = "iter-test" };
        var result = await agent.ChatAsync("start", session, CancellationToken.None);

        StringAssert.Contains(result, "maximum number of tool call iterations");
        _mockChatClient.Verify(c => c.CompleteWithToolsAsync(
            It.IsAny<IEnumerable<Message>>(),
            It.IsAny<IEnumerable<ToolDefinition>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [TestMethod]
    public async Task Loop_HigherLimit_LetsTaskComplete()
    {
        // Proves bumping MaxToolIterations lets a longer task finish.
        // 6 tool-call turns then a final text; default limit (25) would cover
        // this too, but we explicitly bump to 10 to prove it's honored.
        var tool = new Mock<ITool>();
        tool.Setup(t => t.Name).Returns("step");
        tool.Setup(t => t.Description).Returns("one step");
        tool.Setup(t => t.ParametersType).Returns(typeof(EmptyParams));
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("ok"));

        var turnsRemaining = 6;
        _mockChatClient
            .Setup(c => c.CompleteWithToolsAsync(
                It.IsAny<IEnumerable<Message>>(),
                It.IsAny<IEnumerable<ToolDefinition>>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (turnsRemaining-- > 0)
                {
                    return Task.FromResult(new ChatResponse
                    {
                        Content = "",
                        FinishReason = "tool_calls",
                        ToolCalls = new List<ToolCall>
                        {
                            new ToolCall { Id = $"call_{turnsRemaining}", Name = "step", Arguments = "{}" }
                        }
                    });
                }
                return Task.FromResult(new ChatResponse
                {
                    Content = "done",
                    FinishReason = "stop",
                    ToolCalls = null
                });
            });

        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance)
        {
            MaxToolIterations = 10
        };
        agent.RegisterTool(tool.Object);

        var session = new Session { Id = "iter-higher" };
        var result = await agent.ChatAsync("go", session, CancellationToken.None);

        Assert.AreEqual("done", result);
    }

    // ── AgentContext / AgentRequest wiring (for subagent paths) ──

    [TestMethod]
    public void AgentContext_MaxToolIterations_RoundTrips()
    {
        var ctx = new AgentContext
        {
            AgentId = "x",
            Prompt = "hello",
            Model = "any",
            WorkingDirectory = "/tmp",
            MaxToolIterations = 42,
        };

        Assert.AreEqual(42, ctx.MaxToolIterations);
    }

    [TestMethod]
    public void AgentContext_MaxToolIterations_DefaultsToNull()
    {
        var ctx = new AgentContext
        {
            AgentId = "x",
            Prompt = "hello",
            Model = "any",
            WorkingDirectory = "/tmp",
        };

        Assert.IsNull(ctx.MaxToolIterations);
    }

    [TestMethod]
    public void AgentRequest_MaxToolIterations_DefaultsToNull()
    {
        var req = new AgentRequest
        {
            Description = "x",
            Prompt = "y",
        };

        Assert.IsNull(req.MaxToolIterations);
    }

    private sealed class EmptyParams { }
}
