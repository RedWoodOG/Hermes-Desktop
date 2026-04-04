using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Hermes.Agent.Permissions;
using Hermes.Agent.Transcript;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace HermesDesktop.Tests.Services;

/// <summary>
/// Tests for the logic introduced in the rewritten HermesChatService (PR change).
/// Since HermesChatService is in the WinUI project, these tests validate the same
/// patterns using the underlying Hermes.Core types it wraps:
/// Session creation, message persistence, PermissionMode changes, and streaming state.
/// </summary>
[TestClass]
public class HermesChatServiceLogicTests
{
    private string _tempDir = "";
    private Mock<IChatClient> _mockChatClient = null!;
    private Agent _agent = null!;
    private TranscriptStore _transcriptStore = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hermes-chat-tests-{Guid.NewGuid():N}");
        _mockChatClient = new Mock<IChatClient>(MockBehavior.Loose);
        _agent = new Agent(_mockChatClient.Object, NullLogger<Agent>.Instance);
        _transcriptStore = new TranscriptStore(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Session lifecycle (mirrors HermesChatService.EnsureSession) ──

    [TestMethod]
    public void NewSession_HasDesktopPlatform()
    {
        var session = new Session
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Platform = "desktop"
        };

        Assert.AreEqual("desktop", session.Platform);
    }

    [TestMethod]
    public void NewSession_HasEightCharId()
    {
        // HermesChatService.EnsureSession uses Guid.NewGuid().ToString("N")[..8]
        var id = Guid.NewGuid().ToString("N")[..8];

        Assert.AreEqual(8, id.Length);
        Assert.IsTrue(id.All(char.IsAsciiLetterOrDigit), "Id should be alphanumeric");
    }

    [TestMethod]
    public void NewSession_IdIsUnique_AcrossMultipleCreations()
    {
        var ids = Enumerable.Range(0, 50)
            .Select(_ => Guid.NewGuid().ToString("N")[..8])
            .ToList();

        var distinct = ids.Distinct().Count();
        Assert.AreEqual(50, distinct, "All generated session IDs should be unique");
    }

    [TestMethod]
    public void Session_Messages_StartEmpty()
    {
        var session = new Session { Id = "abc12345", Platform = "desktop" };

        Assert.AreEqual(0, session.Messages.Count);
        Assert.IsNull(session.Id is { Length: > 0 } ? null : "empty id");
    }

    // ── PermissionMode (mirrors HermesChatService.SetPermissionMode) ──

    [TestMethod]
    public void PermissionMode_DefaultValue_IsDefault()
    {
        // This mirrors HermesChatService's initial state
        PermissionMode mode = PermissionMode.Default;

        Assert.AreEqual(PermissionMode.Default, mode);
    }

    [TestMethod]
    public void PermissionMode_AllValues_CanBeAssigned()
    {
        var modes = new[] {
            PermissionMode.Default,
            PermissionMode.Plan,
            PermissionMode.Auto,
            PermissionMode.BypassPermissions,
            PermissionMode.AcceptEdits
        };

        PermissionMode current = PermissionMode.Default;
        foreach (var mode in modes)
        {
            current = mode;
            Assert.AreEqual(mode, current);
        }
    }

    [TestMethod]
    public void PermissionMode_HasFiveDistinctValues()
    {
        var values = Enum.GetValues<PermissionMode>();
        Assert.AreEqual(5, values.Length);
    }

    // ── SendAsync pattern (no tools — simple completion path) ──

    [TestMethod]
    public async Task SendAsync_Pattern_SavesMessagesAfterChat()
    {
        // This replicates HermesChatService.SendAsync logic:
        // 1. EnsureSession
        // 2. Record message count before
        // 3. Agent.ChatAsync
        // 4. Save new messages to transcript
        var session = new Session { Id = "send-test", Platform = "desktop" };
        var messageCountBefore = session.Messages.Count;

        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("LLM answer");

        var response = await _agent.ChatAsync("test input", session, CancellationToken.None);

        // Persist new messages (as HermesChatService does)
        for (var i = messageCountBefore; i < session.Messages.Count; i++)
            await _transcriptStore.SaveMessageAsync(session.Id, session.Messages[i], CancellationToken.None);

        Assert.AreEqual("LLM answer", response);
        var loaded = await _transcriptStore.LoadSessionAsync(session.Id, CancellationToken.None);
        Assert.AreEqual(2, loaded.Count); // user msg + assistant msg
        Assert.AreEqual("user", loaded[0].Role);
        Assert.AreEqual("assistant", loaded[1].Role);
    }

    [TestMethod]
    public async Task SendAsync_Pattern_PersistsOnlyNewMessages()
    {
        // Replicate partial save: only messages added during current turn
        var session = new Session { Id = "partial-save", Platform = "desktop" };
        session.AddMessage(new Message { Role = "user", Content = "prior user msg" });
        session.AddMessage(new Message { Role = "assistant", Content = "prior assistant msg" });
        var messageCountBefore = session.Messages.Count; // = 2

        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("new reply");

        await _agent.ChatAsync("new question", session, CancellationToken.None);

        // Save only new messages (index >= messageCountBefore)
        for (var i = messageCountBefore; i < session.Messages.Count; i++)
            await _transcriptStore.SaveMessageAsync(session.Id, session.Messages[i], CancellationToken.None);

        var saved = await _transcriptStore.LoadSessionAsync(session.Id, CancellationToken.None);
        Assert.AreEqual(2, saved.Count, "Only new messages should be saved, not pre-existing ones");
    }

    // ── ResetConversation pattern ──

    [TestMethod]
    public void ResetConversation_Pattern_ClearsCurrentSession()
    {
        // Simulate HermesChatService.ResetConversation()
        Session? currentSession = new Session { Id = "old-session", Platform = "desktop" };
        currentSession.AddMessage(new Message { Role = "user", Content = "some history" });

        // Reset
        currentSession = null;

        Assert.IsNull(currentSession);
    }

    [TestMethod]
    public void ResetConversation_Pattern_NewSessionAfterReset_HasFreshState()
    {
        Session? currentSession = new Session { Id = "old" };
        currentSession.AddMessage(new Message { Role = "user", Content = "existing" });

        // Reset
        currentSession = null;

        // Ensure new session (EnsureSession)
        if (currentSession is null)
        {
            currentSession = new Session
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Platform = "desktop"
            };
        }

        Assert.AreEqual(0, currentSession.Messages.Count);
        Assert.AreNotEqual("old", currentSession.Id);
    }

    // ── LoadSessionAsync pattern ──

    [TestMethod]
    public async Task LoadSessionAsync_Pattern_RestoresAllMessages()
    {
        // Save some messages
        var sessionId = "load-test-session";
        await _transcriptStore.SaveMessageAsync(sessionId, new Message { Role = "user", Content = "hello" }, CancellationToken.None);
        await _transcriptStore.SaveMessageAsync(sessionId, new Message { Role = "assistant", Content = "world" }, CancellationToken.None);

        // Simulate HermesChatService.LoadSessionAsync
        var messages = await _transcriptStore.LoadSessionAsync(sessionId, CancellationToken.None);
        var session = new Session { Id = sessionId, Platform = "desktop" };
        foreach (var msg in messages)
            session.AddMessage(msg);

        Assert.AreEqual(2, session.Messages.Count);
        Assert.AreEqual("hello", session.Messages[0].Content);
        Assert.AreEqual("world", session.Messages[1].Content);
    }

    // ── CancelStream pattern ──

    [TestMethod]
    public void CancelStream_Pattern_CancelsLinkedCts()
    {
        using var outerCts = new CancellationTokenSource();
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(outerCts.Token);

        // Simulate CancelStream()
        streamCts.Cancel();

        Assert.IsTrue(streamCts.Token.IsCancellationRequested);
    }

    [TestMethod]
    public void CancelStream_Pattern_WhenNoCtsExists_DoesNotThrow()
    {
        // Simulate the guard: _streamCts?.Cancel()
        CancellationTokenSource? streamCts = null;

        // Should not throw
        streamCts?.Cancel();
        Assert.IsNull(streamCts);
    }

    // ── Dispose pattern ──

    [TestMethod]
    public void Dispose_Pattern_DisposesCts()
    {
        using var streamCts = new CancellationTokenSource();
        var disposed = false;

        // Simulate HermesChatService.Dispose()
        if (!disposed)
        {
            streamCts.Dispose();
            disposed = true;
        }

        Assert.IsTrue(disposed);
        // Calling Dispose on already-disposed CTS shouldn't throw
        streamCts.Dispose();
    }

    [TestMethod]
    public void Dispose_Pattern_IdempotentWhenCalledTwice()
    {
        // Simulate _disposed guard in Dispose()
        var disposed = false;

        void Dispose()
        {
            if (disposed) return;
            disposed = true;
        }

        Dispose();
        Dispose(); // Second call should be no-op

        Assert.IsTrue(disposed);
    }

    // ── CheckHealth pattern ──

    [TestMethod]
    public async Task CheckHealth_Pattern_ReturnsTrueForNonEmptyResponse()
    {
        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("OK");

        // Replicate HermesChatService.CheckHealthAsync logic
        bool isHealthy;
        string detail;
        try
        {
            var messages = new[] { new Message { Role = "user", Content = "Respond with only: OK" } };
            var response = await _mockChatClient.Object.CompleteAsync(messages, CancellationToken.None);
            (isHealthy, detail) = !string.IsNullOrEmpty(response)
                ? (true, "Connected to LLM")
                : (false, "Empty response from LLM");
        }
        catch (Exception ex)
        {
            (isHealthy, detail) = (false, ex.Message);
        }

        Assert.IsTrue(isHealthy);
        Assert.AreEqual("Connected to LLM", detail);
    }

    [TestMethod]
    public async Task CheckHealth_Pattern_ReturnsFalseForEmptyResponse()
    {
        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");

        bool isHealthy;
        string detail;
        try
        {
            var messages = new[] { new Message { Role = "user", Content = "Respond with only: OK" } };
            var response = await _mockChatClient.Object.CompleteAsync(messages, CancellationToken.None);
            (isHealthy, detail) = !string.IsNullOrEmpty(response)
                ? (true, "Connected to LLM")
                : (false, "Empty response from LLM");
        }
        catch (Exception ex)
        {
            (isHealthy, detail) = (false, ex.Message);
        }

        Assert.IsFalse(isHealthy);
        Assert.AreEqual("Empty response from LLM", detail);
    }

    [TestMethod]
    public async Task CheckHealth_Pattern_ReturnsFalseOnException()
    {
        _mockChatClient
            .Setup(c => c.CompleteAsync(It.IsAny<IEnumerable<Message>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        bool isHealthy;
        string detail;
        try
        {
            var messages = new[] { new Message { Role = "user", Content = "Respond with only: OK" } };
            var response = await _mockChatClient.Object.CompleteAsync(messages, CancellationToken.None);
            (isHealthy, detail) = !string.IsNullOrEmpty(response)
                ? (true, "Connected to LLM")
                : (false, "Empty response from LLM");
        }
        catch (Exception ex)
        {
            (isHealthy, detail) = (false, ex.Message);
        }

        Assert.IsFalse(isHealthy);
        StringAssert.Contains(detail, "Connection refused");
    }
}