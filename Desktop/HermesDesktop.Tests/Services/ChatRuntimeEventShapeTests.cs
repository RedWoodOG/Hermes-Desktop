using System.Text.Json;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Services;

/// <summary>
/// Regression coverage for the Codex P2 finding: the chat runtime stream must
/// carry structured Tool* + Usage variants so the Bundle E.3 usage footer and
/// the <c>/usage</c> slash command actually display real numbers. These tests
/// pin down the <see cref="ChatRuntimeEvent"/> shape so a future refactor
/// cannot silently drop the wiring again.
/// </summary>
[TestClass]
public class ChatRuntimeEventShapeTests
{
    [TestMethod]
    public void TokenDelta_WhenConstructed_CarriesText()
    {
        var evt = new ChatRuntimeEvent.TokenDelta("hello");
        Assert.AreEqual("hello", evt.Text);
    }

    [TestMethod]
    public void ThinkingDelta_WhenConstructed_CarriesText()
    {
        var evt = new ChatRuntimeEvent.ThinkingDelta("reasoning…");
        Assert.AreEqual("reasoning…", evt.Text);
    }

    [TestMethod]
    public void ToolUseStart_WhenConstructed_CarriesIdAndName()
    {
        var evt = new ChatRuntimeEvent.ToolUseStart("call_42", "bash");
        Assert.AreEqual("call_42", evt.Id);
        Assert.AreEqual("bash", evt.Name);
    }

    [TestMethod]
    public void ToolUseDelta_WhenConstructed_CarriesIdAndPartialJson()
    {
        var evt = new ChatRuntimeEvent.ToolUseDelta("call_42", "{\"cmd\":\"ls\"");
        Assert.AreEqual("call_42", evt.Id);
        Assert.AreEqual("{\"cmd\":\"ls\"", evt.PartialJson);
    }

    [TestMethod]
    public void ToolUseComplete_WhenConstructed_ExposesParsedArguments()
    {
        var args = JsonDocument.Parse("{\"cmd\":\"ls -la\"}").RootElement;
        var evt = new ChatRuntimeEvent.ToolUseComplete("call_42", "bash", args);

        Assert.AreEqual("call_42", evt.Id);
        Assert.AreEqual("bash", evt.Name);
        Assert.AreEqual("ls -la", evt.Arguments.GetProperty("cmd").GetString());
    }

    [TestMethod]
    public void Usage_WithStopReason_CarriesStatsAndStopReason()
    {
        var stats = new UsageStats(InputTokens: 120, OutputTokens: 480);
        var evt = new ChatRuntimeEvent.Usage(stats, StopReason: "end_turn");

        Assert.AreEqual(120, evt.Stats.InputTokens);
        Assert.AreEqual(480, evt.Stats.OutputTokens);
        Assert.AreEqual("end_turn", evt.StopReason);
    }

    [TestMethod]
    public void Usage_StopReason_IsOptional()
    {
        var stats = new UsageStats(InputTokens: 1, OutputTokens: 2);
        var evt = new ChatRuntimeEvent.Usage(stats);
        Assert.IsNull(evt.StopReason);
    }

    [TestMethod]
    public void Error_WhenConstructed_CarriesDetail()
    {
        var detail = new ChatRuntimeError("boom", Code: "rate_limit", Retryable: true);
        var evt = new ChatRuntimeEvent.Error(detail);

        Assert.AreEqual("boom", evt.Detail.Message);
        Assert.AreEqual("rate_limit", evt.Detail.Code);
        Assert.IsTrue(evt.Detail.Retryable);
    }

    [TestMethod]
    public void Completed_WhenConstructed_CarriesSessionId()
    {
        var evt = new ChatRuntimeEvent.Completed("sess_abc");
        Assert.AreEqual("sess_abc", evt.SessionId);
    }
}
