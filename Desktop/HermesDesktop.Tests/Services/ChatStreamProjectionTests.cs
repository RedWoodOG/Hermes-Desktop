using System.Text.Json;
using Hermes.Agent.LLM;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

/// <summary>
/// Bundle E.1 tests for <see cref="ChatStreamProjection"/>.
///
/// Validates that every <see cref="StreamEvent"/> variant projects to the expected
/// <see cref="ChatStreamEnvelope"/> shape and that <c>AccumulatedText</c> is set
/// only for the content-bearing branch (<c>TokenDelta</c>).
/// </summary>
[TestClass]
public class ChatStreamProjectionTests
{
    // ── TokenDelta ──

    [TestMethod]
    public void Project_PlainTokenDelta_AccumulatesAndYieldsToken()
    {
        var result = ChatStreamProjection.Project(new StreamEvent.TokenDelta("Hello"));

        Assert.IsNotNull(result.Envelope);
        Assert.AreEqual(ChatStreamEventKind.Token, result.Envelope!.Kind);
        Assert.AreEqual("Hello", result.Envelope.Text);
        Assert.IsNull(result.Envelope.ToolName);
        Assert.IsNull(result.Envelope.Usage);
        Assert.AreEqual("Hello", result.AccumulatedText);
    }

    [TestMethod]
    public void Project_LegacyCallingToolMarkerInTokenDelta_YieldsThinkingWithoutAccumulation()
    {
        var result = ChatStreamProjection.Project(new StreamEvent.TokenDelta("\n[Calling tool: bash]\n"));

        Assert.IsNotNull(result.Envelope);
        Assert.AreEqual(ChatStreamEventKind.Thinking, result.Envelope!.Kind);
        Assert.AreEqual("[Calling tool: bash]", result.Envelope.Text);
        Assert.IsNull(result.AccumulatedText);
    }

    [TestMethod]
    public void Project_BracketedNonToolTokenDelta_YieldsToken()
    {
        var result = ChatStreamProjection.Project(new StreamEvent.TokenDelta("[Note: this is normal output]"));

        Assert.IsNotNull(result.Envelope);
        Assert.AreEqual(ChatStreamEventKind.Token, result.Envelope!.Kind);
    }

    // ── ThinkingDelta ──

    [TestMethod]
    public void Project_ThinkingDelta_YieldsThinkingWithoutAccumulation()
    {
        var result = ChatStreamProjection.Project(new StreamEvent.ThinkingDelta("Let me think..."));

        Assert.IsNotNull(result.Envelope);
        Assert.AreEqual(ChatStreamEventKind.Thinking, result.Envelope!.Kind);
        Assert.AreEqual("Let me think...", result.Envelope.Text);
        Assert.IsNull(result.AccumulatedText);
    }

    // ── ToolUseStart ──

    [TestMethod]
    public void Project_ToolUseStart_YieldsToolStartWithIdAndName()
    {
        var result = ChatStreamProjection.Project(new StreamEvent.ToolUseStart("call_123", "bash"));

        Assert.IsNotNull(result.Envelope);
        Assert.AreEqual(ChatStreamEventKind.ToolStart, result.Envelope!.Kind);
        Assert.AreEqual(string.Empty, result.Envelope.Text);
        Assert.AreEqual("bash", result.Envelope.ToolName);
        Assert.AreEqual("call_123", result.Envelope.ToolCallId);
        Assert.IsNull(result.AccumulatedText);
    }

    // ── ToolUseDelta ──

    [TestMethod]
    public void Project_ToolUseDelta_YieldsToolDeltaWithPartialJson()
    {
        var result = ChatStreamProjection.Project(new StreamEvent.ToolUseDelta("call_123", "{\"cmd\":\"l"));

        Assert.IsNotNull(result.Envelope);
        Assert.AreEqual(ChatStreamEventKind.ToolDelta, result.Envelope!.Kind);
        Assert.AreEqual("{\"cmd\":\"l", result.Envelope.Text);
        Assert.AreEqual("call_123", result.Envelope.ToolCallId);
        Assert.IsNull(result.Envelope.ToolName);
        Assert.IsNull(result.AccumulatedText);
    }

    // ── ToolUseComplete ──

    [TestMethod]
    public void Project_ToolUseComplete_YieldsToolCompleteWithRawJson()
    {
        using var doc = JsonDocument.Parse("{\"cmd\":\"ls\"}");
        var result = ChatStreamProjection.Project(
            new StreamEvent.ToolUseComplete("call_123", "bash", doc.RootElement));

        Assert.IsNotNull(result.Envelope);
        Assert.AreEqual(ChatStreamEventKind.ToolComplete, result.Envelope!.Kind);
        Assert.AreEqual("{\"cmd\":\"ls\"}", result.Envelope.Text);
        Assert.AreEqual("bash", result.Envelope.ToolName);
        Assert.AreEqual("call_123", result.Envelope.ToolCallId);
        Assert.IsNull(result.AccumulatedText);
    }

    // ── MessageComplete ──

    [TestMethod]
    public void Project_MessageCompleteWithUsage_YieldsUsage()
    {
        var usage = new UsageStats(InputTokens: 120, OutputTokens: 45, CacheReadTokens: 30);
        var result = ChatStreamProjection.Project(new StreamEvent.MessageComplete("stop", usage));

        Assert.IsNotNull(result.Envelope);
        Assert.AreEqual(ChatStreamEventKind.Usage, result.Envelope!.Kind);
        Assert.AreEqual("stop", result.Envelope.Text);
        Assert.AreSame(usage, result.Envelope.Usage);
        Assert.IsNull(result.AccumulatedText);
    }

    [TestMethod]
    public void Project_MessageCompleteWithoutUsage_YieldsNothing()
    {
        var result = ChatStreamProjection.Project(new StreamEvent.MessageComplete("stop", null));

        Assert.IsNull(result.Envelope);
        Assert.IsNull(result.AccumulatedText);
    }

    // ── StreamError ──

    [TestMethod]
    public void Project_StreamError_YieldsErrorWithExceptionMessage()
    {
        var ex = new System.InvalidOperationException("Connection lost");
        var result = ChatStreamProjection.Project(
            new StreamEvent.StreamError(ex, ProviderErrorCode.Transport));

        Assert.IsNotNull(result.Envelope);
        Assert.AreEqual(ChatStreamEventKind.Error, result.Envelope!.Kind);
        Assert.AreEqual("Connection lost", result.Envelope.Text);
        Assert.IsNull(result.AccumulatedText);
    }

    // ── Coverage sanity: every enum value is reachable from some StreamEvent ──

    [TestMethod]
    public void Project_AcrossAllStreamEvents_CoversEverySupportedKind()
    {
        using var doc = JsonDocument.Parse("{}");
        var samples = new StreamEvent[]
        {
            new StreamEvent.TokenDelta("x"),
            new StreamEvent.ThinkingDelta("x"),
            new StreamEvent.ToolUseStart("i", "n"),
            new StreamEvent.ToolUseDelta("i", "{"),
            new StreamEvent.ToolUseComplete("i", "n", doc.RootElement),
            new StreamEvent.MessageComplete("stop", new UsageStats(1, 1)),
            new StreamEvent.StreamError(new System.Exception("e")),
        };

        var kinds = samples
            .Select(ChatStreamProjection.Project)
            .Select(r => r.Envelope?.Kind)
            .Where(k => k is not null)
            .Select(k => k!.Value)
            .ToHashSet();

        // All seven enum values should appear at least once across the samples
        // (TokenDelta only produces Token here; the Thinking-via-legacy-marker
        // branch is exercised in a dedicated test above).
        CollectionAssert.AreEquivalent(
            new[]
            {
                ChatStreamEventKind.Token,
                ChatStreamEventKind.Thinking,
                ChatStreamEventKind.ToolStart,
                ChatStreamEventKind.ToolDelta,
                ChatStreamEventKind.ToolComplete,
                ChatStreamEventKind.Usage,
                ChatStreamEventKind.Error,
            },
            kinds.ToArray());
    }
}
