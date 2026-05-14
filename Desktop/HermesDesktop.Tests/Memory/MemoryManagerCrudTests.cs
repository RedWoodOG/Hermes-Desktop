using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hermes.Agent.LLM;
using Hermes.Agent.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace HermesDesktop.Tests.Memory;

/// <summary>
/// Bundle E.5 tests for the MemoryManager CRUD methods that the new MemoryPage UI
/// depends on (Save / Delete / LoadAll). Validates the path is consistent and that
/// frontmatter is preserved on save.
/// </summary>
[TestClass]
public class MemoryManagerCrudTests
{
    private string _tempDir = "";
    private MemoryManager _manager = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hermes-mem-tests-{Guid.NewGuid():N}");
        var mockChatClient = new Mock<IChatClient>(MockBehavior.Loose);
        _manager = new MemoryManager(_tempDir, mockChatClient.Object, NullLogger<MemoryManager>.Instance);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void Constructor_OnInit_CreatesMemoryDir()
    {
        Assert.IsTrue(Directory.Exists(_tempDir), "MemoryManager should create its memory dir on construction.");
        Assert.AreEqual(_tempDir, _manager.MemoryDir);
    }

    [TestMethod]
    public async Task SaveMemoryAsync_AddsFrontmatter_WhenAbsent()
    {
        await _manager.SaveMemoryAsync("test.md", "Bare content with no frontmatter.", "user", CancellationToken.None);

        var path = Path.Combine(_tempDir, "test.md");
        Assert.IsTrue(File.Exists(path));

        var content = await File.ReadAllTextAsync(path);
        StringAssert.StartsWith(content, "---");
        StringAssert.Contains(content, "type: user");
        StringAssert.Contains(content, "Bare content with no frontmatter.");
    }

    [TestMethod]
    public async Task SaveMemoryAsync_PreservesFrontmatter_WhenPresent()
    {
        const string original = "---\nname: My Memory\ntype: feedback\n---\nUser-edited body.";
        await _manager.SaveMemoryAsync("user-edit.md", original, "feedback", CancellationToken.None);

        var content = await File.ReadAllTextAsync(Path.Combine(_tempDir, "user-edit.md"));
        Assert.AreEqual(original, content);
    }

    [TestMethod]
    public async Task DeleteMemoryAsync_ExistingFile_RemovesFile()
    {
        await _manager.SaveMemoryAsync("kill-me.md", "Body", "user", CancellationToken.None);
        var path = Path.Combine(_tempDir, "kill-me.md");
        Assert.IsTrue(File.Exists(path));

        await _manager.DeleteMemoryAsync("kill-me.md", CancellationToken.None);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task DeleteMemoryAsync_MissingFile_NoOp()
    {
        // Should not throw if the file doesn't exist.
        await _manager.DeleteMemoryAsync("never-existed.md", CancellationToken.None);
    }

    [TestMethod]
    public async Task LoadAllMemoriesAsync_AfterSaves_ReturnsSavedEntries()
    {
        await _manager.SaveMemoryAsync("a.md", "First", "user", CancellationToken.None);
        await _manager.SaveMemoryAsync("b.md", "Second", "feedback", CancellationToken.None);

        var loaded = await _manager.LoadAllMemoriesAsync(CancellationToken.None);

        Assert.AreEqual(2, loaded.Count);
        var filenames = loaded.Select(m => m.Filename).OrderBy(f => f).ToArray();
        CollectionAssert.AreEqual(new[] { "a.md", "b.md" }, filenames);
    }

    [TestMethod]
    public async Task LoadAllMemoriesAsync_MixedFiles_SkipsFilesWithoutFrontmatter()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "no-frontmatter.md"),
            "Just some text with no YAML frontmatter at all.");

        var loaded = await _manager.LoadAllMemoriesAsync(CancellationToken.None);

        // MemoryManager's ScanMemoryFilesAsync requires "---" at line 0 to count as a memory.
        Assert.AreEqual(0, loaded.Count);
    }

    [TestMethod]
    public async Task RoundTrip_SaveThenLoadAll_PreservesContent()
    {
        const string body = "This is the body text.";
        await _manager.SaveMemoryAsync("rt.md", body, "user", CancellationToken.None);

        var loaded = await _manager.LoadAllMemoriesAsync(CancellationToken.None);

        Assert.AreEqual(1, loaded.Count);
        StringAssert.Contains(loaded[0].Content, body);
    }
}
