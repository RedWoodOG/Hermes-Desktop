using Hermes.Agent.Core;
using Hermes.Agent.Transcript;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

/// <summary>
/// Tests for TranscriptStore — the persistence layer used by the new HermesChatService
/// (which replaced the old sidecar-based approach with direct in-process execution).
/// HermesChatService.SendAsync saves every new message via TranscriptStore.SaveMessageAsync.
/// </summary>
[TestClass]
public class TranscriptStoreTests
{
    private string _tempDir = "";

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hermes-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private TranscriptStore CreateStore(bool eagerFlush = false)
        => new(_tempDir, eagerFlush);

    // ── Construction ──

    [TestMethod]
    public void Constructor_CreatesTranscriptsDirectory_IfNotExists()
    {
        var subDir = Path.Combine(_tempDir, "nested", "transcripts");
        _ = new TranscriptStore(subDir);

        Assert.IsTrue(Directory.Exists(subDir));
    }

    // ── SaveMessageAsync ──

    [TestMethod]
    public async Task SaveMessageAsync_WritesJsonlFileToDisk()
    {
        var store = CreateStore();
        var msg = new Message { Role = "user", Content = "Hello there" };

        await store.SaveMessageAsync("session1", msg, CancellationToken.None);

        var files = Directory.GetFiles(_tempDir, "*.jsonl");
        Assert.AreEqual(1, files.Length);
        var content = await File.ReadAllTextAsync(files[0]);
        StringAssert.Contains(content, "Hello there");
    }

    [TestMethod]
    public async Task SaveMessageAsync_AppendsMultipleMessages_InSameFile()
    {
        var store = CreateStore();
        var msg1 = new Message { Role = "user", Content = "First" };
        var msg2 = new Message { Role = "assistant", Content = "Second" };

        await store.SaveMessageAsync("sess", msg1, CancellationToken.None);
        await store.SaveMessageAsync("sess", msg2, CancellationToken.None);

        var files = Directory.GetFiles(_tempDir, "*.jsonl");
        Assert.AreEqual(1, files.Length);
        var lines = (await File.ReadAllLinesAsync(files[0]))
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.AreEqual(2, lines.Length);
    }

    [TestMethod]
    public async Task SaveMessageAsync_DifferentSessions_CreateSeparateFiles()
    {
        var store = CreateStore();

        await store.SaveMessageAsync("session-a", new Message { Role = "user", Content = "A" }, CancellationToken.None);
        await store.SaveMessageAsync("session-b", new Message { Role = "user", Content = "B" }, CancellationToken.None);

        var files = Directory.GetFiles(_tempDir, "*.jsonl");
        Assert.AreEqual(2, files.Length);
    }

    [TestMethod]
    public async Task SaveMessageAsync_WithEagerFlush_StillPersists()
    {
        var store = CreateStore(eagerFlush: true);
        var msg = new Message { Role = "user", Content = "eager" };

        await store.SaveMessageAsync("eager-sess", msg, CancellationToken.None);

        Assert.IsTrue(store.SessionExists("eager-sess"));
    }

    // ── LoadSessionAsync ──

    [TestMethod]
    public async Task LoadSessionAsync_ReturnsAllSavedMessages_InOrder()
    {
        var store = CreateStore();
        var messages = new[]
        {
            new Message { Role = "user", Content = "msg1" },
            new Message { Role = "assistant", Content = "msg2" },
            new Message { Role = "user", Content = "msg3" },
        };

        foreach (var m in messages)
            await store.SaveMessageAsync("s1", m, CancellationToken.None);

        var loaded = await store.LoadSessionAsync("s1", CancellationToken.None);

        Assert.AreEqual(3, loaded.Count);
        Assert.AreEqual("msg1", loaded[0].Content);
        Assert.AreEqual("msg2", loaded[1].Content);
        Assert.AreEqual("msg3", loaded[2].Content);
    }

    [TestMethod]
    public async Task LoadSessionAsync_PreservesRoles()
    {
        var store = CreateStore();
        await store.SaveMessageAsync("s2", new Message { Role = "user", Content = "hi" }, CancellationToken.None);
        await store.SaveMessageAsync("s2", new Message { Role = "assistant", Content = "hey" }, CancellationToken.None);
        await store.SaveMessageAsync("s2", new Message { Role = "tool", Content = "result", ToolCallId = "c1", ToolName = "bash" }, CancellationToken.None);

        var loaded = await store.LoadSessionAsync("s2", CancellationToken.None);

        Assert.AreEqual("user", loaded[0].Role);
        Assert.AreEqual("assistant", loaded[1].Role);
        Assert.AreEqual("tool", loaded[2].Role);
        Assert.AreEqual("c1", loaded[2].ToolCallId);
        Assert.AreEqual("bash", loaded[2].ToolName);
    }

    [TestMethod]
    public async Task LoadSessionAsync_ThrowsSessionNotFoundException_ForUnknownSession()
    {
        var store = CreateStore();

        await Assert.ThrowsExceptionAsync<SessionNotFoundException>(async () =>
            await store.LoadSessionAsync("nonexistent-id", CancellationToken.None));
    }

    [TestMethod]
    public async Task LoadSessionAsync_ReturnsCachedResult_OnSecondCall()
    {
        var store = CreateStore();
        await store.SaveMessageAsync("s3", new Message { Role = "user", Content = "cached" }, CancellationToken.None);

        var first = await store.LoadSessionAsync("s3", CancellationToken.None);
        var second = await store.LoadSessionAsync("s3", CancellationToken.None);

        // Both should return the same data
        Assert.AreEqual(first.Count, second.Count);
        Assert.AreEqual(first[0].Content, second[0].Content);
    }

    [TestMethod]
    public async Task LoadSessionAsync_ReturnsNewListInstance_EachCall()
    {
        var store = CreateStore();
        await store.SaveMessageAsync("s4", new Message { Role = "user", Content = "x" }, CancellationToken.None);

        var first = await store.LoadSessionAsync("s4", CancellationToken.None);
        var second = await store.LoadSessionAsync("s4", CancellationToken.None);

        // Modifying one list should not affect the other (returns a copy from cache)
        first.Add(new Message { Role = "user", Content = "injected" });
        Assert.AreEqual(1, second.Count, "Second load should not see mutation of first load result");
    }

    // ── SessionExists ──

    [TestMethod]
    public async Task SessionExists_ReturnsFalse_ForUnknownSession()
    {
        var store = CreateStore();

        Assert.IsFalse(store.SessionExists("ghost"));
    }

    [TestMethod]
    public async Task SessionExists_ReturnsTrue_AfterSave()
    {
        var store = CreateStore();
        await store.SaveMessageAsync("known", new Message { Role = "user", Content = "x" }, CancellationToken.None);

        Assert.IsTrue(store.SessionExists("known"));
    }

    [TestMethod]
    public async Task SessionExists_ReturnsTrue_FromDiskWithoutCache()
    {
        // Save in one store instance (written to disk)
        var store1 = CreateStore();
        await store1.SaveMessageAsync("disk-sess", new Message { Role = "user", Content = "y" }, CancellationToken.None);

        // New store instance has empty cache but same directory
        var store2 = CreateStore();
        Assert.IsTrue(store2.SessionExists("disk-sess"), "Should detect session from disk even with empty cache");
    }

    // ── GetAllSessionIds ──

    [TestMethod]
    public void GetAllSessionIds_ReturnsEmpty_WhenNoSessions()
    {
        var store = CreateStore();

        var ids = store.GetAllSessionIds();

        Assert.AreEqual(0, ids.Count);
    }

    [TestMethod]
    public async Task GetAllSessionIds_ReturnsAllSessionIds()
    {
        var store = CreateStore();
        await store.SaveMessageAsync("alpha", new Message { Role = "user", Content = "a" }, CancellationToken.None);
        await store.SaveMessageAsync("beta", new Message { Role = "user", Content = "b" }, CancellationToken.None);
        await store.SaveMessageAsync("gamma", new Message { Role = "user", Content = "c" }, CancellationToken.None);

        var ids = store.GetAllSessionIds();

        Assert.AreEqual(3, ids.Count);
        CollectionAssert.Contains(ids, "alpha");
        CollectionAssert.Contains(ids, "beta");
        CollectionAssert.Contains(ids, "gamma");
    }

    [TestMethod]
    public async Task GetAllSessionIds_IncludesFromDisk_NotJustCache()
    {
        // Write to disk via one store
        var store1 = CreateStore();
        await store1.SaveMessageAsync("disk-only", new Message { Role = "user", Content = "z" }, CancellationToken.None);

        // New store has empty cache
        var store2 = CreateStore();
        var ids = store2.GetAllSessionIds();

        CollectionAssert.Contains(ids, "disk-only");
    }

    [TestMethod]
    public async Task GetAllSessionIds_DeduplicatesCacheAndDisk()
    {
        // Write to disk AND cache by loading
        var store = CreateStore();
        await store.SaveMessageAsync("both", new Message { Role = "user", Content = "q" }, CancellationToken.None);
        await store.LoadSessionAsync("both", CancellationToken.None); // populates cache

        var ids = store.GetAllSessionIds();

        var count = ids.Count(id => id == "both");
        Assert.AreEqual(1, count, "Same session should not appear twice");
    }

    // ── DeleteSessionAsync ──

    [TestMethod]
    public async Task DeleteSessionAsync_RemovesFile_FromDisk()
    {
        var store = CreateStore();
        await store.SaveMessageAsync("to-delete", new Message { Role = "user", Content = "bye" }, CancellationToken.None);

        await store.DeleteSessionAsync("to-delete", CancellationToken.None);

        Assert.IsFalse(store.SessionExists("to-delete"));
        var files = Directory.GetFiles(_tempDir, "*.jsonl");
        Assert.AreEqual(0, files.Length);
    }

    [TestMethod]
    public async Task DeleteSessionAsync_ForNonExistentSession_DoesNotThrow()
    {
        var store = CreateStore();

        // Should complete without exception
        await store.DeleteSessionAsync("ghost-session", CancellationToken.None);
    }

    [TestMethod]
    public async Task DeleteSessionAsync_RemovesFromGetAllSessionIds()
    {
        var store = CreateStore();
        await store.SaveMessageAsync("del-test", new Message { Role = "user", Content = "x" }, CancellationToken.None);

        await store.DeleteSessionAsync("del-test", CancellationToken.None);
        var ids = store.GetAllSessionIds();

        CollectionAssert.DoesNotContain(ids, "del-test");
    }

    // ── ClearCache ──

    [TestMethod]
    public async Task ClearCache_KeepsDataOnDisk_ButEmptiesCache()
    {
        var store = CreateStore();
        await store.SaveMessageAsync("persist", new Message { Role = "user", Content = "keep" }, CancellationToken.None);
        await store.LoadSessionAsync("persist", CancellationToken.None); // populate cache

        store.ClearCache();

        // Data should still be loadable from disk
        var loaded = await store.LoadSessionAsync("persist", CancellationToken.None);
        Assert.AreEqual(1, loaded.Count);
    }

    // ── SessionNotFoundException ──

    [TestMethod]
    public void SessionNotFoundException_ContainsSessionId_InMessage()
    {
        var ex = new SessionNotFoundException("my-session-id");

        StringAssert.Contains(ex.Message, "my-session-id");
    }

    // ── Edge cases (regression) ──

    [TestMethod]
    public async Task SaveAndLoad_MessageWithSpecialCharacters_RoundTrips()
    {
        var store = CreateStore();
        var content = "Hello \"world\"!\nNew line\t tab\r\nWindows line ending";
        await store.SaveMessageAsync("special", new Message { Role = "user", Content = content }, CancellationToken.None);

        var loaded = await store.LoadSessionAsync("special", CancellationToken.None);

        Assert.AreEqual(content, loaded[0].Content);
    }

    [TestMethod]
    public async Task SaveAndLoad_MessageWithUnicodeContent_RoundTrips()
    {
        var store = CreateStore();
        var content = "日本語テスト 🎉 emoji ñoño";
        await store.SaveMessageAsync("unicode", new Message { Role = "user", Content = content }, CancellationToken.None);

        var loaded = await store.LoadSessionAsync("unicode", CancellationToken.None);

        Assert.AreEqual(content, loaded[0].Content);
    }

    [TestMethod]
    public async Task SaveAndLoad_EmptyContent_RoundTrips()
    {
        var store = CreateStore();
        await store.SaveMessageAsync("empty", new Message { Role = "system", Content = "" }, CancellationToken.None);

        var loaded = await store.LoadSessionAsync("empty", CancellationToken.None);

        Assert.AreEqual("", loaded[0].Content);
    }

    [TestMethod]
    public async Task ConcurrentSaves_ToSameSession_AllMessagesPreserved()
    {
        var store = CreateStore();
        const int count = 20;

        var tasks = Enumerable.Range(0, count).Select(i =>
            store.SaveMessageAsync("concurrent", new Message { Role = "user", Content = $"msg-{i}" }, CancellationToken.None));

        await Task.WhenAll(tasks);

        var loaded = await store.LoadSessionAsync("concurrent", CancellationToken.None);
        Assert.AreEqual(count, loaded.Count, "All concurrent saves should be present");
    }
}