using Hermes.Agent.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

/// <summary>
/// Regression tests for PR #44 — WebSearchConfig.NormalizeProvider.
/// Locks in the contract that a typo or manual edit in config.yaml can't
/// turn every web-search call into NotSupportedException (the Codex review
/// concern that prompted this helper).
/// </summary>
[TestClass]
public class WebSearchConfigTests
{
    [TestMethod]
    public void NormalizeProvider_Null_ReturnsDuckDuckGo()
    {
        Assert.AreEqual("duckduckgo", WebSearchConfig.NormalizeProvider(null));
    }

    [TestMethod]
    public void NormalizeProvider_Empty_ReturnsDuckDuckGo()
    {
        Assert.AreEqual("duckduckgo", WebSearchConfig.NormalizeProvider(""));
    }

    [TestMethod]
    public void NormalizeProvider_Whitespace_ReturnsDuckDuckGo()
    {
        Assert.AreEqual("duckduckgo", WebSearchConfig.NormalizeProvider("   "));
    }

    [TestMethod]
    public void NormalizeProvider_Unknown_ReturnsDuckDuckGo()
    {
        // A typo like "gogle" or "bingg" previously caused WebSearchTool to
        // throw NotSupportedException on every call.
        Assert.AreEqual("duckduckgo", WebSearchConfig.NormalizeProvider("gogle"));
        Assert.AreEqual("duckduckgo", WebSearchConfig.NormalizeProvider("bingg"));
        Assert.AreEqual("duckduckgo", WebSearchConfig.NormalizeProvider("yahoo"));
    }

    [TestMethod]
    public void NormalizeProvider_Google_Lowercased()
    {
        Assert.AreEqual("google", WebSearchConfig.NormalizeProvider("Google"));
        Assert.AreEqual("google", WebSearchConfig.NormalizeProvider("GOOGLE"));
        Assert.AreEqual("google", WebSearchConfig.NormalizeProvider("  google  "));
    }

    [TestMethod]
    public void NormalizeProvider_Bing_Lowercased()
    {
        Assert.AreEqual("bing", WebSearchConfig.NormalizeProvider("Bing"));
    }

    [TestMethod]
    public void NormalizeProvider_DuckDuckGo_Lowercased()
    {
        Assert.AreEqual("duckduckgo", WebSearchConfig.NormalizeProvider("DuckDuckGo"));
    }

    [TestMethod]
    public void NormalizeProvider_DdgAlias_MapsToDuckDuckGo()
    {
        // WebSearchTool.ExecuteAsync treats "ddg" as an alias for DuckDuckGo;
        // the normalizer preserves that mapping.
        Assert.AreEqual("duckduckgo", WebSearchConfig.NormalizeProvider("ddg"));
        Assert.AreEqual("duckduckgo", WebSearchConfig.NormalizeProvider("DDG"));
    }

    [TestMethod]
    public void SupportedProviders_ListedExactly()
    {
        CollectionAssert.AreEquivalent(
            new[] { "duckduckgo", "google", "bing" },
            WebSearchConfig.SupportedProviders.ToArray());
    }
}
