using Hermes.Agent.Dreamer;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Dreamer;

[TestClass]
public class DreamerStatusTests
{
    // ── Initial state ──

    [TestMethod]
    public void GetSnapshot_InitialState_PhaseIsIdle()
    {
        var status = new DreamerStatus();
        var snapshot = status.GetSnapshot();

        Assert.AreEqual("idle", snapshot.Phase);
    }

    [TestMethod]
    public void GetSnapshot_InitialState_WalkCountIsZero()
    {
        var status = new DreamerStatus();
        var snapshot = status.GetSnapshot();

        Assert.AreEqual(0, snapshot.WalkCount);
    }

    [TestMethod]
    public void GetSnapshot_InitialState_SummaryAndPostcardAreEmpty()
    {
        var status = new DreamerStatus();
        var snapshot = status.GetSnapshot();

        Assert.AreEqual("", snapshot.LastWalkSummary);
        Assert.AreEqual("", snapshot.LastPostcardPreview);
        Assert.AreEqual("", snapshot.TopSignalSlug);
        Assert.AreEqual(0.0, snapshot.TopSignalScore, 0.001);
    }

    // ── SetPhase ──

    [TestMethod]
    public void SetPhase_UpdatesPhaseInSnapshot()
    {
        var status = new DreamerStatus();
        status.SetPhase("walking");

        Assert.AreEqual("walking", status.GetSnapshot().Phase);
    }

    [TestMethod]
    public void SetPhase_CalledMultipleTimes_LastValueWins()
    {
        var status = new DreamerStatus();
        status.SetPhase("walking");
        status.SetPhase("building");
        status.SetPhase("idle");

        Assert.AreEqual("idle", status.GetSnapshot().Phase);
    }

    [TestMethod]
    public void SetPhase_AllKnownPhases_DoNotThrow()
    {
        var status = new DreamerStatus();
        foreach (var phase in new[] { "idle", "walking", "building", "disabled", "error" })
        {
            status.SetPhase(phase);
            Assert.AreEqual(phase, status.GetSnapshot().Phase);
        }
    }

    // ── AfterWalk ──

    [TestMethod]
    public void AfterWalk_SetsPhaseToIdle()
    {
        var status = new DreamerStatus();
        status.SetPhase("walking");
        status.AfterWalk("preview text", 1, 5.5, "my-project");

        Assert.AreEqual("idle", status.GetSnapshot().Phase);
    }

    [TestMethod]
    public void AfterWalk_SetsWalkCount()
    {
        var status = new DreamerStatus();
        status.AfterWalk("preview", 7, 0.0, "");

        Assert.AreEqual(7, status.GetSnapshot().WalkCount);
    }

    [TestMethod]
    public void AfterWalk_SetsWalkPreview()
    {
        var status = new DreamerStatus();
        status.AfterWalk("my walk preview", 1, 0.0, "");

        Assert.AreEqual("my walk preview", status.GetSnapshot().LastWalkSummary);
    }

    [TestMethod]
    public void AfterWalk_SetsTopSignalScoreAndSlug()
    {
        var status = new DreamerStatus();
        status.AfterWalk("preview", 1, 8.3, "cool-project");

        var snap = status.GetSnapshot();
        Assert.AreEqual(8.3, snap.TopSignalScore, 0.001);
        Assert.AreEqual("cool-project", snap.TopSignalSlug);
    }

    [TestMethod]
    public void AfterWalk_CalledTwice_SecondCallOverwrites()
    {
        var status = new DreamerStatus();
        status.AfterWalk("first", 1, 3.0, "first-project");
        status.AfterWalk("second", 2, 9.0, "second-project");

        var snap = status.GetSnapshot();
        Assert.AreEqual(2, snap.WalkCount);
        Assert.AreEqual("second", snap.LastWalkSummary);
        Assert.AreEqual("second-project", snap.TopSignalSlug);
    }

    // ── SetPostcardPreview ──

    [TestMethod]
    public void SetPostcardPreview_UpdatesPostcardInSnapshot()
    {
        var status = new DreamerStatus();
        status.SetPostcardPreview("postcard content here");

        Assert.AreEqual("postcard content here", status.GetSnapshot().LastPostcardPreview);
    }

    [TestMethod]
    public void SetPostcardPreview_EmptyString_ClearsPostcard()
    {
        var status = new DreamerStatus();
        status.SetPostcardPreview("old content");
        status.SetPostcardPreview("");

        Assert.AreEqual("", status.GetSnapshot().LastPostcardPreview);
    }

    // ── Thread safety ──

    [TestMethod]
    public void ConcurrentUpdates_DoNotThrowOrCorrupt()
    {
        var status = new DreamerStatus();
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            status.SetPhase(i % 2 == 0 ? "walking" : "idle");
            status.AfterWalk($"preview {i}", i, i * 0.5, $"project-{i}");
            status.SetPostcardPreview($"postcard {i}");
            _ = status.GetSnapshot();
        })).ToArray();

        Task.WaitAll(tasks);

        // After all concurrent writes the snapshot should be in a valid, consistent state
        var snap = status.GetSnapshot();
        Assert.IsNotNull(snap.Phase);
        Assert.IsTrue(snap.WalkCount >= 0);
    }

    // ── DreamerStatusSnapshot record ──

    [TestMethod]
    public void DreamerStatusSnapshot_RecordEquality_SameValues_AreEqual()
    {
        var a = new DreamerStatusSnapshot("idle", 5, "summary", "postcard", "", 3.5, "slug", "");
        var b = new DreamerStatusSnapshot("idle", 5, "summary", "postcard", "", 3.5, "slug", "");

        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void DreamerStatusSnapshot_RecordEquality_DifferentPhase_AreNotEqual()
    {
        var a = new DreamerStatusSnapshot("idle", 0, "", "", "", 0.0, "", "");
        var b = new DreamerStatusSnapshot("walking", 0, "", "", "", 0.0, "", "");

        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void DreamerStatusSnapshot_AllPropertiesAccessible()
    {
        var snap = new DreamerStatusSnapshot("building", 3, "my walk", "my postcard", "none", 7.2, "my-slug", "digest.md");

        Assert.AreEqual("building", snap.Phase);
        Assert.AreEqual(3, snap.WalkCount);
        Assert.AreEqual("my walk", snap.LastWalkSummary);
        Assert.AreEqual("my postcard", snap.LastPostcardPreview);
        Assert.AreEqual("none", snap.StartupFailureMessage);
        Assert.AreEqual(7.2, snap.TopSignalScore, 0.001);
        Assert.AreEqual("my-slug", snap.TopSignalSlug);
        Assert.AreEqual("digest.md", snap.LastLocalDigestHint);
    }
}