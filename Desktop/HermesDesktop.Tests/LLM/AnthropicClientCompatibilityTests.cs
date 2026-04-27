using System.Net;
using System.Text;
using System.Text.Json;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.LLM;

[TestClass]
public class AnthropicClientCompatibilityTests
{
    [TestMethod]
    public void ChatClientFactory_Minimax_UsesAnthropicCompatibleClient()
    {
        var factory = new ChatClientFactory(
            new LlmConfig
            {
                Provider = "minimax",
                Model = "MiniMax-M2.7",
                BaseUrl = "https://api.minimaxi.com/anthropic/v1",
                ApiKey = "test-key"
            },
            new HttpClient(),
            NullLogger<ChatClientFactory>.Instance);

        Assert.IsInstanceOfType(factory.Current, typeof(AnthropicClient));
    }

    [TestMethod]
    public async Task CompleteWithToolsAsync_Minimax_UsesConfiguredAnthropicEndpointAndBearerAuth()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        using var httpClient = new HttpClient(new CaptureHandler(request =>
        {
            capturedRequest = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateSuccessResponse();
        }));

        var client = new AnthropicClient(
            new LlmConfig
            {
                Provider = "minimax",
                Model = "MiniMax-M2.7",
                BaseUrl = "https://api.minimaxi.com/anthropic/v1",
                ApiKey = "test-key",
                MaxTokens = 4096
            },
            httpClient);

        await client.CompleteWithToolsAsync(
            new[] { new Message { Role = "user", Content = "hello" } },
            new[] { CreateToolDefinition() },
            CancellationToken.None);

        Assert.IsNotNull(capturedRequest);
        Assert.AreEqual(
            "https://api.minimaxi.com/anthropic/v1/messages",
            capturedRequest!.RequestUri!.ToString());
        Assert.AreEqual("Bearer test-key", capturedRequest.Headers.Authorization!.ToString());
        Assert.IsFalse(capturedRequest.Headers.Contains("x-api-key"));
        Assert.IsFalse(capturedRequest.Headers.Contains("anthropic-version"));

        Assert.IsFalse(string.IsNullOrWhiteSpace(capturedBody));
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.AreEqual(2048, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.IsTrue(doc.RootElement.TryGetProperty("tools", out _));
    }

    [TestMethod]
    public async Task CompleteWithToolsAsync_Anthropic_UsesConfiguredEndpointAndAnthropicHeaders()
    {
        HttpRequestMessage? capturedRequest = null;
        using var httpClient = new HttpClient(new CaptureHandler(request =>
        {
            capturedRequest = request;
            return CreateSuccessResponse();
        }));

        var client = new AnthropicClient(
            new LlmConfig
            {
                Provider = "anthropic",
                Model = "claude-sonnet-4-5",
                BaseUrl = "https://proxy.example/anthropic/v1",
                ApiKey = "test-key",
                MaxTokens = 4096
            },
            httpClient);

        await client.CompleteWithToolsAsync(
            new[] { new Message { Role = "user", Content = "hello" } },
            new[] { CreateToolDefinition() },
            CancellationToken.None);

        Assert.IsNotNull(capturedRequest);
        Assert.AreEqual(
            "https://proxy.example/anthropic/v1/messages",
            capturedRequest!.RequestUri!.ToString());
        Assert.IsTrue(capturedRequest.Headers.Contains("x-api-key"));
        Assert.IsTrue(capturedRequest.Headers.Contains("anthropic-version"));
        Assert.IsNull(capturedRequest.Headers.Authorization);
    }

    private static ToolDefinition CreateToolDefinition()
    {
        using var schema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string" }
              }
            }
            """);

        return new ToolDefinition
        {
            Name = "search",
            Description = "Search for a short query",
            Parameters = schema.RootElement.Clone()
        };
    }

    private static HttpResponseMessage CreateSuccessResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}
                """,
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responseFactory(request));
        }
    }
}
