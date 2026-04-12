using System.Net.Http.Headers;
using Hermes.Agent.Dreamer;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

[TestClass]
public sealed class DreamerHttpClientFactoryTests
{
    [TestMethod]
    public void Create_ReturnsClientWithSanitizedDefaultHeaders()
    {
        using var client = DreamerHttpClientFactory.Create(TimeSpan.FromSeconds(45));

        Assert.AreEqual(TimeSpan.FromSeconds(45), client.Timeout);
        Assert.IsNull(client.DefaultRequestHeaders.Authorization);
        Assert.IsNull(client.DefaultRequestHeaders.ProxyAuthorization);
        Assert.AreEqual(0, client.DefaultRequestHeaders.Accept.Count);
        Assert.AreEqual(0, client.DefaultRequestHeaders.AcceptEncoding.Count);
        Assert.AreEqual(0, client.DefaultRequestHeaders.UserAgent.Count);
    }

    [TestMethod]
    public void ResetDefaultHeaders_RemovesSharedAuthAndMetadata()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        client.DefaultRequestHeaders.ProxyAuthorization = new AuthenticationHeaderValue("Basic", "proxy-secret");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HermesDesktop.Tests/1.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Test-Header", "value");

        DreamerHttpClientFactory.ResetDefaultHeaders(client);

        Assert.IsNull(client.DefaultRequestHeaders.Authorization);
        Assert.IsNull(client.DefaultRequestHeaders.ProxyAuthorization);
        Assert.AreEqual(0, client.DefaultRequestHeaders.Accept.Count);
        Assert.AreEqual(0, client.DefaultRequestHeaders.AcceptEncoding.Count);
        Assert.AreEqual(0, client.DefaultRequestHeaders.UserAgent.Count);
        Assert.IsFalse(client.DefaultRequestHeaders.Contains("X-Test-Header"));
    }
}
