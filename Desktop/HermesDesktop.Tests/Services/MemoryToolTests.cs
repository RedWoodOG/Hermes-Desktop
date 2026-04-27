using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Hermes.Agent.Memory;
using Hermes.Agent.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace HermesDesktop.Tests.Services;

/// <summary>
/// Regression tests for PR #43 — MemoryTool/MemoryManager directory alignment
/// and recall indexability. These tests cover the exact symptoms reported in
/// issue #42: "agent cannot read the memory files it creates."
/// </summary>
[TestClass]
public class MemoryToolTests
{
    private string _tempDir = null!;
    private MemoryManager _manager = null!;
    private MemoryTool _tool = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hermes-memtool-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var chatClient = new Mock<IChatClient>(MockBehavior.Loose).Object;
        _manager = new MemoryManager(_tempDir, chatClient, NullLogger<MemoryManager>.Instance);
        _tool = new MemoryTool(_manager);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Save / recall alignment (the #42 bug) ──

    [TestMethod]
    public async Task Save_WritesFileIntoMemoryManagerDir()
    {
        var result = await _tool.ExecuteAsync(
            new MemoryToolParameters { Action = "save", Content = "remember the alamo", Type = "user" },
            CancellationToken.None);

        Assert.IsTrue(result.Success, "save should succeed");
        var files = Directory.GetFiles(_manager.MemoryDir, "*.md");
        Assert.AreEqual(1, files.Length, "exactly one file should exist in MemoryManager's dir");
    }

    [TestMethod]
    public async Task Save_WritesYamlFrontmatter_SoManagerCanIndexIt()
    {
        // The #42 regression: MemoryTool used to write plain content. MemoryManager's
        // scanner requires the first line to be "---" (YAML frontmatter), so tool-saved
        // memories were silently invisible to auto-recall.
        await _tool.ExecuteAsync(
            new MemoryToolParameters { Action = "save", Content = "plain body", Type = "user" },
            CancellationToken.None);

        var files = Directory.GetFiles(_manager.MemoryDir, "*.md");
        Assert.AreEqual(1, files.Length);
        var lines = File.ReadAllLines(files[0]);
        Assert.AreEqual("---", lines[0].Trim(), "first line must be YAML frontmatter delimiter");
        Assert.IsTrue(lines.Any(l => l.StartsWith("type:")), "frontmatter must include type field");
    }

    [TestMethod]
    public async Task Save_ThenList_RoundTrips()
    {
        await _tool.ExecuteAsync(
            new MemoryToolParameters { Action = "save", Content = "first memory" },
            CancellationToken.None);
        await _tool.ExecuteAsync(
            new MemoryToolParameters { Action = "save", Content = "second memory" },
            CancellationToken.None);

        var listResult = await _tool.ExecuteAsync(
            new MemoryToolParameters { Action = "list" },
            CancellationToken.None);

        Assert.IsTrue(listResult.Success);
        var lines = listResult.Content.Split('\n');
        Assert.AreEqual(2, lines.Length, "list should surface both saved files");
    }

    [TestMethod]
    public async Task Save_EmptyContent_Fails()
    {
        var result = await _tool.ExecuteAsync(
            new MemoryToolParameters { Action = "save", Content = "" },
            CancellationToken.None);

        Assert.IsFalse(result.Success);
    }

    // ── Path traversal guards (Qodo security review) ──

    [TestMethod]
    public async Task Delete_RejectsDotDotTraversal()
    {
        var result = await _tool.ExecuteAsync(
            new MemoryToolParameters { Action = "delete", Filename = "../evil.md" },
            CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Content, "Invalid filename");
    }

    [TestMethod]
    public async Task Delete_RejectsBackslashTraversal()
    {
        var result = await _tool.ExecuteAsync(
            new MemoryToolParameters { Action = "delete", Filename = @"..\..\evil.md" },
            CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Content, "Invalid filename");
    }

    [TestMethod]
    public async Task Delete_RejectsForwardSlashPath()
    {
        var result = await _tool.ExecuteAsync(
            new MemoryToolParameters { Action = "delete", Filename = "sub/file.md" },
            CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Content, "Invalid filename");
    }

    [TestMethod]
    public async Task Delete_RejectsAbsolutePath()
    {
        var rooted = Path.Combine(Path.GetTempPath(), "absolute.md");
        var result = await _tool.ExecuteAsync(
            new MemoryToolParameters { Action = "delete", Filename = rooted },
            CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Content, "Invalid filename");
    }

    [TestMethod]
    public async Task Delete_AcceptsPlainFilename()
    {
        // First save a file so there's something to delete.
        var saveResult = await _tool.ExecuteAsync(
            new MemoryToolParameters { Action = "save", Content = "kill me" },
            CancellationToken.None);
        Assert.IsTrue(saveResult.Success);

        var filename = Directory.GetFiles(_manager.MemoryDir, "*.md")
            .Select(Path.GetFileName)
            .First()!;

        var deleteResult = await _tool.ExecuteAsync(
            new MemoryToolParameters { Action = "delete", Filename = filename },
            CancellationToken.None);

        Assert.IsTrue(deleteResult.Success);
        Assert.AreEqual(0, Directory.GetFiles(_manager.MemoryDir, "*.md").Length);
    }
}
