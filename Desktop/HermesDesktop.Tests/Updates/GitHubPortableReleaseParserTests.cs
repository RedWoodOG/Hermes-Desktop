namespace HermesDesktop.Tests.Updates;

using System.Reflection;
using System.Text;
using Hermes.Agent.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class GitHubPortableReleaseParserTests
{
    private const string SampleReleaseJson = """
{
  "tag_name": "v2.6.0",
  "html_url": "https://github.com/RedWoodOG/Hermes-Desktop/releases/tag/v2.6.0",
  "assets": [
    {
      "name": "HermesDesktop-portable-x64.zip",
      "browser_download_url": "https://github.com/RedWoodOG/Hermes-Desktop/releases/download/v2.6.0/HermesDesktop-portable-x64.zip"
    },
    {
      "name": "HermesDesktop-portable-x64.zip.sha256",
      "browser_download_url": "https://github.com/RedWoodOG/Hermes-Desktop/releases/download/v2.6.0/HermesDesktop-portable-x64.zip.sha256"
    }
  ]
}
""";

    [TestMethod]
    public void TryParseLatestReleaseJson_ZipPlusSha256Asset_FindsBothAssets()
    {
        var offer = GitHubPortableReleaseParser.TryParseLatestReleaseJson(SampleReleaseJson);
        Assert.IsNotNull(offer);
        Assert.AreEqual("v2.6.0", offer.TagName);
        Assert.AreEqual(new Version(2, 6, 0), offer.Version);
        Assert.IsNotNull(offer.Sha256BrowserDownloadUri);
        Assert.IsTrue(offer.ZipBrowserDownloadUri.IsAbsoluteUri);
    }

    [TestMethod]
    public void TryParseLatestReleaseJson_WithoutSha256_SucceedsWithNullSha()
    {
        const string json = """
{"tag_name":"v2.6.1","html_url":"https://example.invalid/r","assets":[
{"name":"HermesDesktop-portable-x64.zip","browser_download_url":"https://example.invalid/z.zip"}]}
""";
        var offer = GitHubPortableReleaseParser.TryParseLatestReleaseJson(json);
        Assert.IsNotNull(offer);
        Assert.IsNull(offer.Sha256BrowserDownloadUri);
    }

    [TestMethod]
    public void IsNewerThan_WhenLatestGreater_ReturnsTrue()
    {
        Assert.IsTrue(GitHubPortableReleaseParser.IsNewerThan(new Version(2, 6, 0), new Version(2, 5, 4)));
        Assert.IsFalse(GitHubPortableReleaseParser.IsNewerThan(new Version(2, 5, 4), new Version(2, 5, 4)));
        Assert.IsFalse(GitHubPortableReleaseParser.IsNewerThan(new Version(2, 4, 0), new Version(2, 5, 4)));
    }

    [TestMethod]
    public void TryParseVersionFromTag_LeadingVAndPrerelease_StripsBoth()
    {
        Assert.IsTrue(GitHubPortableReleaseParser.TryParseVersionFromTag("v2.5.4-beta.1", out var v));
        Assert.AreEqual(new Version(2, 5, 4), v);
    }

    [TestMethod]
    public void TryParseSha256SumFile_ShasumLine_AcceptsAndReturnsBytes()
    {
        const string hex = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        Assert.IsTrue(GitHubPortableReleaseParser.TryParseSha256SumFile($"{hex}  HermesDesktop-portable-x64.zip", out var mem));
        Assert.AreEqual(32, mem.Length);
    }

    [TestMethod]
    public void TryParseSha256SumFile_InvalidHex_Rejects()
    {
        const string bad = "gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg";
        Assert.IsFalse(GitHubPortableReleaseParser.TryParseSha256SumFile(bad, out _));
    }

    [TestMethod]
    public void FixedTimeEquals_DifferentDigests_ReturnsFalse()
    {
        var a = new byte[32];
        var b = new byte[32];
        a[0] = 1;
        Assert.IsFalse(GitHubPortableReleaseParser.FixedTimeEquals(a, b));
    }

    [TestMethod]
    public void LatestReleaseApiUri_OwnerRepoWithSpaces_EscapesOwnerAndRepoOnly()
    {
        var u = GitHubPortableReleaseParser.LatestReleaseApiUri("O wner", "Re po");
        Assert.IsTrue(u.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(u.AbsolutePath.Contains("O%20wner", StringComparison.Ordinal));
        Assert.IsTrue(u.AbsolutePath.Contains("Re%20po", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TryParseDesktopAssemblyVersion_ExecutingAssembly_ReadsAssemblyVersion()
    {
        Assert.IsTrue(
            GitHubPortableReleaseParser.TryParseDesktopAssemblyVersion(Assembly.GetExecutingAssembly(), out var v));
        Assert.IsNotNull(v);
    }
}
