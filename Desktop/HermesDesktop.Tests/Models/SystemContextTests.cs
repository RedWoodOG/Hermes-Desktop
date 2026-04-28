using Hermes.Agent.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Models;

/// <summary>
/// Tests for SystemContext — the structured replacement for emitting multiple
/// role:"system" entries into the conversation list. Guards the contract that
/// providers consume: a single coalesced system block, layers in canonical
/// order, no mid-conversation system content.
/// </summary>
[TestClass]
public class SystemContextTests
{
    // ── Empty ──

    [TestMethod]
    public void Empty_HasNoLayers()
    {
        Assert.IsTrue(SystemContext.Empty.IsEmpty);
        Assert.AreEqual(0, SystemContext.Empty.NonEmptyLayers().Count());
        Assert.AreEqual(string.Empty, SystemContext.Empty.Render());
    }

    [TestMethod]
    public void IsEmpty_AllNullLayers_ReturnsTrue()
    {
        var ctx = new SystemContext();
        Assert.IsTrue(ctx.IsEmpty);
    }

    [TestMethod]
    public void IsEmpty_OnlyWhitespaceLayers_ReturnsTrue()
    {
        var ctx = new SystemContext { Soul = "  ", Wiki = "\t\n" };
        Assert.IsTrue(ctx.IsEmpty);
    }

    [TestMethod]
    public void IsEmpty_AnyNonEmptyLayer_ReturnsFalse()
    {
        var ctx = new SystemContext { Memory = "x" };
        Assert.IsFalse(ctx.IsEmpty);
    }

    // ── NonEmptyLayers ──

    [TestMethod]
    public void NonEmptyLayers_PreservesCanonicalOrder()
    {
        var ctx = new SystemContext
        {
            Memory = "memory",
            Soul = "soul",
            Persona = "persona",
            SessionState = "session",
            Wiki = "wiki",
            Plugins = "plugins"
        };

        var ordered = ctx.NonEmptyLayers().Select(l => l.Name).ToArray();

        CollectionAssert.AreEqual(
            new[] { "soul", "persona", "sessionState", "wiki", "plugins", "memory" },
            ordered);
    }

    [TestMethod]
    public void NonEmptyLayers_SkipsWhitespaceLayers()
    {
        var ctx = new SystemContext { Soul = "soul", Persona = "   ", Wiki = null };

        var names = ctx.NonEmptyLayers().Select(l => l.Name).ToArray();

        CollectionAssert.AreEqual(new[] { "soul" }, names);
    }

    [TestMethod]
    public void NonEmptyLayers_TrimsContent()
    {
        var ctx = new SystemContext { Soul = "  soul body  \n" };

        var (_, content) = ctx.NonEmptyLayers().Single();

        Assert.AreEqual("soul body", content);
    }

    [TestMethod]
    public void NonEmptyLayers_TransientFollowsNamedLayers()
    {
        var ctx = new SystemContext
        {
            Soul = "soul",
            Transient = new[] { "t1", "t2" }
        };

        var ordered = ctx.NonEmptyLayers().Select(l => l.Name).ToArray();

        CollectionAssert.AreEqual(new[] { "soul", "transient[0]", "transient[1]" }, ordered);
    }

    [TestMethod]
    public void NonEmptyLayers_TransientWithWhitespace_IsSkipped()
    {
        var ctx = new SystemContext { Transient = new[] { "real", "  ", "also-real" } };

        var contents = ctx.NonEmptyLayers().Select(l => l.Content).ToArray();

        CollectionAssert.AreEqual(new[] { "real", "also-real" }, contents);
    }

    // ── Render ──

    [TestMethod]
    public void Render_DefaultSeparator_IsTwoNewlines()
    {
        var ctx = new SystemContext { Soul = "A", Persona = "B" };
        Assert.AreEqual("A\n\nB", ctx.Render());
    }

    [TestMethod]
    public void Render_CustomSeparator_IsHonored()
    {
        var ctx = new SystemContext { Soul = "A", Persona = "B" };
        Assert.AreEqual("A | B", ctx.Render(" | "));
    }

    [TestMethod]
    public void Render_EmptyContext_ReturnsEmptyString()
    {
        Assert.AreEqual(string.Empty, SystemContext.Empty.Render());
    }

    [TestMethod]
    public void Render_AllSixLayersPlusTransient_AllJoined()
    {
        var ctx = new SystemContext
        {
            Soul = "soul",
            Persona = "persona",
            SessionState = "session",
            Wiki = "wiki",
            Plugins = "plugins",
            Memory = "memory",
            Transient = new[] { "t1", "t2" }
        };

        var rendered = ctx.Render(" | ");

        Assert.AreEqual("soul | persona | session | wiki | plugins | memory | t1 | t2", rendered);
    }

    // ── FromLegacyMessages ──

    [TestMethod]
    public void FromLegacyMessages_NoSystem_TransientIsEmpty()
    {
        var msgs = new[]
        {
            new Message { Role = "user", Content = "hi" },
            new Message { Role = "assistant", Content = "ok" }
        };

        var (sys, conv) = SystemContext.FromLegacyMessages(msgs);

        Assert.AreEqual(0, sys.Transient.Count);
        Assert.IsTrue(sys.IsEmpty);
        Assert.AreEqual(2, conv.Count);
    }

    [TestMethod]
    public void FromLegacyMessages_MidArraySystem_LiftedIntoTransient()
    {
        var msgs = new[]
        {
            new Message { Role = "system", Content = "soul" },
            new Message { Role = "user", Content = "first" },
            new Message { Role = "assistant", Content = "ok" },
            new Message { Role = "system", Content = "late-injection" },
            new Message { Role = "user", Content = "second" }
        };

        var (sys, conv) = SystemContext.FromLegacyMessages(msgs);

        CollectionAssert.AreEqual(new[] { "soul", "late-injection" }, sys.Transient.ToArray());
        Assert.AreEqual(3, conv.Count);
        foreach (var m in conv)
            Assert.AreNotEqual("system", m.Role);
    }

    [TestMethod]
    public void FromLegacyMessages_DropsEmptySystemContent()
    {
        var msgs = new[]
        {
            new Message { Role = "system", Content = "real" },
            new Message { Role = "system", Content = "   " },
            new Message { Role = "user", Content = "hi" }
        };

        var (sys, _) = SystemContext.FromLegacyMessages(msgs);

        CollectionAssert.AreEqual(new[] { "real" }, sys.Transient.ToArray());
    }

    [TestMethod]
    public void FromLegacyMessages_PreservesNonSystemOrder()
    {
        var msgs = new[]
        {
            new Message { Role = "user", Content = "a" },
            new Message { Role = "system", Content = "sys" },
            new Message { Role = "assistant", Content = "b" },
            new Message { Role = "tool", Content = "c", ToolCallId = "t1" },
            new Message { Role = "user", Content = "d" }
        };

        var (_, conv) = SystemContext.FromLegacyMessages(msgs);

        CollectionAssert.AreEqual(
            new[] { "a", "b", "c", "d" },
            conv.Select(m => m.Content).ToArray());
    }
}
