using Hermes.Agent.Dreamer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Dreamer;

[TestClass]
public class BuildSprintTests
{
    private string _tempDir = "";
    private DreamerRoom _room = null!;
    private BuildSprint _sprint = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"build-sprint-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _room = new DreamerRoom(_tempDir);
        _room.EnsureLayout();
        _sprint = new BuildSprint(_room, NullLogger<BuildSprint>.Instance);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── RunAsync creates expected files ──

    [TestMethod]
    public async Task RunAsync_ValidSlug_CreatesProjectDirectory()
    {
        await _sprint.RunAsync("my-project", "walk excerpt", "full", CancellationToken.None);

        Assert.IsTrue(Directory.Exists(Path.Combine(_room.ProjectsDir, "my-project")));
    }

    [TestMethod]
    public async Task RunAsync_ValidSlug_CreatesReadme()
    {
        await _sprint.RunAsync("test-proj", "walk content here", "full", CancellationToken.None);

        var readmePath = Path.Combine(_room.ProjectsDir, "test-proj", "README.md");
        Assert.IsTrue(File.Exists(readmePath));
    }

    [TestMethod]
    public async Task RunAsync_ReadmeContainsSlug()
    {
        await _sprint.RunAsync("cool-feature", "walk excerpt", "full", CancellationToken.None);

        var content = await File.ReadAllTextAsync(Path.Combine(_room.ProjectsDir, "cool-feature", "README.md"));
        Assert.IsTrue(content.Contains("cool-feature"), "README should reference the slug");
    }

    [TestMethod]
    public async Task RunAsync_ReadmeContainsAutonomyMode()
    {
        await _sprint.RunAsync("feature-x", "walk", "drafts", CancellationToken.None);

        var content = await File.ReadAllTextAsync(Path.Combine(_room.ProjectsDir, "feature-x", "README.md"));
        Assert.IsTrue(content.Contains("drafts"), "README should show autonomy mode");
    }

    [TestMethod]
    public async Task RunAsync_ReadmeContainsWalkExcerpt()
    {
        await _sprint.RunAsync("proj", "unique-walk-content-xyz", "full", CancellationToken.None);

        var content = await File.ReadAllTextAsync(Path.Combine(_room.ProjectsDir, "proj", "README.md"));
        Assert.IsTrue(content.Contains("unique-walk-content-xyz"));
    }

    // ── SPRINT.md creation based on autonomy ──

    [TestMethod]
    public async Task RunAsync_AutonomyFull_CreateSprintMd()
    {
        await _sprint.RunAsync("sprint-full", "walk text", "full", CancellationToken.None);

        Assert.IsTrue(File.Exists(Path.Combine(_room.ProjectsDir, "sprint-full", "SPRINT.md")));
    }

    [TestMethod]
    public async Task RunAsync_AutonomyDrafts_CreateSprintMd()
    {
        await _sprint.RunAsync("sprint-drafts", "walk text", "drafts", CancellationToken.None);

        Assert.IsTrue(File.Exists(Path.Combine(_room.ProjectsDir, "sprint-drafts", "SPRINT.md")));
    }

    [TestMethod]
    public async Task RunAsync_AutonomyIdeas_DoesNotCreateSprintMd()
    {
        await _sprint.RunAsync("sprint-ideas", "walk text", "ideas", CancellationToken.None);

        Assert.IsFalse(File.Exists(Path.Combine(_room.ProjectsDir, "sprint-ideas", "SPRINT.md")),
            "ideas autonomy should NOT write SPRINT.md");
    }

    [TestMethod]
    public async Task RunAsync_AutonomyIdeasCaseInsensitive_DoesNotCreateSprintMd()
    {
        await _sprint.RunAsync("sprint-ideas2", "walk text", "IDEAS", CancellationToken.None);

        Assert.IsFalse(File.Exists(Path.Combine(_room.ProjectsDir, "sprint-ideas2", "SPRINT.md")));
    }

    // ── Long walk excerpt is truncated ──

    [TestMethod]
    public async Task RunAsync_LongWalkExcerpt_TruncatedTo8000Chars()
    {
        var longText = new string('A', 10_000);
        await _sprint.RunAsync("long-proj", longText, "full", CancellationToken.None);

        var content = await File.ReadAllTextAsync(Path.Combine(_room.ProjectsDir, "long-proj", "README.md"));
        // README contains additional uppercase 'A' from template text; assert the longest
        // contiguous run from the seed excerpt is capped at 8000.
        var longestRun = 0;
        var currentRun = 0;
        foreach (var ch in content)
        {
            if (ch == 'A')
            {
                currentRun++;
                if (currentRun > longestRun)
                    longestRun = currentRun;
            }
            else
            {
                currentRun = 0;
            }
        }

        Assert.AreEqual(8000, longestRun, $"Expected longest contiguous excerpt run of 8000, got {longestRun}");
    }

    // ── Slug sanitization ──

    [TestMethod]
    public async Task RunAsync_SlugWithDotDot_IsSanitizedAndCreatesProjectDirectory()
    {
        await _sprint.RunAsync("../evil", "walk", "full", CancellationToken.None);

        Assert.IsTrue(Directory.Exists(Path.Combine(_room.ProjectsDir, "evil")));
    }

    [TestMethod]
    public async Task RunAsync_EmptySlug_ReturnsWithoutCreatingProject()
    {
        await _sprint.RunAsync("", "walk", "full", CancellationToken.None);

        Assert.AreEqual(0, Directory.EnumerateDirectories(_room.ProjectsDir).Count());
    }

    [TestMethod]
    public async Task RunAsync_WhitespaceSlug_ReturnsWithoutCreatingProject()
    {
        await _sprint.RunAsync("   ", "walk", "full", CancellationToken.None);

        Assert.AreEqual(0, Directory.EnumerateDirectories(_room.ProjectsDir).Count());
    }

    [TestMethod]
    public async Task RunAsync_SlugWithForwardSlash_SanitizedToHyphen()
    {
        // "a/b" → "a-b" after sanitization
        await _sprint.RunAsync("a/b", "walk", "full", CancellationToken.None);

        Assert.IsTrue(Directory.Exists(Path.Combine(_room.ProjectsDir, "a-b")));
    }

    [TestMethod]
    public async Task RunAsync_SlugWithBackslash_SanitizedToHyphen()
    {
        await _sprint.RunAsync("a\\b", "walk", "full", CancellationToken.None);

        Assert.IsTrue(Directory.Exists(Path.Combine(_room.ProjectsDir, "a-b")));
    }

    [TestMethod]
    public async Task RunAsync_SlugWithDoubleDotEmbedded_DoubleDotStripped()
    {
        // "a..b" → "a-b" after separator normalization and trim.
        await _sprint.RunAsync("a..b", "walk", "full", CancellationToken.None);

        Assert.IsTrue(Directory.Exists(Path.Combine(_room.ProjectsDir, "a-b")));
    }

    // ── Cancellation support ──

    [TestMethod]
    public async Task RunAsync_CancelledToken_ThrowsTaskCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            () => _sprint.RunAsync("cancel-proj", "walk text", "full", cts.Token));
    }

    // ── SPRINT.md content check ──

    [TestMethod]
    public async Task RunAsync_SprintMd_ContainsChecklistItems()
    {
        await _sprint.RunAsync("checklist-proj", "walk text", "full", CancellationToken.None);

        var content = await File.ReadAllTextAsync(Path.Combine(_room.ProjectsDir, "checklist-proj", "SPRINT.md"));
        Assert.IsTrue(content.Contains("[ ]"), "SPRINT.md should contain unchecked checklist items");
    }
}