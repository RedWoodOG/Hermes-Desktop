using System.Globalization;
using Hermes.Agent.Analytics;
using Hermes.Agent.LLM;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace HermesDesktop.Views;

/// <summary>
/// Bundle E.3 — token usage footer + chat-path InsightsService accumulation.
///
/// Renders prompt / completion / total counts in <c>UsageFooter</c> as soon as a
/// <see cref="ChatStreamEventKind.Usage"/> envelope arrives from the chat stream.
/// Also feeds <see cref="InsightsService.RecordTokens"/> so the Dashboard and
/// <c>InsightsCostText</c> start reporting real numbers.
/// </summary>
public partial class ChatPage
{
    // The static ResourceLoader is declared once in ChatPage.xaml.cs and shared across all
    // partial files of this class (Bugbot 2026-05-14: no redundant per-file loaders).

    // Per-session running totals — reset by ChatService.ResetConversation via NewChat_Click.
    private long _sessionInputTokens;
    private long _sessionOutputTokens;

    partial void OnUsageReceived(UsageStats? usage)
    {
        if (usage is null) return;

        _lastUsageStats = usage;
        _sessionInputTokens += usage.InputTokens;
        _sessionOutputTokens += usage.OutputTokens;

        // Hand off to InsightsService for cross-session aggregation. We do this on every
        // turn (not just at session end) so the Dashboard updates live while the chat runs.
        var insights = App.Services.GetService<InsightsService>();
        if (insights is not null)
        {
            var model = _runtimeStatusService.GetConfiguredSnapshot().DisplayModel;
            insights.RecordTokens(model, usage.InputTokens, usage.OutputTokens);
        }

        RenderUsageFooter();
    }

    private void RenderUsageFooter()
    {
        var total = _sessionInputTokens + _sessionOutputTokens;

        UsageFooterText.Text = string.Format(
            CultureInfo.CurrentCulture,
            ResourceLoader.GetString("ChatUsageFooterFormat"),
            _sessionInputTokens,
            _sessionOutputTokens,
            total);

        UsageFooter.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Reset the per-session usage counters and hide the footer.
    /// Wired from <c>NewChat_Click</c> when starting a fresh conversation.
    /// </summary>
    private void ResetUsageFooter()
    {
        _sessionInputTokens = 0;
        _sessionOutputTokens = 0;
        _lastUsageStats = null;
        UsageFooter.Visibility = Visibility.Collapsed;
        UsageFooterText.Text = string.Empty;
    }
}
