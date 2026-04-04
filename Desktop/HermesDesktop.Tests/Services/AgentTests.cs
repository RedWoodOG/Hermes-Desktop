using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

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

    // ── Helpers ──

    private static Mock<ITool> CreateMockTool(string name)
    {
        var mock = new Mock<ITool>();
        mock.Setup(t => t.Name).Returns(name);
        mock.Setup(t => t.Description).Returns($"Description of {name}");
        mock.Setup(t => t.ParametersType).Returns(typeof(EmptyParams));
        return mock;
    }

    /// <summary>Minimal parameter type used to satisfy Agent's schema builder.</summary>
    private sealed class EmptyParams { }
}