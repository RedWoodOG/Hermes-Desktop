using Hermes.Agent.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Mcp;

[TestClass]
public class McpRemoteEndpointValidatorTests
{
    [TestMethod]
    public void TryValidateRemoteUri_HttpsPublicHost_Allows()
    {
        bool ok = McpRemoteEndpointValidator.TryValidateRemoteUri(
            new Uri("https://api.example.com/mcp", UriKind.Absolute),
            out string? err);

        Assert.IsTrue(ok);
        Assert.IsNull(err);
    }

    [TestMethod]
    public void TryValidateRemoteUri_WssPublicHost_Allows()
    {
        bool ok = McpRemoteEndpointValidator.TryValidateRemoteUri(
            new Uri("wss://stream.example.com/sse", UriKind.Absolute),
            out string? err);

        Assert.IsTrue(ok);
        Assert.IsNull(err);
    }

    [TestMethod]
    public void TryValidateRemoteUri_HttpsMetadataIp_Blocks()
    {
        bool ok = McpRemoteEndpointValidator.TryValidateRemoteUri(
            new Uri("https://169.254.169.254/latest/meta-data/", UriKind.Absolute),
            out string? err);

        Assert.IsFalse(ok);
        Assert.IsFalse(string.IsNullOrEmpty(err));
    }

    [TestMethod]
    public void TryValidateRemoteUri_HttpsLinkLocalRange_Blocks()
    {
        bool ok = McpRemoteEndpointValidator.TryValidateRemoteUri(
            new Uri("https://169.254.10.20/mcp", UriKind.Absolute),
            out string? err);

        Assert.IsFalse(ok);
        Assert.IsFalse(string.IsNullOrEmpty(err));
    }

    [TestMethod]
    public void TryValidateRemoteUri_HttpNonLoopback_Blocks()
    {
        bool ok = McpRemoteEndpointValidator.TryValidateRemoteUri(
            new Uri("http://192.168.1.1/mcp", UriKind.Absolute),
            out string? err);

        Assert.IsFalse(ok);
        StringAssert.Contains(err, "loopback");
    }

    [TestMethod]
    public void TryValidateRemoteUri_HttpLocalhost_Allows()
    {
        bool ok = McpRemoteEndpointValidator.TryValidateRemoteUri(
            new Uri("http://localhost:8931/mcp", UriKind.Absolute),
            out string? err);

        Assert.IsTrue(ok);
        Assert.IsNull(err);
    }

    [TestMethod]
    public void TryValidateRemoteUri_Http127_Allows()
    {
        bool ok = McpRemoteEndpointValidator.TryValidateRemoteUri(
            new Uri("http://127.0.0.1:3000/", UriKind.Absolute),
            out string? err);

        Assert.IsTrue(ok);
        Assert.IsNull(err);
    }

    [TestMethod]
    public void TryValidateRemoteUri_Ws127_Allows()
    {
        bool ok = McpRemoteEndpointValidator.TryValidateRemoteUri(
            new Uri("ws://127.0.0.1:8080/ws", UriKind.Absolute),
            out string? err);

        Assert.IsTrue(ok);
        Assert.IsNull(err);
    }

    [TestMethod]
    public void TryValidateRemoteUri_WsPublicHost_Blocks()
    {
        bool ok = McpRemoteEndpointValidator.TryValidateRemoteUri(
            new Uri("ws://example.com/ws", UriKind.Absolute),
            out string? err);

        Assert.IsFalse(ok);
        StringAssert.Contains(err, "loopback");
    }

    [TestMethod]
    public void TryValidateRemoteUri_UnsupportedScheme_Blocks()
    {
        bool ok = McpRemoteEndpointValidator.TryValidateRemoteUri(
            new Uri("ftp://files.example.com/mcp", UriKind.Absolute),
            out string? err);

        Assert.IsFalse(ok);
        StringAssert.Contains(err, "Unsupported MCP URL scheme");
    }

    [TestMethod]
    public void TryValidateRemoteUri_RelativeUri_Blocks()
    {
        bool ok = McpRemoteEndpointValidator.TryValidateRemoteUri(
            new Uri("servers/foo/mcp.json", UriKind.Relative),
            out string? err);

        Assert.IsFalse(ok);
        StringAssert.Contains(err, "absolute");
    }
}
