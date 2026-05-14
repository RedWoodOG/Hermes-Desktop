using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hermes.Agent.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Skills;

/// <summary>
/// Bundle E.6 tests for the new <see cref="Skill.IsEnabled"/> toggle and persistence
/// via <c>.skill-toggles.json</c>, plus the <see cref="SkillManager.ListEnabledSkills"/>
/// filter used by runtime dispatch paths.
/// </summary>
[TestClass]
public class SkillToggleTests
{
    private string _tempDir = "";

    [TestInitialize]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hermes-skill-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        WriteSkill("alpha", "First skill.");
        WriteSkill("beta", "Second skill.");
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private void WriteSkill(string name, string description)
    {
        var content =
            $"---\nname: {name}\ndescription: {description}\ntools: read_file\n---\n# {name}\n\nBody.";
        File.WriteAllText(Path.Combine(_tempDir, $"{name}.md"), content);
    }

    private SkillManager NewManager() =>
        new(_tempDir, NullLogger<SkillManager>.Instance);

    [TestMethod]
    public void IsEnabled_FreshSkill_DefaultsToTrue()
    {
        var manager = NewManager();
        Assert.IsTrue(manager.ListSkills().All(s => s.IsEnabled));
        Assert.AreEqual(2, manager.ListEnabledSkills().Count);
    }

    [TestMethod]
    public void SetEnabled_False_PersistsAcrossReload()
    {
        var manager = NewManager();
        manager.SetEnabled("alpha", false);

        Assert.IsFalse(manager.GetSkill("alpha")!.IsEnabled);
        Assert.AreEqual(1, manager.ListEnabledSkills().Count);

        var reloaded = NewManager();
        Assert.IsFalse(reloaded.GetSkill("alpha")!.IsEnabled);
        Assert.IsTrue(reloaded.GetSkill("beta")!.IsEnabled);
        Assert.AreEqual(1, reloaded.ListEnabledSkills().Count);
    }

    [TestMethod]
    public void SetEnabled_TrueAfterFalse_RestoresVisibility()
    {
        var manager = NewManager();
        manager.SetEnabled("alpha", false);
        manager.SetEnabled("alpha", true);

        var reloaded = NewManager();
        Assert.IsTrue(reloaded.GetSkill("alpha")!.IsEnabled);
        Assert.AreEqual(2, reloaded.ListEnabledSkills().Count);
    }

    [TestMethod]
    [ExpectedException(typeof(SkillNotFoundException))]
    public void SetEnabled_UnknownSkill_ThrowsSkillNotFound()
    {
        var manager = NewManager();
        manager.SetEnabled("ghost", false);
    }

    [TestMethod]
    public void SetEnabled_AfterToggle_WritesJsonFileNextToSkills()
    {
        var manager = NewManager();
        manager.SetEnabled("beta", false);

        var togglesPath = Path.Combine(_tempDir, ".skill-toggles.json");
        Assert.IsTrue(File.Exists(togglesPath));
        var raw = File.ReadAllText(togglesPath);
        Assert.IsTrue(raw.Contains("\"beta\""));
        Assert.IsTrue(raw.Contains("false"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Regression: Cursor Bugbot / Codex P1 — slash-command fallback bypassed
    // the user-facing toggle because InvokeSkillAsync didn't check IsEnabled.
    // ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task InvokeSkillAsync_EnabledSkill_ReturnsContext()
    {
        var manager = NewManager();
        var context = await manager.InvokeSkillAsync("alpha", "hello", CancellationToken.None);

        StringAssert.Contains(context, "Active Skill: alpha");
        StringAssert.Contains(context, "hello");
    }

    [TestMethod]
    [ExpectedException(typeof(SkillDisabledException))]
    public async Task InvokeSkillAsync_DisabledSkill_ThrowsSkillDisabled()
    {
        var manager = NewManager();
        manager.SetEnabled("alpha", false);

        await manager.InvokeSkillAsync("alpha", "hello", CancellationToken.None);
    }

    [TestMethod]
    public async Task InvokeSkillAsync_ReenabledSkill_WorksAgain()
    {
        var manager = NewManager();
        manager.SetEnabled("alpha", false);
        manager.SetEnabled("alpha", true);

        var context = await manager.InvokeSkillAsync("alpha", "ping", CancellationToken.None);

        StringAssert.Contains(context, "Active Skill: alpha");
    }

    [TestMethod]
    public void SkillDisabledException_OnConstruction_ExposesSkillName()
    {
        var ex = new SkillDisabledException("alpha");

        Assert.AreEqual("alpha", ex.SkillName);
        StringAssert.Contains(ex.Message, "alpha");
    }
}
