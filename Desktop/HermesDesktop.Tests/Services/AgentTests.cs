using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Hermes.Agent.Plugins;
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

/// <summary>
/// Tests for Agent with PluginManager integration — added in this PR.
/// Verifies the plugin lifecycle hooks are invoked during ChatAsync and StreamChatAsync.
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

    // ── PluginManager integration in ChatAsync ──

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
    public async Task ChatAsync_WithPluginManager_InjectsSystemPromptBlock()
    {
        _mockPlugin.Setup(p => p.GetSystemPromptBlockAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("You have memory context.");

        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance, pluginManager: _pluginManager);
        var session = new Session { Id = "pm-sess-3" };

        IEnumerable<Message>? captured = null;
        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Message>, CancellationToken>((msgs, _) => captured = msgs)
            .ReturnsAsync("ok");

        await agent.ChatAsync("question", session, CancellationToken.None);

        Assert.IsNotNull(captured);
        var messages = captured.ToList();
        // The system prompt block should have been injected as a system message
        Assert.IsTrue(messages.Any(m => m.Role == "system"),
            "A system message from the plugin should have been inserted");
    }

    [TestMethod]
    public async Task ChatAsync_WithPluginManager_EmptySystemPromptBlock_NothingInjected()
    {
        _mockPlugin.Setup(p => p.GetSystemPromptBlockAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("");  // empty block

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
        // When block is empty/whitespace, no system message should be injected
        Assert.IsFalse(messages.Any(m => m.Role == "system"),
            "No system message should be injected for an empty block");
    }

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

        // Should not throw — plugin failures are isolated
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

        // Should not throw even when OnTurnEnd raises
        var result = await agent.ChatAsync("msg", session, CancellationToken.None);

        Assert.AreEqual("completed", result);
    }

    [TestMethod]
    public async Task ChatAsync_NoPluginManager_StillCompletesSuccessfully()
    {
        // Agent without pluginManager should work exactly as before
        var agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance);
        var session = new Session { Id = "pm-sess-8" };

        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("no plugins needed");

        var result = await agent.ChatAsync("msg", session, CancellationToken.None);

        Assert.AreEqual("no plugins needed", result);
    }
}

/// <summary>
/// Tests for Agent.ActivityLog — tracking tool execution events.
/// Added in this PR alongside the PluginManager integration.
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

    [TestMethod]
    public void ActivityLog_InitiallyEmpty()
    {
        Assert.AreEqual(0, _agent.ActivityLog.Count);
    }

    [TestMethod]
    public void ClearActivityLog_EmptiesLog()
    {
        // Manually add an activity entry to simulate tracking
        // (ActivityLog is a List<ActivityEntry> exposed publicly)
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
        // Should be idempotent
        _agent.ClearActivityLog();
        Assert.AreEqual(0, _agent.ActivityLog.Count);
    }

    [TestMethod]
    public async Task ActivityEntryAdded_Event_FiredDuringToolExecution()
    {
        // Register a tool that succeeds
        var tool = new Mock<ITool>();
        tool.Setup(t => t.Name).Returns("event_tool");
        tool.Setup(t => t.Description).Returns("Fires an event");
        tool.Setup(t => t.ParametersType).Returns(typeof(EmptyToolParams));
        tool.Setup(t => t.ExecuteAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolResult.Ok("done"));

        _agent.RegisterTool(tool.Object);

        var session = new Session { Id = "activity-sess" };

        // Setup: first call returns tool call, second call returns final response
        var toolCall = new ToolCall { Id = "call-1", Name = "event_tool", Arguments = "{}" };
        var callSequence = new Queue<ChatResponse>(new[]
        {
            new ChatResponse { Content = null, ToolCalls = [toolCall], FinishReason = "tool_calls" },
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
            new ChatResponse { Content = null, ToolCalls = [toolCall], FinishReason = "tool_calls" },
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
            new ChatResponse { Content = null, ToolCalls = [toolCall], FinishReason = "tool_calls" },
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
            new ChatResponse { Content = null, ToolCalls = [toolCall], FinishReason = "tool_calls" },
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

    private sealed class EmptyToolParams { }
}

/// <summary>
/// Tests for Agent.StreamChatAsync — streaming tool-calling loop added in this PR.
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

    [TestMethod]
    public async Task StreamChatAsync_NoTools_YieldsTokens()
    {
        _stubClient.StreamEvents = [
            new StreamEvent.TokenDelta("Hello"),
            new StreamEvent.TokenDelta(" world")
        ];

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
        _stubClient.StreamEvents = [new StreamEvent.TokenDelta("response")];

        var session = new Session { Id = "stream-2" };

        await foreach (var _ in _agent.StreamChatAsync("my message", session, CancellationToken.None)) { }

        Assert.IsTrue(session.Messages.Any(m => m.Role == "user" && m.Content == "my message"),
            "User message should be added to session");
    }

    [TestMethod]
    public async Task StreamChatAsync_NoTools_AddsAssistantMessageToSession()
    {
        _stubClient.StreamEvents = [
            new StreamEvent.TokenDelta("assistant"),
            new StreamEvent.TokenDelta(" reply")
        ];

        var session = new Session { Id = "stream-3" };

        await foreach (var _ in _agent.StreamChatAsync("prompt", session, CancellationToken.None)) { }

        Assert.IsTrue(session.Messages.Any(m => m.Role == "assistant" && m.Content == "assistant reply"),
            "Accumulated response should be saved as assistant message");
    }

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

        // First CompleteWithTools returns tool call; second returns final text
        var callCount = 0;
        _stubClient.OnCompleteWithTools = (msgs, tools, ct) =>
        {
            callCount++;
            if (callCount == 1)
                return Task.FromResult(new ChatResponse { ToolCalls = [toolCall], FinishReason = "tool_calls" });
            return Task.FromResult(new ChatResponse { Content = "stream final", FinishReason = "stop" });
        };

        var session = new Session { Id = "stream-4" };
        var tokenTexts = new List<string>();

        await foreach (var evt in _agent.StreamChatAsync("run tool", session, CancellationToken.None))
        {
            if (evt is StreamEvent.TokenDelta td)
                tokenTexts.Add(td.Text);
        }

        // The final text "stream final" should appear as a token delta
        Assert.IsTrue(tokenTexts.Any(t => t.Contains("stream final") || t.Contains("stream_tool")),
            "Final response or tool call notification should be yielded");
    }

    [TestMethod]
    public async Task StreamChatAsync_NoTools_EmptyResponse_SavesEmptyAssistantMessage()
    {
        _stubClient.StreamEvents = []; // no tokens

        var session = new Session { Id = "stream-5" };

        await foreach (var _ in _agent.StreamChatAsync("question", session, CancellationToken.None)) { }

        // Should still save an assistant message (possibly empty)
        Assert.IsTrue(session.Messages.Any(m => m.Role == "assistant"),
            "An assistant message should be saved even if streaming yielded no tokens");
    }

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
        public IReadOnlyList<StreamEvent> StreamEvents { get; set; } = [];
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
            // Default: no tool calls, return empty final response
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

            foreach (var evt in StreamEvents)
            {
                ct.ThrowIfCancellationRequested();
                yield return evt;
            }
        }
    }
}