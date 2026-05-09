using Hermes.Agent.Core;
using Hermes.Agent.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Tools;

[TestClass]
public sealed class LargeToolOutputRouterTests
{
    [TestMethod]
    public void Route_PassesSmallSuccessfulOutputThrough()
    {
        using var temp = new TempDir();
        var router = new LargeToolOutputRouter(temp.Path, thresholdTokens: 100);
        var result = ToolResult.Ok("small output");

        var routed = router.Route("read_file", result);

        Assert.AreSame(result, routed);
    }

    [TestMethod]
    public void Route_DoesNotRouteFailures()
    {
        using var temp = new TempDir();
        var router = new LargeToolOutputRouter(temp.Path, thresholdTokens: 1);
        var result = ToolResult.Fail(new string('x', 100));

        var routed = router.Route("bash", result);

        Assert.AreSame(result, routed);
        Assert.AreEqual(0, Directory.GetFiles(temp.Path).Length);
    }

    [TestMethod]
    public void Route_StoresLargeOutputAndReturnsHeadTailSummary()
    {
        using var temp = new TempDir();
        var router = new LargeToolOutputRouter(temp.Path, thresholdTokens: 10);
        var content = "HEAD-" + new string('m', 6000) + "-TAIL";

        var routed = router.Route("grep", ToolResult.Ok(content));

        Assert.IsTrue(routed.Success);
        StringAssert.Contains(routed.Content, "[large-tool-output: tool=grep");
        StringAssert.Contains(routed.Content, "Raw output saved to:");
        StringAssert.Contains(routed.Content, "HEAD-");
        StringAssert.Contains(routed.Content, "-TAIL");

        var files = Directory.GetFiles(temp.Path, "*.txt");
        Assert.AreEqual(1, files.Length);
        Assert.AreEqual(content, File.ReadAllText(files[0]));
    }

    [TestMethod]
    public void Route_RedactsSecretsBeforeWritingArtifact()
    {
        using var temp = new TempDir();
        var router = new LargeToolOutputRouter(temp.Path, thresholdTokens: 10);
        var secret = "sk-" + new string('a', 48);
        var content = "HEAD " + secret + " " + new string('m', 6000) + " TAIL";

        var routed = router.Route("bash", ToolResult.Ok(content));

        Assert.IsTrue(routed.Success);
        Assert.IsFalse(routed.Content.Contains(secret, StringComparison.Ordinal));

        var files = Directory.GetFiles(temp.Path, "*.txt");
        Assert.AreEqual(1, files.Length);
        var artifact = File.ReadAllText(files[0]);
        Assert.IsFalse(artifact.Contains(secret, StringComparison.Ordinal));
        StringAssert.Contains(artifact, "[REDACTED");
    }

    [TestMethod]
    public void EstimateTokens_RoundsUp()
    {
        Assert.AreEqual(0, LargeToolOutputRouter.EstimateTokens(""));
        Assert.AreEqual(1, LargeToolOutputRouter.EstimateTokens("abc"));
        Assert.AreEqual(2, LargeToolOutputRouter.EstimateTokens("abcd"));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hermes-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
