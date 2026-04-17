using Hermes.Agent.Permissions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

[TestClass]
public sealed class WorkspacePermissionRuleStoreTests
{
    [TestMethod]
    public void LoadAlwaysAllowRules_MissingFile_ReturnsEmptyList()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root, "/tmp/workspace-a");

            var rules = store.LoadAlwaysAllowRules();

            Assert.AreEqual(0, rules.Count);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void SaveAlwaysAllowRules_ThenLoad_ReturnsPersistedRules()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root, "/tmp/workspace-a");
            var rules = new[]
            {
                new PermissionRule { ToolName = "bash" },
                new PermissionRule { ToolName = "write_file", Pattern = "**/*.md" }
            };

            store.SaveAlwaysAllowRules(rules);
            var reloaded = store.LoadAlwaysAllowRules();

            Assert.AreEqual(2, reloaded.Count);
            Assert.AreEqual("bash", reloaded[0].ToolName);
            Assert.AreEqual("write_file", reloaded[1].ToolName);
            Assert.AreEqual("**/*.md", reloaded[1].Pattern);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void SaveAlwaysAllowRules_DuplicateEntries_AreDeduplicated()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root, "/tmp/workspace-a");
            var rules = new[]
            {
                new PermissionRule { ToolName = "bash" },
                new PermissionRule { ToolName = "BASH" },
                new PermissionRule { ToolName = "write_file", Pattern = "**/*.md" },
                new PermissionRule { ToolName = "write_file", Pattern = "  **/*.md  " }
            };

            store.SaveAlwaysAllowRules(rules);
            var reloaded = store.LoadAlwaysAllowRules();

            Assert.AreEqual(2, reloaded.Count);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void SaveAlwaysAllowRules_DifferentWorkspaces_AreIsolated()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspaceAStore = CreateStore(root, "/tmp/workspace-a");
            var workspaceBStore = CreateStore(root, "/tmp/workspace-b");
            workspaceAStore.SaveAlwaysAllowRules(new[]
            {
                new PermissionRule { ToolName = "bash" }
            });
            workspaceBStore.SaveAlwaysAllowRules(new[]
            {
                new PermissionRule { ToolName = "write_file" }
            });

            var rulesA = workspaceAStore.LoadAlwaysAllowRules();
            var rulesB = workspaceBStore.LoadAlwaysAllowRules();

            Assert.AreEqual(1, rulesA.Count);
            Assert.AreEqual("bash", rulesA[0].ToolName);
            Assert.AreEqual(1, rulesB.Count);
            Assert.AreEqual("write_file", rulesB[0].ToolName);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void LoadAlwaysAllowRules_InvalidJson_ReturnsEmptyList()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root, "/tmp/workspace-a");
            File.WriteAllText(store.WorkspaceFilePath, "{invalid-json");

            var rules = store.LoadAlwaysAllowRules();

            Assert.AreEqual(0, rules.Count);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void ClearAlwaysAllowRules_RemovesWorkspaceFile_AndReturnsNoRules()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root, "/tmp/workspace-a");
            store.SaveAlwaysAllowRules(new[]
            {
                new PermissionRule { ToolName = "bash" }
            });

            Assert.IsTrue(File.Exists(store.WorkspaceFilePath));

            store.ClearAlwaysAllowRules();
            var reloaded = store.LoadAlwaysAllowRules();

            Assert.IsFalse(File.Exists(store.WorkspaceFilePath));
            Assert.AreEqual(0, reloaded.Count);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    // ── Additional edge-case tests for WorkspacePermissionRuleStore ──

    [TestMethod]
    public void Constructor_NullPermissionsDir_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            new WorkspacePermissionRuleStore(
                null!,
                "/tmp/workspace-a",
                NullLogger<WorkspacePermissionRuleStore>.Instance));
    }

    [TestMethod]
    public void Constructor_WhitespacePermissionsDir_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            new WorkspacePermissionRuleStore(
                "   ",
                "/tmp/workspace-a",
                NullLogger<WorkspacePermissionRuleStore>.Instance));
    }

    [TestMethod]
    public void Constructor_NullWorkspacePath_ThrowsArgumentException()
    {
        var root = CreateTempDirectory();
        try
        {
            Assert.ThrowsException<ArgumentException>(() =>
                new WorkspacePermissionRuleStore(
                    root,
                    null!,
                    NullLogger<WorkspacePermissionRuleStore>.Instance));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Constructor_WhitespaceWorkspacePath_ThrowsArgumentException()
    {
        var root = CreateTempDirectory();
        try
        {
            Assert.ThrowsException<ArgumentException>(() =>
                new WorkspacePermissionRuleStore(
                    root,
                    "   ",
                    NullLogger<WorkspacePermissionRuleStore>.Instance));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void SaveAlwaysAllowRules_NullRules_ThrowsArgumentNullException()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root, "/tmp/workspace-a");

            Assert.ThrowsException<ArgumentNullException>(() =>
                store.SaveAlwaysAllowRules(null!));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void SaveAlwaysAllowRules_EmptyCollection_LoadReturnsEmptyList()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root, "/tmp/workspace-a");

            store.SaveAlwaysAllowRules(Array.Empty<PermissionRule>());
            var reloaded = store.LoadAlwaysAllowRules();

            Assert.AreEqual(0, reloaded.Count);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void SaveAlwaysAllowRules_SkipsEntriesWithBlankToolName()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root, "/tmp/workspace-a");
            var rules = new[]
            {
                new PermissionRule { ToolName = "" },
                new PermissionRule { ToolName = "   " },
                new PermissionRule { ToolName = "bash" }
            };

            store.SaveAlwaysAllowRules(rules);
            var reloaded = store.LoadAlwaysAllowRules();

            // Only the valid "bash" entry should survive.
            Assert.AreEqual(1, reloaded.Count);
            Assert.AreEqual("bash", reloaded[0].ToolName);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void WorkspaceFilePath_IsDeterministic_ForSameWorkspacePath()
    {
        var root = CreateTempDirectory();
        try
        {
            var storeA = CreateStore(root, "/tmp/workspace-a");
            var storeB = CreateStore(root, "/tmp/workspace-a");

            Assert.AreEqual(storeA.WorkspaceFilePath, storeB.WorkspaceFilePath);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void WorkspaceFilePath_IsDifferent_ForDifferentWorkspacePaths()
    {
        var root = CreateTempDirectory();
        try
        {
            var storeA = CreateStore(root, "/tmp/workspace-a");
            var storeB = CreateStore(root, "/tmp/workspace-b");

            Assert.AreNotEqual(storeA.WorkspaceFilePath, storeB.WorkspaceFilePath);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void SaveAlwaysAllowRules_Overwrite_ReplacesExistingRules()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root, "/tmp/workspace-a");
            store.SaveAlwaysAllowRules(new[] { new PermissionRule { ToolName = "bash" } });

            // Second save with different rules must replace the first.
            store.SaveAlwaysAllowRules(new[] { new PermissionRule { ToolName = "write_file" } });
            var reloaded = store.LoadAlwaysAllowRules();

            Assert.AreEqual(1, reloaded.Count);
            Assert.AreEqual("write_file", reloaded[0].ToolName);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void ClearAlwaysAllowRules_WhenFileDoesNotExist_DoesNotThrow()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root, "/tmp/workspace-a");

            // File has never been created — Clear must be a no-op, not throw.
            store.ClearAlwaysAllowRules();

            Assert.IsFalse(File.Exists(store.WorkspaceFilePath));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void SaveAndLoad_PreservesNullPattern()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root, "/tmp/workspace-a");
            store.SaveAlwaysAllowRules(new[]
            {
                new PermissionRule { ToolName = "bash", Pattern = null }
            });

            var reloaded = store.LoadAlwaysAllowRules();

            Assert.AreEqual(1, reloaded.Count);
            Assert.IsNull(reloaded[0].Pattern);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void LoadAlwaysAllowRules_TrimsWhitespaceFromPersistedToolNames()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root, "/tmp/workspace-a");
            // SaveAlwaysAllowRules normalizes on save, so we write a manually
            // crafted JSON file that contains untrimmed tool names to verify
            // that LoadAlwaysAllowRules also trims defensively on load.
            Directory.CreateDirectory(root);
            var json = """
                {
                  "WorkspacePath": "/tmp/workspace-a",
                  "AlwaysAllow": [
                    { "ToolName": "  bash  ", "Pattern": null },
                    { "ToolName": "write_file", "Pattern": "  **/*.md  " }
                  ]
                }
                """;
            File.WriteAllText(store.WorkspaceFilePath, json);

            var rules = store.LoadAlwaysAllowRules();

            Assert.AreEqual(2, rules.Count);
            Assert.AreEqual("bash", rules[0].ToolName);
            Assert.AreEqual("**/*.md", rules[1].Pattern);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void LoadAlwaysAllowRules_EmptyJsonPayload_ReturnsEmptyList()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root, "/tmp/workspace-a");
            Directory.CreateDirectory(root);
            // Valid JSON but with no AlwaysAllow entries.
            File.WriteAllText(store.WorkspaceFilePath, """{"WorkspacePath":"/tmp/workspace-a","AlwaysAllow":[]}""");

            var rules = store.LoadAlwaysAllowRules();

            Assert.AreEqual(0, rules.Count);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static WorkspacePermissionRuleStore CreateStore(string root, string workspacePath)
    {
        return new WorkspacePermissionRuleStore(
            root,
            workspacePath,
            NullLogger<WorkspacePermissionRuleStore>.Instance);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "WorkspacePermissionRuleStoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}