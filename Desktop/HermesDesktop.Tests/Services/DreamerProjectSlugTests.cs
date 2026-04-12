using Hermes.Agent.Dreamer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

[TestClass]
public sealed class DreamerProjectSlugTests
{
    [TestMethod]
    public void Normalize_ReplacesSeparatorsAndLowercases()
    {
        var normalized = DreamerProjectSlug.Normalize("My Cool/Idea");

        Assert.AreEqual("my-cool-idea", normalized);
    }

    [TestMethod]
    public void Normalize_ReturnsEmpty_WhenOnlySeparatorsRemain()
    {
        var normalized = DreamerProjectSlug.Normalize("...///\\\\   ");

        Assert.AreEqual(string.Empty, normalized);
    }

    [TestMethod]
    public void TryNormalize_ReturnsFalse_ForRootedPath()
    {
        var ok = DreamerProjectSlug.TryNormalize("C:\\temp\\dream", out var normalized);

        Assert.IsFalse(ok);
        Assert.AreEqual(string.Empty, normalized);
    }

    [TestMethod]
    public async Task BuildSprint_RunAsync_ReturnsWithoutCreatingProject_WhenSlugIsInvalid()
    {
        var hermesHome = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var room = new DreamerRoom(hermesHome);
            room.EnsureLayout();
            var sprint = new BuildSprint(room, NullLogger<BuildSprint>.Instance);

            await sprint.RunAsync("C:\\temp\\dream", "walk excerpt", "ideas", CancellationToken.None);

            Assert.AreEqual(0, Directory.EnumerateDirectories(room.ProjectsDir).Count());
        }
        finally
        {
            if (Directory.Exists(hermesHome))
                Directory.Delete(hermesHome, recursive: true);
        }
    }

    [TestMethod]
    public void ShouldTriggerBuild_UsesNormalizedSlugWhenMatchingSignalLog()
    {
        var hermesHome = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var room = new DreamerRoom(hermesHome);
            room.EnsureLayout();

            var scorer = new SignalScorer(room, NullLogger<SignalScorer>.Instance);
            var normalized = DreamerProjectSlug.Normalize("My Cool/Idea");
            var board = new SignalBoard
            {
                PositiveWalkStreak = 1,
                LastWalkUtc = DateTime.UtcNow,
                Projects =
                {
                    [normalized] = new ProjectSignals
                    {
                        Score = 2.0,
                        SignalTypes = new List<string> { "commit" }
                    }
                }
            };

            scorer.SaveBoard(board);
            scorer.AppendSignalLog(new SignalEvent { Utc = DateTime.UtcNow, Type = "commit", ProjectKey = normalized, Delta = 1.0, EchoScore = 1 });
            scorer.AppendSignalLog(new SignalEvent { Utc = DateTime.UtcNow, Type = "mention", ProjectKey = normalized, Delta = 1.0, EchoScore = 1 });

            var shouldTrigger = scorer.ShouldTriggerBuild(
                "My Cool/Idea",
                new DreamerConfig { MinWalksToTrigger = 1, TriggerThreshold = 1.0 },
                out var signals);

            Assert.IsTrue(shouldTrigger);
            Assert.IsNotNull(signals);
            Assert.AreEqual(2.0, signals.Score);
        }
        finally
        {
            if (Directory.Exists(hermesHome))
                Directory.Delete(hermesHome, recursive: true);
        }
    }
}
