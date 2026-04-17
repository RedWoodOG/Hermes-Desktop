using Hermes.Agent.Permissions;
using Hermes.Agent.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

[TestClass]
public class PermissionManagerTests
{
    private static PermissionManager CreateManager(
        PermissionMode mode,
        Action<PermissionContext>? configure = null)
    {
        var context = new PermissionContext { Mode = mode };
        configure?.Invoke(context);
        return new PermissionManager(context, NullLogger<PermissionManager>.Instance);
    }

    [TestMethod]
    public void Mode_ReflectsPermissionContextDefault()
    {
        var context = new PermissionContext { Mode = PermissionMode.Auto };
        var manager = new PermissionManager(context, NullLogger<PermissionManager>.Instance);

        Assert.AreEqual(PermissionMode.Auto, manager.Mode);
    }

    [TestMethod]
    public void Mode_SetterUpdatesUnderlyingContext()
    {
        var context = new PermissionContext { Mode = PermissionMode.Default };
        var manager = new PermissionManager(context, NullLogger<PermissionManager>.Instance);

        manager.Mode = PermissionMode.AcceptEdits;

        Assert.AreEqual(PermissionMode.AcceptEdits, context.Mode);
        Assert.AreEqual(PermissionMode.AcceptEdits, manager.Mode);
    }

    [TestMethod]
    public async Task CheckPermissionsAsync_BypassPermissions_AllowsAnyTool()
    {
        var manager = CreateManager(PermissionMode.BypassPermissions);
        const string input = "{\"path\":\"/tmp/file.txt\"}";

        var decision = await manager.CheckPermissionsAsync("write_file", input, CancellationToken.None);

        Assert.AreEqual(PermissionBehavior.Allow, decision.Behavior);
        Assert.AreEqual(input, decision.UpdatedInput);
        Assert.IsTrue(decision.IsAllowed);
        Assert.IsFalse(decision.IsDenied);
    }

    [TestMethod]
    public async Task CheckPermissionsAsync_PlanMode_AllowsReadOnlyTools()
    {
        var manager = CreateManager(PermissionMode.Plan);

        var decision = await manager.CheckPermissionsAsync("read_file", "/workspace/readme.md", CancellationToken.None);

        Assert.AreEqual(PermissionBehavior.Allow, decision.Behavior);
        Assert.IsTrue(decision.IsAllowed);
    }

    [TestMethod]
    public async Task CheckPermissionsAsync_PlanMode_DeniesMutatingTools()
    {
        var manager = CreateManager(PermissionMode.Plan);

        var decision = await manager.CheckPermissionsAsync("edit_file", "{\"path\":\"file.cs\"}", CancellationToken.None);

        Assert.AreEqual(PermissionBehavior.Deny, decision.Behavior);
        Assert.AreEqual("Cannot modify files in plan mode", decision.Message);
        Assert.IsTrue(decision.IsDenied);
    }

    [TestMethod]
    public async Task CheckPermissionsAsync_AlwaysAllowRule_OverridesDefaultAskBehavior()
    {
        var manager = CreateManager(PermissionMode.Default, context =>
            context.AlwaysAllow.Add(PermissionRule.AllowAll("bash")));

        var decision = await manager.CheckPermissionsAsync("bash", "rm -rf /tmp/sandbox", CancellationToken.None);

        Assert.AreEqual(PermissionBehavior.Allow, decision.Behavior);
    }

    [TestMethod]
    public async Task CheckPermissionsAsync_AlwaysDenyRule_BlocksMatchingTool()
    {
        var manager = CreateManager(PermissionMode.Default, context =>
            context.AlwaysDeny.Add(PermissionRule.DenyAll("bash")));

        var decision = await manager.CheckPermissionsAsync("bash", "git status", CancellationToken.None);

        Assert.AreEqual(PermissionBehavior.Deny, decision.Behavior);
        Assert.AreEqual("Blocked by permission rule", decision.Message);
    }

    [TestMethod]
    public async Task CheckPermissionsAsync_AlwaysAllowRule_TakesPrecedenceOverAlwaysDeny()
    {
        var manager = CreateManager(PermissionMode.Default, context =>
        {
            context.AlwaysAllow.Add(PermissionRule.AllowAll("bash"));
            context.AlwaysDeny.Add(PermissionRule.DenyAll("bash"));
        });

        var decision = await manager.CheckPermissionsAsync("bash", "touch /tmp/allowed", CancellationToken.None);

        Assert.AreEqual(PermissionBehavior.Allow, decision.Behavior);
    }

    [TestMethod]
    public async Task CheckPermissionsAsync_AlwaysAskRule_ForcesPrompt()
    {
        var manager = CreateManager(PermissionMode.Auto, context =>
            context.AlwaysAsk.Add(PermissionRule.AllowAll("write_file")));

        var decision = await manager.CheckPermissionsAsync("write_file", "{\"path\":\"x.txt\"}", CancellationToken.None);

        Assert.AreEqual(PermissionBehavior.Ask, decision.Behavior);
        Assert.AreEqual("Requires permission: write_file", decision.Message);
        Assert.IsTrue(decision.NeedsUserInput);
    }

    [TestMethod]
    public async Task CheckPermissionsAsync_AutoMode_AllowsReadOnlyToolWithReason()
    {
        var manager = CreateManager(PermissionMode.Auto);

        var decision = await manager.CheckPermissionsAsync("glob", "**/*.cs", CancellationToken.None);

        Assert.AreEqual(PermissionBehavior.Allow, decision.Behavior);
        Assert.AreEqual("Auto-approved read-only operation", decision.DecisionReason);
    }

    [TestMethod]
    public async Task CheckPermissionsAsync_AutoMode_AsksForMutatingTool()
    {
        var manager = CreateManager(PermissionMode.Auto);

        var decision = await manager.CheckPermissionsAsync("write_file", "{\"path\":\"x.txt\"}", CancellationToken.None);

        Assert.AreEqual(PermissionBehavior.Ask, decision.Behavior);
        Assert.AreEqual("Modify operation requires permission", decision.Message);
    }

    [TestMethod]
    public async Task CheckPermissionsAsync_AutoMode_BashReadOnlyCommandIsAllowed()
    {
        var manager = CreateManager(PermissionMode.Auto);
        var input = new BashParameters { Command = "git status" };

        var decision = await manager.CheckPermissionsAsync("bash", input, CancellationToken.None);

        Assert.AreEqual(PermissionBehavior.Allow, decision.Behavior);
        Assert.AreEqual("Auto-approved read-only operation", decision.DecisionReason);
    }

    [TestMethod]
    public async Task CheckPermissionsAsync_AutoMode_BashMutatingCommandRequiresPrompt()
    {
        var manager = CreateManager(PermissionMode.Auto);
        var input = new BashParameters { Command = "rm -rf /tmp/unsafe" };

        var decision = await manager.CheckPermissionsAsync("bash", input, CancellationToken.None);

        Assert.AreEqual(PermissionBehavior.Ask, decision.Behavior);
        Assert.AreEqual("Modify operation requires permission", decision.Message);
    }

    [TestMethod]
    public async Task CheckPermissionsAsync_AcceptEditsMode_AllowsInWorkspaceOperations()
    {
        var manager = CreateManager(PermissionMode.AcceptEdits);

        var decision = await manager.CheckPermissionsAsync("edit_file", "{\"path\":\"src/file.cs\"}", CancellationToken.None);

        Assert.AreEqual(PermissionBehavior.Allow, decision.Behavior);
        Assert.AreEqual("Auto-approved: within workspace", decision.DecisionReason);
    }

    [TestMethod]
    public async Task CheckPermissionsAsync_PatternRule_MatchesGlobPattern()
    {
        var manager = CreateManager(PermissionMode.Default, context =>
            context.AlwaysDeny.Add(PermissionRule.AllowPattern("bash", "git *")));

        var blocked = await manager.CheckPermissionsAsync("bash", "git status", CancellationToken.None);
        var notBlocked = await manager.CheckPermissionsAsync("bash", "npm test", CancellationToken.None);

        Assert.AreEqual(PermissionBehavior.Deny, blocked.Behavior);
        Assert.AreEqual(PermissionBehavior.Ask, notBlocked.Behavior);
    }

    [TestMethod]
    public async Task CheckPermissionsAsync_WildcardToolRule_MatchesAnyTool()
    {
        var manager = CreateManager(PermissionMode.Default, context =>
            context.AlwaysDeny.Add(PermissionRule.AllowPattern("*", "*.secrets*")));

        var decision = await manager.CheckPermissionsAsync("read_file", "/workspace/.secrets/config.yaml", CancellationToken.None);

        Assert.AreEqual(PermissionBehavior.Deny, decision.Behavior);
    }

    [TestMethod]
    public async Task CheckPermissionsAsync_UnknownMode_FallsBackToAsk()
    {
        var manager = CreateManager((PermissionMode)999);

        var decision = await manager.CheckPermissionsAsync("read_file", "/workspace/readme.md", CancellationToken.None);

        Assert.AreEqual(PermissionBehavior.Ask, decision.Behavior);
        StringAssert.Contains(decision.Message ?? string.Empty, "Unknown mode");
    }

    [TestMethod]
    public async Task AddAlwaysAllowRule_DefaultMode_PreventsFuturePermissionPrompts()
    {
        var manager = CreateManager(PermissionMode.Default);
        var added = manager.AddAlwaysAllowRule("write_file");

        var decision = await manager.CheckPermissionsAsync("write_file", "{\"path\":\"x.txt\"}", CancellationToken.None);

        Assert.IsTrue(added);
        Assert.AreEqual(PermissionBehavior.Allow, decision.Behavior);
    }

    [TestMethod]
    public void AddAlwaysAllowRule_DuplicateRule_ReturnsFalse()
    {
        var manager = CreateManager(PermissionMode.Default);

        var first = manager.AddAlwaysAllowRule("bash");
        var second = manager.AddAlwaysAllowRule("bash");

        Assert.IsTrue(first);
        Assert.IsFalse(second);
    }

    [TestMethod]
    public void AddAlwaysAllowRule_InvalidToolName_ThrowsArgumentException()
    {
        var manager = CreateManager(PermissionMode.Default);

        Assert.ThrowsException<ArgumentException>(() => manager.AddAlwaysAllowRule("   "));
    }

    [TestMethod]
    public void HasAlwaysAllowRule_RespectsCaseInsensitiveRuleMatching()
    {
        var manager = CreateManager(PermissionMode.Default);
        manager.AddAlwaysAllowRule("write_file");

        Assert.IsTrue(manager.HasAlwaysAllowRule("WRITE_FILE"));
    }

    [TestMethod]
    public void ClearAlwaysAllowRules_RemovesAllRememberedRules()
    {
        var manager = CreateManager(PermissionMode.Default);
        manager.AddAlwaysAllowRule("write_file");
        manager.AddAlwaysAllowRule("bash");

        manager.ClearAlwaysAllowRules();

        Assert.IsFalse(manager.HasAlwaysAllowRule("write_file"));
        Assert.IsFalse(manager.HasAlwaysAllowRule("bash"));
        Assert.AreEqual(0, manager.GetAlwaysAllowRulesSnapshot().Count);
    }

    // ── Additional tests for new AddAlwaysAllowRule / HasAlwaysAllowRule /
    //    GetAlwaysAllowRulesSnapshot / ClearAlwaysAllowRules surface ──

    [TestMethod]
    public void AddAlwaysAllowRule_WithPattern_AddsDistinctRuleFromNoPattern()
    {
        var manager = CreateManager(PermissionMode.Default);

        var withoutPattern = manager.AddAlwaysAllowRule("write_file");
        // Same tool, different pattern → should be a separate, addable rule.
        var withPattern = manager.AddAlwaysAllowRule("write_file", "**/*.md");

        Assert.IsTrue(withoutPattern);
        Assert.IsTrue(withPattern);
        Assert.AreEqual(2, manager.GetAlwaysAllowRulesSnapshot().Count);
    }

    [TestMethod]
    public void AddAlwaysAllowRule_WithSameToolAndPattern_ReturnsFalse()
    {
        var manager = CreateManager(PermissionMode.Default);

        var first = manager.AddAlwaysAllowRule("write_file", "**/*.md");
        var second = manager.AddAlwaysAllowRule("write_file", "**/*.md");

        Assert.IsTrue(first);
        Assert.IsFalse(second);
    }

    [TestMethod]
    public void AddAlwaysAllowRule_TrimsWhitespaceFromToolName()
    {
        var manager = CreateManager(PermissionMode.Default);

        manager.AddAlwaysAllowRule("  bash  ");

        // Lookup without extra spaces should find the trimmed rule.
        Assert.IsTrue(manager.HasAlwaysAllowRule("bash"));
    }

    [TestMethod]
    public void AddAlwaysAllowRule_EmptyString_ThrowsArgumentException()
    {
        var manager = CreateManager(PermissionMode.Default);

        Assert.ThrowsException<ArgumentException>(() => manager.AddAlwaysAllowRule(""));
    }

    [TestMethod]
    public void HasAlwaysAllowRule_WhenNoRulesExist_ReturnsFalse()
    {
        var manager = CreateManager(PermissionMode.Default);

        Assert.IsFalse(manager.HasAlwaysAllowRule("bash"));
    }

    [TestMethod]
    public void HasAlwaysAllowRule_EmptyString_ReturnsFalseWithoutThrowing()
    {
        var manager = CreateManager(PermissionMode.Default);
        manager.AddAlwaysAllowRule("bash");

        // Empty/whitespace tool name should return false, not throw.
        Assert.IsFalse(manager.HasAlwaysAllowRule(""));
        Assert.IsFalse(manager.HasAlwaysAllowRule("   "));
    }

    [TestMethod]
    public void HasAlwaysAllowRule_WithMatchingPattern_ReturnsTrue()
    {
        var manager = CreateManager(PermissionMode.Default);
        manager.AddAlwaysAllowRule("write_file", "**/*.md");

        Assert.IsTrue(manager.HasAlwaysAllowRule("write_file", "**/*.md"));
    }

    [TestMethod]
    public void HasAlwaysAllowRule_WithDifferentPattern_ReturnsFalse()
    {
        var manager = CreateManager(PermissionMode.Default);
        manager.AddAlwaysAllowRule("write_file", "**/*.md");

        // Rule exists only for *.md; *.cs should not match.
        Assert.IsFalse(manager.HasAlwaysAllowRule("write_file", "**/*.cs"));
    }

    [TestMethod]
    public void GetAlwaysAllowRulesSnapshot_IsDeepCopy_MutationDoesNotAffectManager()
    {
        var manager = CreateManager(PermissionMode.Default);
        manager.AddAlwaysAllowRule("bash");

        // Snapshot is an IReadOnlyList backed by an array — the reference itself
        // is detached from the live list; cast to array and check length after add.
        var snapshot = manager.GetAlwaysAllowRulesSnapshot();
        int countBeforeAdd = snapshot.Count;

        manager.AddAlwaysAllowRule("write_file");

        // Original snapshot must remain unchanged.
        Assert.AreEqual(countBeforeAdd, snapshot.Count);
        // Manager now has 2 rules.
        Assert.AreEqual(2, manager.GetAlwaysAllowRulesSnapshot().Count);
    }

    [TestMethod]
    public void GetAlwaysAllowRulesSnapshot_ContainsExpectedToolNamesAndPatterns()
    {
        var manager = CreateManager(PermissionMode.Default);
        manager.AddAlwaysAllowRule("bash");
        manager.AddAlwaysAllowRule("write_file", "**/*.cs");

        var snapshot = manager.GetAlwaysAllowRulesSnapshot();

        Assert.AreEqual(2, snapshot.Count);
        var bashRule = snapshot.Single(r => r.ToolName == "bash");
        var writeRule = snapshot.Single(r => r.ToolName == "write_file");
        Assert.IsNull(bashRule.Pattern);
        Assert.AreEqual("**/*.cs", writeRule.Pattern);
    }

    [TestMethod]
    public void ClearAlwaysAllowRules_WhenEmpty_ReturnsZero()
    {
        var manager = CreateManager(PermissionMode.Default);

        var removed = manager.ClearAlwaysAllowRules();

        Assert.AreEqual(0, removed);
    }

    [TestMethod]
    public void ClearAlwaysAllowRules_ReturnsCountOfRemovedRules()
    {
        var manager = CreateManager(PermissionMode.Default);
        manager.AddAlwaysAllowRule("bash");
        manager.AddAlwaysAllowRule("write_file");
        manager.AddAlwaysAllowRule("read_file");

        var removed = manager.ClearAlwaysAllowRules();

        Assert.AreEqual(3, removed);
    }

    [TestMethod]
    public async Task ClearAlwaysAllowRules_DoesNotAffectAlwaysDenyRules()
    {
        var manager = CreateManager(PermissionMode.Default, context =>
            context.AlwaysDeny.Add(PermissionRule.DenyAll("bash")));
        manager.AddAlwaysAllowRule("write_file");

        manager.ClearAlwaysAllowRules();

        // AlwaysDeny rule must still be in effect.
        var decision = await manager.CheckPermissionsAsync("bash", "git log", CancellationToken.None);
        Assert.AreEqual(PermissionBehavior.Deny, decision.Behavior);
    }

    [TestMethod]
    public void AddAlwaysAllowRule_AfterClear_CanBeAddedAgain()
    {
        var manager = CreateManager(PermissionMode.Default);
        manager.AddAlwaysAllowRule("bash");
        manager.ClearAlwaysAllowRules();

        // After clearing, the same rule should be addable again (returns true).
        var reAdded = manager.AddAlwaysAllowRule("bash");

        Assert.IsTrue(reAdded);
        Assert.IsTrue(manager.HasAlwaysAllowRule("bash"));
    }

    [TestMethod]
    public async Task AddAlwaysAllowRule_WithPattern_AllowsToolCallMatchingPattern()
    {
        var manager = CreateManager(PermissionMode.Default);
        manager.AddAlwaysAllowRule("bash", "git *");

        var allowed = await manager.CheckPermissionsAsync("bash", "git status", CancellationToken.None);
        var notAllowed = await manager.CheckPermissionsAsync("bash", "npm install", CancellationToken.None);

        Assert.AreEqual(PermissionBehavior.Allow, allowed.Behavior);
        // npm install doesn't match "git *" so the manager falls through to Default → Ask.
        Assert.AreEqual(PermissionBehavior.Ask, notAllowed.Behavior);
    }
}