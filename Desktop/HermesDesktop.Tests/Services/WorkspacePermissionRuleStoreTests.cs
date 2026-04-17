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
