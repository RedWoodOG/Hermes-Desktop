namespace Hermes.Agent.Dreamer;

using System.Net;
using System.Net.Http;

/// <summary>
/// Creates long-lived Dreamer HTTP clients with no shared auth/header state.
/// Authentication for Dreamer LLM calls must be applied on each HttpRequestMessage,
/// never through HttpClient.DefaultRequestHeaders on the shared client instance.
/// </summary>
public static class DreamerHttpClientFactory
{
    public static HttpClient Create(TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            UseProxy = false
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout
        };

        ResetDefaultHeaders(client);
        return client;
    }

    /// <summary>
    /// Clears any shared default headers so reused Dreamer clients remain safe for
    /// request-scoped auth and do not carry credentials between requests.
    /// </summary>
    public static void ResetDefaultHeaders(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.ProxyAuthorization = null;
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.AcceptEncoding.Clear();
        client.DefaultRequestHeaders.UserAgent.Clear();
    }
}
