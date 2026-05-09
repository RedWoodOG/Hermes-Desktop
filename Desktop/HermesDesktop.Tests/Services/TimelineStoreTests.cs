using Hermes.Agent.Transcript;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

[TestClass]
public sealed class TimelineStoreTests
{
    private string _tempDir = "";

    [TestInitialize]
    public void Initialize()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hermes-timeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task StartTurnAsync_CreatesDurableThreadAndTurn()
    {
        var store = new TimelineStore(_tempDir);

        var turn = await store.StartTurnAsync(
            "session-a",
            "desktop",
            "Build the thing",
            workspaceRoot: "C:\\workspace",
            provider: "openai",
            model: "gpt-test",
            CancellationToken.None);

        var thread = await store.LoadThreadAsync("session-a", CancellationToken.None);
        var loadedTurn = await store.LoadTurnAsync(turn.TurnId, CancellationToken.None);

        Assert.IsNotNull(thread);
        Assert.IsNotNull(loadedTurn);
        Assert.AreEqual("session-a", thread.ThreadId);
        Assert.AreEqual(turn.TurnId, thread.LatestTurnId);
        Assert.AreEqual("C:\\workspace", thread.WorkspaceRoot);
        Assert.AreEqual(TurnStatus.InProgress, loadedTurn.Status);
        Assert.AreEqual(1, loadedTurn.Sequence);
        Assert.AreEqual("Build the thing", loadedTurn.InputSummary);
    }

    [TestMethod]
    public async Task AppendItemAsync_PersistsOrderedTurnItems()
    {
        var store = new TimelineStore(_tempDir);
        var turn = await store.StartTurnAsync("session-a", "desktop", "Question", null, null, null, CancellationToken.None);

        var userItem = await store.AppendItemAsync(
            turn.ThreadId,
            turn.TurnId,
            TurnItemKind.UserMessage,
            TurnItemStatus.Completed,
            "Question",
            role: "user",
            metadata: new Dictionary<string, string> { ["source"] = "test" },
            ct: CancellationToken.None);
        var assistantItem = await store.AppendItemAsync(
            turn.ThreadId,
            turn.TurnId,
            TurnItemKind.AssistantMessage,
            TurnItemStatus.Completed,
            "Answer",
            role: "assistant",
            ct: CancellationToken.None);

        var items = await store.LoadTurnItemsAsync(turn.TurnId, CancellationToken.None);

        Assert.AreEqual(2, items.Count);
        Assert.AreEqual(userItem.ItemId, items[0].ItemId);
        Assert.AreEqual(assistantItem.ItemId, items[1].ItemId);
        Assert.AreEqual(1, items[0].Sequence);
        Assert.AreEqual(2, items[1].Sequence);
        Assert.AreEqual("test", items[0].Metadata["source"]);
    }

    [TestMethod]
    public async Task CompleteTurnAsync_MarksTurnCompleteAndSurvivesReopen()
    {
        var store = new TimelineStore(_tempDir);
        var turn = await store.StartTurnAsync("session-a", "desktop", "Question", null, null, null, CancellationToken.None);

        await store.CompleteTurnAsync(turn.ThreadId, turn.TurnId, TurnStatus.Completed, null, CancellationToken.None);

        var reopened = new TimelineStore(_tempDir);
        var loaded = await reopened.LoadTurnAsync(turn.TurnId, CancellationToken.None);
        var threads = await reopened.ListThreadsAsync(CancellationToken.None);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(TurnStatus.Completed, loaded.Status);
        Assert.IsNotNull(loaded.CompletedAt);
        Assert.IsTrue(loaded.DurationMs >= 0);
        Assert.AreEqual(1, threads.Count);
        Assert.AreEqual("session-a", threads[0].ThreadId);
    }

    [TestMethod]
    public async Task LoadEventsAsync_PreservesSequenceAndSkipsCorruptLines()
    {
        var store = new TimelineStore(_tempDir);
        var turn = await store.StartTurnAsync("session-a", "desktop", "Question", null, null, null, CancellationToken.None);
        await store.AppendItemAsync(turn.ThreadId, turn.TurnId, TurnItemKind.UserMessage, TurnItemStatus.Completed, "Question", ct: CancellationToken.None);
        await store.CompleteTurnAsync(turn.ThreadId, turn.TurnId, TurnStatus.Completed, null, CancellationToken.None);

        var eventPath = Path.Combine(_tempDir, "events", "session-a.jsonl");
        await File.AppendAllTextAsync(eventPath, "{bad json\n", CancellationToken.None);

        var events = await store.LoadEventsAsync("session-a", CancellationToken.None);

        Assert.AreEqual(4, events.Count);
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, events.Select(evt => evt.EventSequence).ToArray());
    }

    [TestMethod]
    public async Task Summaries_AreNormalizedForUiLists()
    {
        var store = new TimelineStore(_tempDir);
        var longMessage = "hello\r\n" + new string('x', 400);

        var turn = await store.StartTurnAsync("session-a", "desktop", longMessage, null, null, null, CancellationToken.None);

        Assert.IsFalse(turn.InputSummary.Contains('\n'));
        Assert.IsTrue(turn.InputSummary.Length <= 240);
        StringAssert.EndsWith(turn.InputSummary, "...");
    }
}
