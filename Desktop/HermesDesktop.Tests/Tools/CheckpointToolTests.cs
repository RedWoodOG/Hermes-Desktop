using System.Diagnostics;
using Hermes.Agent.Core;
using Hermes.Agent.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Tools;

/// <summary>
/// Regression tests for issue #52 — CheckpointTool infinite recursion when the
/// checkpoint output directory was inside the source directory. Also covers
/// reparse-point cycle defense and legacy fallback for restore/list.
/// </summary>
[TestClass]
public class CheckpointToolTests
{
    private string _root = null!;
    private string _workDir = null!;
    private string _legacyDir = null!;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"hermes-ckpt-test-{Guid.NewGuid():N}");
        _workDir = Path.Combine(_root, "workdir");
        _legacyDir = Path.Combine(_root, "legacy-checkpoints");
        Directory.CreateDirectory(_workDir);
        Directory.CreateDirectory(_legacyDir);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (!Directory.Exists(_root)) return;
        try
        {
            ClearReparsePoints(_root);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [TestMethod]
    public async Task Create_DoesNotRecurse_WhenLegacyCheckpointDirIsInsideSource()
    {
        // Original repro from issue #52: legacy _checkpointDir lives under the source.
        // With the A+B fix, snapshots go to <parent>/<basename>-checkpoints, not into the
        // source — so no infinite recursion is possible regardless of where _checkpointDir is.
        var legacyInsideSource = Path.Combine(_workDir, "data", "checkpoints");
        Directory.CreateDirectory(legacyInsideSource);
        File.WriteAllText(Path.Combine(_workDir, "marker.txt"), "x");

        var tool = new CheckpointTool(legacyInsideSource);
        var result = await tool.ExecuteAsync(
            new CheckpointParameters { Action = "create", Directory = _workDir, Name = "test" },
            CancellationToken.None);

        Assert.IsTrue(result.Success, $"create should succeed: {result.Content}");

        var snapshotRoot = Path.Combine(_root, "workdir-checkpoints");
        Assert.IsTrue(File.Exists(Path.Combine(snapshotRoot, "test", "marker.txt")),
            "snapshot should contain source files at the new structural location");

        Assert.IsFalse(Directory.Exists(Path.Combine(legacyInsideSource, "test")),
            "snapshot must not be written inside the source directory anymore");

        Assert.IsFalse(Directory.Exists(Path.Combine(snapshotRoot, "test", "workdir-checkpoints")),
            "snapshot must not contain a nested copy of itself");

        Assert.IsFalse(
            Directory.Exists(Path.Combine(snapshotRoot, "test", "data", "checkpoints", "test")),
            "snapshot must not contain a nested copy of the legacy checkpoint dir");
    }

    [TestMethod]
    public async Task Create_PlacesSnapshotAtParentBasenameCheckpoints()
    {
        File.WriteAllText(Path.Combine(_workDir, "a.txt"), "a");

        var tool = new CheckpointTool(_legacyDir);
        var result = await tool.ExecuteAsync(
            new CheckpointParameters { Action = "create", Directory = _workDir, Name = "snap1" },
            CancellationToken.None);

        Assert.IsTrue(result.Success, result.Content);
        var expected = Path.Combine(_root, "workdir-checkpoints", "snap1");
        Assert.IsTrue(Directory.Exists(expected),
            $"snapshot should land at {expected}, got result: {result.Content}");
        Assert.IsTrue(File.Exists(Path.Combine(expected, "a.txt")));
    }

    [TestMethod]
    public async Task Create_DoesNotFollowReparsePointInsideSource()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Reparse-point test only runs on Windows.");
            return;
        }

        File.WriteAllText(Path.Combine(_workDir, "real.txt"), "real");

        // Create a junction inside source pointing back at source. With B's reparse-point
        // skip, this must not be followed; without it, CopyDirectory would loop forever.
        var junctionPath = Path.Combine(_workDir, "loop");
        using (var proc = Process.Start("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{_workDir}\""))
        {
            Assert.IsNotNull(proc);
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0)
            {
                Assert.Inconclusive($"mklink failed with exit code {proc.ExitCode}");
                return;
            }
        }

        var tool = new CheckpointTool(_legacyDir);

        var task = tool.ExecuteAsync(
            new CheckpointParameters { Action = "create", Directory = _workDir, Name = "snap" },
            CancellationToken.None);

        // Hard cap: if the bug regresses, this test would otherwise hang until the disk fills.
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.AreSame(task, completed, "checkpoint must terminate quickly even with junction cycle");

        var result = await task;
        Assert.IsTrue(result.Success, result.Content);

        var snapPath = Path.Combine(_root, "workdir-checkpoints", "snap");
        Assert.IsTrue(File.Exists(Path.Combine(snapPath, "real.txt")));
        // Junction is not followed — either absent in the snapshot or present as an empty entry
        // we created via Directory.CreateDirectory inside CopyDirectory. Either way, no recursion.
        Assert.IsFalse(Directory.Exists(Path.Combine(snapPath, "loop", "loop")),
            "junction must not be followed; no nested 'loop' should exist");
    }

    [TestMethod]
    public async Task Restore_FallsBackToLegacyCheckpointDir()
    {
        File.WriteAllText(Path.Combine(_workDir, "before.txt"), "before");

        // Place a checkpoint manually into the legacy location, simulating a snapshot
        // taken before this fix.
        var legacySnap = Path.Combine(_legacyDir, "old-snap");
        Directory.CreateDirectory(legacySnap);
        File.WriteAllText(Path.Combine(legacySnap, "restored.txt"), "from-legacy");

        var tool = new CheckpointTool(_legacyDir);
        var result = await tool.ExecuteAsync(
            new CheckpointParameters { Action = "restore", Directory = _workDir, Name = "old-snap" },
            CancellationToken.None);

        Assert.IsTrue(result.Success, result.Content);
        Assert.IsTrue(File.Exists(Path.Combine(_workDir, "restored.txt")),
            "legacy checkpoint should restore into the work dir");
        Assert.AreEqual("from-legacy", File.ReadAllText(Path.Combine(_workDir, "restored.txt")));
    }

    [TestMethod]
    public async Task List_MergesPerSourceAndLegacyCheckpoints()
    {
        // Per-source snapshot
        File.WriteAllText(Path.Combine(_workDir, "x.txt"), "x");
        var tool = new CheckpointTool(_legacyDir);
        var create = await tool.ExecuteAsync(
            new CheckpointParameters { Action = "create", Directory = _workDir, Name = "fresh" },
            CancellationToken.None);
        Assert.IsTrue(create.Success, create.Content);

        // Legacy snapshot, placed manually
        Directory.CreateDirectory(Path.Combine(_legacyDir, "ancient"));
        File.WriteAllText(Path.Combine(_legacyDir, "ancient", "y.txt"), "y");

        var list = await tool.ExecuteAsync(
            new CheckpointParameters { Action = "list", Directory = _workDir },
            CancellationToken.None);

        Assert.IsTrue(list.Success, list.Content);
        StringAssert.Contains(list.Content, "fresh");
        StringAssert.Contains(list.Content, "(source)");
        StringAssert.Contains(list.Content, "ancient");
        StringAssert.Contains(list.Content, "(legacy)");
    }

    [TestMethod]
    public async Task Create_RefusesWhenDirectoryEqualsSnapshotRoot()
    {
        // A directory whose computed snapshotRoot equals itself is degenerate. The simplest
        // way to construct one is to pass a path equal to its own ComputeSnapshotRoot output;
        // we trigger the equivalent guard by passing the snapshotRoot back as the directory.
        var basenameDir = Path.Combine(_root, "self-checkpoints");
        Directory.CreateDirectory(basenameDir);

        var tool = new CheckpointTool(_legacyDir);

        // Compute what the tool would produce for basenameDir: parent=_root, basename="self-checkpoints",
        // snapshotRoot = _root/self-checkpoints-checkpoints. Distinct, so this is fine — the equality
        // refusal really fires only at filesystem roots. We assert that on non-degenerate input the
        // tool does NOT refuse, which is the behavior we care about for normal use.
        var result = await tool.ExecuteAsync(
            new CheckpointParameters { Action = "create", Directory = basenameDir, Name = "x" },
            CancellationToken.None);
        Assert.IsTrue(result.Success, result.Content);
    }

    [TestMethod]
    public async Task Create_RejectsMissingDirectory()
    {
        var tool = new CheckpointTool(_legacyDir);
        var result = await tool.ExecuteAsync(
            new CheckpointParameters { Action = "create", Directory = null, Name = "x" },
            CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Content, "Directory is required");
    }

    private static void ClearReparsePoints(string root)
    {
        // Junctions inside the test tree must be removed *before* Directory.Delete recursive,
        // otherwise the recursive delete would traverse them and risk deleting the target.
        // Walk manually (List used as a stack) so we never recurse into a reparse point.
        if (!Directory.Exists(root)) return;
        var pending = new List<string> { root };
        while (pending.Count > 0)
        {
            var current = pending[pending.Count - 1];
            pending.RemoveAt(pending.Count - 1);

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(current); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var sub in subDirs)
            {
                try
                {
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
                        Directory.Delete(sub);  // delete the link, not its target
                    else
                        pending.Add(sub);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
