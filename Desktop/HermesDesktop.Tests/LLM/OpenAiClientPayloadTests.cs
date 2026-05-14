using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.LLM;

/// <summary>
/// Bundle E.1 tests for <c>OpenAiClient.BuildPayload</c>.
///
/// Validates that streaming requests carry <c>stream_options.include_usage = true</c> so
/// OpenAI-compatible servers (OpenAI, OpenRouter, Groq, DeepSeek, vLLM, LM Studio) emit
/// a final usage chunk. Non-streaming requests must NOT carry the field — some servers
/// reject the combination with HTTP 400.
/// </summary>
[TestClass]
public class OpenAiClientPayloadTests
{
    private static object InvokeBuildPayload(bool stream, object? tools = null)
    {
        var client = new OpenAiClient(
            new LlmConfig
            {
                Provider = "openai",
                Model = "gpt-4o",
                BaseUrl = "https://api.openai.com/v1",
                ApiKey = "test"
            },
            new HttpClient(),
            credentialPool: null);

        var method = typeof(OpenAiClient).GetMethod(
            "BuildPayload",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "BuildPayload private method not found on OpenAiClient.");

        var messages = new[] { new Message { Role = "user", Content = "hi" } };
        var result = method!.Invoke(client, new[] { messages, tools, stream });
        Assert.IsNotNull(result);
        return result!;
    }

    private static JsonElement SerializeAndParse(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [TestMethod]
    public void BuildPayload_StreamingNoTools_IncludesStreamOptionsIncludeUsage()
    {
        var payload = InvokeBuildPayload(stream: true);
        var root = SerializeAndParse(payload);

        Assert.IsTrue(root.TryGetProperty("stream", out var streamEl));
        Assert.AreEqual(true, streamEl.GetBoolean());

        Assert.IsTrue(
            root.TryGetProperty("stream_options", out var streamOpts),
            "Streaming payload must include stream_options.");
        Assert.IsTrue(streamOpts.TryGetProperty("include_usage", out var includeUsage));
        Assert.AreEqual(true, includeUsage.GetBoolean());
    }

    [TestMethod]
    public void BuildPayload_StreamingWithTools_IncludesStreamOptionsIncludeUsage()
    {
        var tools = new object[]
        {
            new
            {
                type = "function",
                function = new { name = "bash", description = "Run a shell command", parameters = new { } }
            }
        };

        var payload = InvokeBuildPayload(stream: true, tools: tools);
        var root = SerializeAndParse(payload);

        Assert.IsTrue(root.TryGetProperty("stream", out var streamEl));
        Assert.AreEqual(true, streamEl.GetBoolean());

        Assert.IsTrue(
            root.TryGetProperty("stream_options", out var streamOpts),
            "Streaming-with-tools payload must include stream_options.");
        Assert.IsTrue(streamOpts.TryGetProperty("include_usage", out var includeUsage));
        Assert.AreEqual(true, includeUsage.GetBoolean());

        Assert.IsTrue(root.TryGetProperty("tools", out _));
        Assert.IsTrue(root.TryGetProperty("tool_choice", out _));
    }

    [TestMethod]
    public void BuildPayload_NonStreaming_OmitsStreamOptions()
    {
        var payload = InvokeBuildPayload(stream: false);
        var root = SerializeAndParse(payload);

        Assert.IsTrue(root.TryGetProperty("stream", out var streamEl));
        Assert.AreEqual(false, streamEl.GetBoolean());

        Assert.IsFalse(
            root.TryGetProperty("stream_options", out _),
            "Non-streaming payload must omit stream_options (some servers 400 otherwise).");
    }

    [TestMethod]
    public void BuildPayload_NonStreamingWithTools_OmitsStreamOptions()
    {
        var tools = new object[]
        {
            new
            {
                type = "function",
                function = new { name = "bash", description = "x", parameters = new { } }
            }
        };

        var payload = InvokeBuildPayload(stream: false, tools: tools);
        var root = SerializeAndParse(payload);

        Assert.IsTrue(root.TryGetProperty("stream", out var streamEl));
        Assert.AreEqual(false, streamEl.GetBoolean());
        Assert.IsFalse(root.TryGetProperty("stream_options", out _));
    }
}
