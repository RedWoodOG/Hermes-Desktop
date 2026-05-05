using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.LLM;

[TestClass]
public class OpenAiClientAuthTests
{
    [TestMethod]
    public async Task CompleteAsync_DirtyLegacySystemMessages_SendsSingleLeadingSystem()
    {
        string? capturedJson = null;
        using var httpClient = new HttpClient(new CaptureHandler(request =>
        {
            capturedJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateSuccessResponse();
        }));

        var client = new OpenAiClient(
            new LlmConfig
            {
                Provider = "openai",
                Model = "qwen3.5-122b-a10b",
                BaseUrl = "http://vllm.example/v1",
                AuthMode = "none"
            },
            httpClient);

        await client.CompleteAsync(
            new[]
            {
                new Message { Role = "system", Content = "stable system" },
                new Message { Role = "user", Content = "first" },
                new Message { Role = "assistant", Content = "ok" },
                new Message { Role = "system", Content = "late system" },
                new Message { Role = "user", Content = "second" }
            },
            CancellationToken.None);

        AssertPayloadHasSingleLeadingSystem(
            capturedJson,
            new[] { "system", "user", "assistant", "user" },
            "stable system",
            "late system");
    }

    [TestMethod]
    public async Task CompleteWithToolsAsync_DirtyLegacySystemMessages_SendsSingleLeadingSystem()
    {
        string? capturedJson = null;
        using var httpClient = new HttpClient(new CaptureHandler(request =>
        {
            capturedJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return CreateToolCallResponse();
        }));

        var client = new OpenAiClient(
            new LlmConfig
            {
                Provider = "openai",
                Model = "qwen3.5-122b-a10b",
                BaseUrl = "http://vllm.example/v1",
                AuthMode = "none"
            },
            httpClient);

        await client.CompleteWithToolsAsync(
            new[]
            {
                new Message { Role = "system", Content = "tool system" },
                new Message { Role = "user", Content = "use a tool" },
                new Message { Role = "system", Content = "late tool system" }
            },
            new[]
            {
                new ToolDefinition
                {
                    Name = "echo_tool",
                    Description = "Echo input",
                    Parameters = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone()
                }
            },
            CancellationToken.None);

        AssertPayloadHasSingleLeadingSystem(
            capturedJson,
            new[] { "system", "user" },
            "tool system",
            "late tool system");
    }

    [TestMethod]
    public async Task StreamAsync_DirtyLegacySystemMessages_SendsSingleLeadingSystem()
    {
        string? capturedJson = null;
        var sse =
            """
            data: {"choices":[{"delta":{"content":"ok"}}]}

            data: [DONE]

            """;
            using var httpClient = new HttpClient(new CaptureHandler(request =>
            {
                capturedJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new SseTestContent(sse)
                };
            }));

        var client = new OpenAiClient(
            new LlmConfig
            {
                Provider = "openai",
                Model = "qwen3.5-122b-a10b",
                BaseUrl = "http://vllm.example/v1",
                AuthMode = "none"
            },
            httpClient);

        await foreach (var _ in client.StreamAsync(
                           new[]
                           {
                               new Message { Role = "system", Content = "stream system" },
                               new Message { Role = "user", Content = "stream" },
                               new Message { Role = "system", Content = "late stream system" }
                           },
                           CancellationToken.None))
        {
            // Drain SSE until completion
        }

        AssertPayloadHasSingleLeadingSystem(
            capturedJson,
            new[] { "system", "user" },
            "stream system",
            "late stream system");
    }

    [TestMethod]
    public async Task CompleteAsync_UsesEnvBackedOAuthProxyHeader()
    {
        const string envVarName = "HERMES_TEST_PROXY_TOKEN";
        Environment.SetEnvironmentVariable(envVarName, "oauth-token");

        try
        {
            HttpRequestMessage? capturedRequest = null;
            using var httpClient = new HttpClient(new CaptureHandler(request =>
            {
                capturedRequest = request;
                return CreateSuccessResponse();
            }));

            var client = new OpenAiClient(
                new LlmConfig
                {
                    Provider = "openai",
                    Model = "gpt-5.4",
                    BaseUrl = "https://proxy.example/v1",
                    AuthMode = "oauth_proxy_env",
                    AuthHeader = "Authorization",
                    AuthScheme = "Bearer",
                    AuthTokenEnv = envVarName
                },
                httpClient);

            var result = await client.CompleteAsync(
                new[] { new Message { Role = "user", Content = "hello" } },
                CancellationToken.None);

            Assert.AreEqual("ok", result);
            Assert.IsNotNull(capturedRequest);
            Assert.AreEqual("Bearer oauth-token", capturedRequest!.Headers.Authorization!.ToString());
            Assert.IsNull(httpClient.DefaultRequestHeaders.Authorization);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, null);
        }
    }

    [TestMethod]
    public async Task CompleteAsync_UsesCommandBackedOAuthProxyCustomHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        using var httpClient = new HttpClient(new CaptureHandler(request =>
        {
            capturedRequest = request;
            return CreateSuccessResponse();
        }));

        var client = new OpenAiClient(
            new LlmConfig
            {
                Provider = "openai",
                Model = "gpt-5.4",
                BaseUrl = "https://proxy.example/v1",
                AuthMode = "oauth_proxy_command",
                AuthHeader = "X-Proxy-Auth",
                AuthScheme = "",
                AuthTokenCommand = "echo cmd-token"
            },
            httpClient);

        var result = await client.CompleteAsync(
            new[] { new Message { Role = "user", Content = "hello" } },
            CancellationToken.None);

        Assert.AreEqual("ok", result);
        Assert.IsNotNull(capturedRequest);
        Assert.IsTrue(capturedRequest!.Headers.TryGetValues("X-Proxy-Auth", out var headerValues));
        CollectionAssert.AreEqual(new[] { "cmd-token" }, headerValues!.ToArray());
        Assert.IsNull(capturedRequest.Headers.Authorization);
        Assert.IsFalse(httpClient.DefaultRequestHeaders.Contains("X-Proxy-Auth"));
        Assert.IsNull(httpClient.DefaultRequestHeaders.Authorization);
    }

    [TestMethod]
    public async Task StreamAsync_StreamEvent_AppliesOAuthProxyEnvToOutgoingRequest()
    {
        const string envVarName = "HERMES_TEST_PROXY_TOKEN_STREAM";
        Environment.SetEnvironmentVariable(envVarName, "stream-oauth-token");

        try
        {
            HttpRequestMessage? capturedRequest = null;
            var sse =
                """
                data: {"choices":[{"delta":{"content":"z"}}]}

                data: [DONE]

                """;
            using var httpClient = new HttpClient(new CaptureHandler(request =>
            {
                capturedRequest = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new SseTestContent(sse)
                };
            }));

            var client = new OpenAiClient(
                new LlmConfig
                {
                    Provider = "openai",
                    Model = "gpt-5.4",
                    BaseUrl = "https://proxy.example/v1",
                    AuthMode = "oauth_proxy_env",
                    AuthHeader = "Authorization",
                    AuthScheme = "Bearer",
                    AuthTokenEnv = envVarName
                },
                httpClient);

            await foreach (var _ in client.StreamAsync(
                               null,
                               new[] { new Message { Role = "user", Content = "hello" } },
                               null,
                               CancellationToken.None))
            {
                // Drain SSE until completion
            }

            Assert.IsNotNull(capturedRequest);
            Assert.AreEqual(
                "Bearer stream-oauth-token",
                capturedRequest!.Headers.Authorization!.ToString());
            Assert.IsNull(httpClient.DefaultRequestHeaders.Authorization);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, null);
        }
    }

    [TestMethod]
    public async Task StreamAsync_Text_DoesNotMutateSharedHttpClientDefaultHeaders()
    {
        const string envVarName = "HERMES_TEST_PROXY_TOKEN_TEXT_SHARED";
        Environment.SetEnvironmentVariable(envVarName, "request-token");

        try
        {
            HttpRequestMessage? capturedRequest = null;
            var sse =
                """
                data: {"choices":[{"delta":{"content":"z"}}]}

                data: [DONE]

                """;
            using var httpClient = new HttpClient(new CaptureHandler(request =>
            {
                capturedRequest = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new SseTestContent(sse)
                };
            }));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "shared-default-token");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Shared-Header", "shared-default-value");

            var client = new OpenAiClient(
                new LlmConfig
                {
                    Provider = "openai",
                    Model = "gpt-5.4",
                    BaseUrl = "https://proxy.example/v1",
                    AuthMode = "oauth_proxy_env",
                    AuthHeader = "Authorization",
                    AuthScheme = "Bearer",
                    AuthTokenEnv = envVarName
                },
                httpClient);

            await foreach (var _ in client.StreamAsync(
                               new[] { new Message { Role = "user", Content = "hello" } },
                               CancellationToken.None))
            {
                // Drain SSE until completion
            }

            Assert.IsNotNull(capturedRequest);
            Assert.AreEqual("Bearer request-token", capturedRequest!.Headers.Authorization!.ToString());
            Assert.AreEqual("Bearer shared-default-token", httpClient.DefaultRequestHeaders.Authorization!.ToString());
            Assert.IsTrue(httpClient.DefaultRequestHeaders.TryGetValues("X-Shared-Header", out var sharedValues));
            CollectionAssert.AreEqual(new[] { "shared-default-value" }, sharedValues!.ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, null);
        }
    }

    [TestMethod]
    public async Task StreamAsync_StreamEvent_DoesNotMutateSharedHttpClientDefaultHeaders()
    {
        const string envVarName = "HERMES_TEST_PROXY_TOKEN_EVENT_SHARED";
        Environment.SetEnvironmentVariable(envVarName, "request-token");

        try
        {
            HttpRequestMessage? capturedRequest = null;
            var sse =
                """
                data: {"choices":[{"delta":{"content":"z"}}]}

                data: [DONE]

                """;
            using var httpClient = new HttpClient(new CaptureHandler(request =>
            {
                capturedRequest = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new SseTestContent(sse)
                };
            }));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "shared-default-token");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Shared-Header", "shared-default-value");

            var client = new OpenAiClient(
                new LlmConfig
                {
                    Provider = "openai",
                    Model = "gpt-5.4",
                    BaseUrl = "https://proxy.example/v1",
                    AuthMode = "oauth_proxy_env",
                    AuthHeader = "Authorization",
                    AuthScheme = "Bearer",
                    AuthTokenEnv = envVarName
                },
                httpClient);

            await foreach (var _ in client.StreamAsync(
                               null,
                               new[] { new Message { Role = "user", Content = "hello" } },
                               null,
                               CancellationToken.None))
            {
                // Drain SSE until completion
            }

            Assert.IsNotNull(capturedRequest);
            Assert.AreEqual("Bearer request-token", capturedRequest!.Headers.Authorization!.ToString());
            Assert.AreEqual("Bearer shared-default-token", httpClient.DefaultRequestHeaders.Authorization!.ToString());
            Assert.IsTrue(httpClient.DefaultRequestHeaders.TryGetValues("X-Shared-Header", out var sharedValues));
            CollectionAssert.AreEqual(new[] { "shared-default-value" }, sharedValues!.ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, null);
        }
    }

    private static HttpResponseMessage CreateSuccessResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}
                """,
                Encoding.UTF8,
                "application/json")
        };
    }

    private static HttpResponseMessage CreateToolCallResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"choices":[{"message":{"content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"echo_tool","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}
                """,
                Encoding.UTF8,
                "application/json")
        };
    }

    private static void AssertPayloadHasSingleLeadingSystem(
        string? capturedJson,
        string[] expectedRoles,
        params string[] expectedSystemFragments)
    {
        Assert.IsNotNull(capturedJson);
        using var payload = JsonDocument.Parse(capturedJson!);
        var messages = payload.RootElement.GetProperty("messages").EnumerateArray().ToArray();

        Assert.AreEqual("system", messages[0].GetProperty("role").GetString());
        var systemContent = messages[0].GetProperty("content").GetString()!;
        foreach (var expectedSystemFragment in expectedSystemFragments)
        {
            StringAssert.Contains(systemContent, expectedSystemFragment);
        }

        CollectionAssert.AreEqual(
            expectedRoles,
            messages.Select(message => message.GetProperty("role").GetString()).ToArray(),
            "OpenAI-compatible payloads must have at most one leading system message for strict vLLM/Qwen templates.");
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class SseTestContent(string payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            return stream.WriteAsync(bytes, 0, bytes.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Encoding.UTF8.GetByteCount(payload);
            return true;
        }
    }
}
