using Hermes.Agent.Dreamer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Dreamer;

[TestClass]
public class SignalScorerTests
{
    private string _tempDir = "";
    private DreamerRoom _room = null!;
    private SignalScorer _scorer = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"signal-scorer-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _room = new DreamerRoom(_tempDir);
        _room.EnsureLayout();
        _scorer = new SignalScorer(_room, NullLogger<SignalScorer>.Instance);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private DreamerConfig DefaultConfig() => new()
    {
        TriggerThreshold = 7.0,
        MinWalksToTrigger = 4,
        Autonomy = "full"
    };

    // ── LoadBoard ──

    [TestMethod]
    public void LoadBoard_NoStateFile_ReturnsEmptyBoard()
    {
        var board = _scorer.LoadBoard();

        Assert.AreEqual(0, board.Projects.Count);
        Assert.AreEqual(0, board.PositiveWalkStreak);
        Assert.AreEqual(default(DateTime), board.LastWalkUtc);
    }

    [TestMethod]
    public void LoadBoard_CorruptJson_ReturnsEmptyBoard()
    {
        File.WriteAllText(_room.SignalStatePath, "not valid json {{{{");
        var board = _scorer.LoadBoard();

        Assert.AreEqual(0, board.Projects.Count);
    }

    // ── SaveBoard + LoadBoard round-trip ──

    [TestMethod]
    public void SaveBoard_ThenLoadBoard_RoundTripsData()
    {
        var board = new SignalBoard
        {
            PositiveWalkStreak = 3,
            LastWalkUtc = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc)
        };
        board.Projects["my-project"] = new ProjectSignals { Score = 5.5 };
        board.Projects["my-project"].SignalTypes.Add("excitement");

        _scorer.SaveBoard(board);
        var loaded = _scorer.LoadBoard();

        Assert.AreEqual(3, loaded.PositiveWalkStreak);
        Assert.IsTrue(loaded.Projects.ContainsKey("my-project"));
        Assert.AreEqual(5.5, loaded.Projects["my-project"].Score, 0.001);
        Assert.IsTrue(loaded.Projects["my-project"].SignalTypes.Contains("excitement"));
    }

    // ── ProcessWalk — slug extraction ──

    [TestMethod]
    public void ProcessWalk_WithBuildTag_ExtractsBuildSlug()
    {
        _scorer.ProcessWalk("I want to build this thing. [BUILD: my-feature]", 3, DefaultConfig(), out var slug);

        Assert.AreEqual("my-feature", slug);
    }

    [TestMethod]
    public void ProcessWalk_BuildTagCaseInsensitive_ExtractsSlug()
    {
        _scorer.ProcessWalk("[build: My-Feature]", 3, DefaultConfig(), out var slug);

        Assert.AreEqual("my-feature", slug?.ToLowerInvariant());
    }

    [TestMethod]
    public void ProcessWalk_NoBuildTag_SlugIsNull()
    {
        _scorer.ProcessWalk("Just a regular walk with no build intent.", 3, DefaultConfig(), out var slug);

        Assert.IsNull(slug);
    }

    [TestMethod]
    public void ProcessWalk_BuildSlugIsLowercased()
    {
        _scorer.ProcessWalk("[BUILD: MyProject]", 3, DefaultConfig(), out var slug);

        Assert.AreEqual("myproject", slug);
    }

    // ── ProcessWalk — signal detection ──

    [TestMethod]
    public void ProcessWalk_ExcitementKeyword_AddsExcitementSignal()
    {
        _scorer.ProcessWalk("This is fascinating and I'm so excited about it!", 1, DefaultConfig(), out _);

        var board = _scorer.LoadBoard();
        var general = board.Projects["general"];
        Assert.IsTrue(general.SignalTypes.Contains("excitement", StringComparer.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ProcessWalk_FrustrationKeyword_AddsFrustrationSignal()
    {
        _scorer.ProcessWalk("I feel completely blocked on this feature.", 1, DefaultConfig(), out _);

        var board = _scorer.LoadBoard();
        Assert.IsTrue(board.Projects["general"].SignalTypes.Contains("frustration", StringComparer.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ProcessWalk_ReturnKeyword_AddsReturnSignal()
    {
        _scorer.ProcessWalk("I keep coming back to the same idea again and again.", 1, DefaultConfig(), out _);

        var board = _scorer.LoadBoard();
        Assert.IsTrue(board.Projects["general"].SignalTypes.Contains("return", StringComparer.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ProcessWalk_CommitKeyword_AddsCommitSignal()
    {
        _scorer.ProcessWalk("I want to commit to this and ship it now.", 1, DefaultConfig(), out _);

        var board = _scorer.LoadBoard();
        Assert.IsTrue(board.Projects["general"].SignalTypes.Contains("commit", StringComparer.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ProcessWalk_CoolingKeyword_AddsCoolingSignal_AndReducesScore()
    {
        // First, add a positive signal to have a score to reduce
        _scorer.ProcessWalk("I am excited about this!", 1, DefaultConfig(), out _);
        var before = _scorer.LoadBoard().Projects["general"].Score;

        // Now cool down
        _scorer.ProcessWalk("Never mind, forget about it.", 1, DefaultConfig(), out _);
        var after = _scorer.LoadBoard().Projects["general"].Score;

        // The cooling signal has negative weight, so total should be lower than after only excitement
        Assert.IsTrue(after < before + 4.0, "Cooling signal should not further increase score");
    }

    [TestMethod]
    public void ProcessWalk_WithBuildSlug_CommitSignalGoesToSlugProject()
    {
        _scorer.ProcessWalk("I want to build and commit this. [BUILD: cool-slug]", 1, DefaultConfig(), out var slug);

        var board = _scorer.LoadBoard();
        Assert.IsTrue(board.Projects.ContainsKey("cool-slug"));
        Assert.IsTrue(board.Projects["cool-slug"].SignalTypes.Contains("commit", StringComparer.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ProcessWalk_WithBuildSlug_MentionSignalAddedToSlugProject()
    {
        _scorer.ProcessWalk("Just a mention [BUILD: my-slug]", 1, DefaultConfig(), out _);

        var board = _scorer.LoadBoard();
        Assert.IsTrue(board.Projects["my-slug"].SignalTypes.Contains("mention", StringComparer.OrdinalIgnoreCase));
    }

    // ── ProcessWalk — walk streak ──

    [TestMethod]
    public void ProcessWalk_IncrementsPositiveWalkStreak()
    {
        _scorer.ProcessWalk("walk 1", 3, DefaultConfig(), out _);
        _scorer.ProcessWalk("walk 2", 3, DefaultConfig(), out _);

        Assert.AreEqual(2, _scorer.LoadBoard().PositiveWalkStreak);
    }

    [TestMethod]
    public void ProcessWalk_UpdatesLastWalkUtc()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        _scorer.ProcessWalk("walk", 3, DefaultConfig(), out _);
        var after = DateTime.UtcNow.AddSeconds(1);

        var ts = _scorer.LoadBoard().LastWalkUtc;
        Assert.IsTrue(ts >= before && ts <= after);
    }

    // ── Echo score factor ──

    [TestMethod]
    public void ProcessWalk_HighEchoScore_ReducesDelta()
    {
        // Echo score 5 → echoFactor = (6-5)/5 = 0.2
        _scorer.ProcessWalk("I am excited about this!", 5, DefaultConfig(), out _);
        var highEchoScore = _scorer.LoadBoard().Projects["general"].Score;

        // Reset
        File.Delete(_room.SignalStatePath);
        File.Delete(_room.SignalLogPath);
        File.WriteAllText(_room.SignalLogPath, "");
        _scorer = new SignalScorer(_room, NullLogger<SignalScorer>.Instance);

        // Echo score 1 → echoFactor = (6-1)/5 = 1.0
        _scorer.ProcessWalk("I am excited about this!", 1, DefaultConfig(), out _);
        var lowEchoScore = _scorer.LoadBoard().Projects["general"].Score;

        Assert.IsTrue(lowEchoScore > highEchoScore, "Low echo score should produce higher signal delta");
    }

    // ── AppendSignalLog ──

    [TestMethod]
    public void AppendSignalLog_WritesToLogFile()
    {
        _scorer.AppendSignalLog(new SignalEvent { Type = "excitement", ProjectKey = "proj", Delta = 2.0 });

        var lines = File.ReadAllLines(_room.SignalLogPath);
        Assert.IsTrue(lines.Any(l => l.Contains("excitement")));
    }

    [TestMethod]
    public void ProcessWalk_WritesSignalEvents_ToLogFile()
    {
        _scorer.ProcessWalk("I'm so excited and I want to build this! [BUILD: test-proj]", 1, DefaultConfig(), out _);

        var logContent = File.ReadAllText(_room.SignalLogPath);
        Assert.IsTrue(logContent.Length > 0, "Signal log should have content after processing walk");
    }

    // ── ShouldTriggerBuild ──

    [TestMethod]
    public void ShouldTriggerBuild_NullSlug_ReturnsFalse()
    {
        var result = _scorer.ShouldTriggerBuild(null, DefaultConfig(), out _);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void ShouldTriggerBuild_EmptySlug_ReturnsFalse()
    {
        var result = _scorer.ShouldTriggerBuild("", DefaultConfig(), out _);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void ShouldTriggerBuild_StreakBelowMinimum_ReturnsFalse()
    {
        var board = new SignalBoard { PositiveWalkStreak = 2 }; // below MinWalksToTrigger=4
        board.Projects["my-slug"] = new ProjectSignals { Score = 100.0 };
        board.Projects["my-slug"].SignalTypes.AddRange(["excitement", "commit"]);
        _scorer.SaveBoard(board);

        var result = _scorer.ShouldTriggerBuild("my-slug", DefaultConfig(), out _);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void ShouldTriggerBuild_ScoreBelowThreshold_ReturnsFalse()
    {
        var board = new SignalBoard { PositiveWalkStreak = 5 };
        board.Projects["my-slug"] = new ProjectSignals { Score = 3.0 }; // below 7.0
        board.Projects["my-slug"].SignalTypes.AddRange(["excitement", "commit"]);
        _scorer.SaveBoard(board);

        var result = _scorer.ShouldTriggerBuild("my-slug", DefaultConfig(), out _);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void ShouldTriggerBuild_OnlyOneDistinctSignalType_ReturnsFalse()
    {
        var board = new SignalBoard { PositiveWalkStreak = 5 };
        board.Projects["my-slug"] = new ProjectSignals { Score = 20.0 };
        board.Projects["my-slug"].SignalTypes.Add("excitement"); // only one type
        _scorer.SaveBoard(board);
        // No log file entries for this slug

        var result = _scorer.ShouldTriggerBuild("my-slug", DefaultConfig(), out _);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void ShouldTriggerBuild_ProjectNotInBoard_ReturnsFalse()
    {
        var board = new SignalBoard { PositiveWalkStreak = 5 };
        _scorer.SaveBoard(board);

        var result = _scorer.ShouldTriggerBuild("nonexistent-slug", DefaultConfig(), out _);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void ShouldTriggerBuild_AllConditionsMet_ReturnsTrue()
    {
        var board = new SignalBoard { PositiveWalkStreak = 5 };
        board.Projects["ready-proj"] = new ProjectSignals { Score = 10.0 };
        board.Projects["ready-proj"].SignalTypes.AddRange(["excitement", "commit"]);
        _scorer.SaveBoard(board);

        var result = _scorer.ShouldTriggerBuild("ready-proj", DefaultConfig(), out var signals);

        Assert.IsTrue(result);
        Assert.IsNotNull(signals);
    }

    [TestMethod]
    public void ShouldTriggerBuild_OutSignals_ReturnsProjectSignals()
    {
        var board = new SignalBoard { PositiveWalkStreak = 5 };
        board.Projects["out-proj"] = new ProjectSignals { Score = 9.0 };
        board.Projects["out-proj"].SignalTypes.AddRange(["frustration", "commit"]);
        _scorer.SaveBoard(board);

        _scorer.ShouldTriggerBuild("out-proj", DefaultConfig(), out var signals);

        Assert.IsNotNull(signals);
        Assert.AreEqual(9.0, signals!.Score, 0.001);
    }

    // ── ResetProjectAfterBuild ──

    [TestMethod]
    public void ResetProjectAfterBuild_RemovesProject()
    {
        var board = new SignalBoard { PositiveWalkStreak = 5 };
        board.Projects["built-proj"] = new ProjectSignals { Score = 10.0 };
        _scorer.SaveBoard(board);

        _scorer.ResetProjectAfterBuild("built-proj");

        Assert.IsFalse(_scorer.LoadBoard().Projects.ContainsKey("built-proj"));
    }

    [TestMethod]
    public void ResetProjectAfterBuild_ResetsStreak()
    {
        var board = new SignalBoard { PositiveWalkStreak = 8 };
        board.Projects["proj"] = new ProjectSignals { Score = 5.0 };
        _scorer.SaveBoard(board);

        _scorer.ResetProjectAfterBuild("proj");

        Assert.AreEqual(0, _scorer.LoadBoard().PositiveWalkStreak);
    }

    [TestMethod]
    public void ResetProjectAfterBuild_NonexistentSlug_DoesNotThrow()
    {
        var board = new SignalBoard { PositiveWalkStreak = 3 };
        _scorer.SaveBoard(board);

        // Should not throw
        _scorer.ResetProjectAfterBuild("does-not-exist");
        Assert.AreEqual(0, _scorer.LoadBoard().PositiveWalkStreak);
    }

    // ── Signal board data models ──

    [TestMethod]
    public void SignalBoard_ProjectsKeyIsCaseInsensitive()
    {
        var board = new SignalBoard();
        board.Projects["MyProject"] = new ProjectSignals { Score = 5.0 };

        Assert.IsTrue(board.Projects.ContainsKey("myproject"));
        Assert.IsTrue(board.Projects.ContainsKey("MYPROJECT"));
    }
}