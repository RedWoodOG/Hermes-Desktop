using System.Linq;
using Hermes.Agent.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Shell;

/// <summary>
/// Bundle E.2 tests for <see cref="SlashCommandRegistry"/>.
/// </summary>
[TestClass]
public class SlashCommandRegistryTests
{
    [TestMethod]
    public void All_AfterStaticInit_ContainsExpectedCommands()
    {
        var names = SlashCommandRegistry.All.Select(c => c.Name).ToHashSet();

        CollectionAssert.IsSubsetOf(
            new[] { "/new", "/clear", "/help", "/tools", "/skills", "/model", "/usage", "/version", "/status", "/debug" }.ToArray(),
            names.ToArray(),
            "All 10 local commands must be registered.");

        CollectionAssert.IsSubsetOf(
            new[] { "/btw", "/approve", "/deny", "/reset" }.ToArray(),
            names.ToArray(),
            "Core agent-forwarded commands must be registered.");
    }

    [TestMethod]
    public void All_LocalCommands_AreFlaggedLocal()
    {
        foreach (var name in new[] { "/new", "/clear", "/retry", "/help", "/tools", "/skills",
                                     "/model", "/usage", "/version", "/status", "/debug" })
        {
            var cmd = SlashCommandRegistry.Find(name);
            Assert.IsNotNull(cmd, $"{name} should be in the registry.");
            Assert.AreEqual(SlashCommandKind.Local, cmd!.Kind, $"{name} must be local.");
        }
    }

    [TestMethod]
    public void All_AgentCommands_AreFlaggedAgentForwarded()
    {
        foreach (var name in new[] { "/btw", "/approve", "/deny", "/reset", "/compact" })
        {
            var cmd = SlashCommandRegistry.Find(name);
            Assert.IsNotNull(cmd, $"{name} should be in the registry.");
            Assert.AreEqual(SlashCommandKind.AgentForwarded, cmd!.Kind, $"{name} must be agent-forwarded.");
        }
    }

    // ── FindByPrefix ──

    [TestMethod]
    public void FindByPrefix_EmptyString_ReturnsAll()
    {
        var results = SlashCommandRegistry.FindByPrefix(string.Empty);
        Assert.AreEqual(SlashCommandRegistry.All.Count, results.Count);
    }

    [TestMethod]
    public void FindByPrefix_SlashOnly_ReturnsAll()
    {
        var results = SlashCommandRegistry.FindByPrefix("/");
        Assert.AreEqual(SlashCommandRegistry.All.Count, results.Count);
    }

    [TestMethod]
    public void FindByPrefix_SlashNe_ReturnsNewOnly()
    {
        var results = SlashCommandRegistry.FindByPrefix("/ne");
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("/new", results[0].Name);
    }

    [TestMethod]
    public void FindByPrefix_WithoutLeadingSlash_StillWorks()
    {
        var results = SlashCommandRegistry.FindByPrefix("hel");
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("/help", results[0].Name);
    }

    [TestMethod]
    public void FindByPrefix_UpperCaseInput_MatchesCaseInsensitive()
    {
        var results = SlashCommandRegistry.FindByPrefix("/NEW");
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("/new", results[0].Name);
    }

    [TestMethod]
    public void FindByPrefix_NoMatch_ReturnsEmpty()
    {
        var results = SlashCommandRegistry.FindByPrefix("/zzz");
        Assert.AreEqual(0, results.Count);
    }

    // ── TryParse ──

    [TestMethod]
    public void TryParse_NoSlash_ReturnsNull()
    {
        Assert.IsNull(SlashCommandRegistry.TryParse("hello"));
        Assert.IsNull(SlashCommandRegistry.TryParse(""));
        Assert.IsNull(SlashCommandRegistry.TryParse("   "));
    }

    [TestMethod]
    public void TryParse_SlashOnly_ReturnsNull()
    {
        Assert.IsNull(SlashCommandRegistry.TryParse("/"));
    }

    [TestMethod]
    public void TryParse_KnownCommand_NoArgs()
    {
        var result = SlashCommandRegistry.TryParse("/new");

        Assert.IsNotNull(result);
        Assert.IsNotNull(result!.Command);
        Assert.AreEqual("/new", result.Command!.Name);
        Assert.AreEqual("new", result.TypedName);
        Assert.AreEqual(string.Empty, result.Arguments);
    }

    [TestMethod]
    public void TryParse_KnownCommand_WithArgs()
    {
        var result = SlashCommandRegistry.TryParse("/btw what time is it");

        Assert.IsNotNull(result);
        Assert.IsNotNull(result!.Command);
        Assert.AreEqual("/btw", result.Command!.Name);
        Assert.AreEqual("what time is it", result.Arguments);
    }

    [TestMethod]
    public void TryParse_UnknownCommand_ReturnsInvocationWithNullCommand()
    {
        var result = SlashCommandRegistry.TryParse("/foobar baz");

        Assert.IsNotNull(result);
        Assert.IsNull(result!.Command);
        Assert.AreEqual("foobar", result.TypedName);
        Assert.AreEqual("baz", result.Arguments);
    }

    [TestMethod]
    public void TryParse_LeadingWhitespace_StillMatchesCommand()
    {
        var result = SlashCommandRegistry.TryParse("   /help");

        Assert.IsNotNull(result);
        Assert.AreEqual("/help", result!.Command?.Name);
    }

    // ── DescriptionResourceKey ──

    [TestMethod]
    public void DescriptionResourceKey_ForKnownCommand_FollowsExpectedFormat()
    {
        var cmd = SlashCommandRegistry.Find("/help")!;
        Assert.AreEqual("SlashCommand.help.Description", cmd.DescriptionResourceKey);
    }

    [TestMethod]
    public void All_RegisteredCategories_ContainChatAgentAndInfo()
    {
        var categories = SlashCommandRegistry.All
            .Select(c => c.Category)
            .Distinct()
            .ToHashSet();

        Assert.IsTrue(categories.Contains(SlashCommandCategory.Chat),
            "Chat-category commands (/new, /clear, /retry) must be registered.");
        Assert.IsTrue(categories.Contains(SlashCommandCategory.Agent),
            "Agent-forwarded commands (/btw, /approve, ...) must be registered.");
        Assert.IsTrue(categories.Contains(SlashCommandCategory.Info),
            "Info-category commands (/help, /status, ...) must be registered.");
    }

    [TestMethod]
    public void All_RegisteredCategories_ToolsCategoryReservedButUnused()
    {
        var categories = SlashCommandRegistry.All
            .Select(c => c.Category)
            .Distinct()
            .ToHashSet();

        // The Tools enum value is reserved for future tool-invocation shortcuts (/web, /shell, …)
        // but is intentionally not used by any built-in command today. If a Tools-category
        // command is added, update both this test and All_RegisteredCategories_ContainChatAgentAndInfo.
        Assert.IsFalse(categories.Contains(SlashCommandCategory.Tools),
            "No built-in command currently uses the Tools category; remove this guard when one is added.");
    }
}
