using System.Globalization;
using HermesDesktop.Services;
using Hermes.Agent.Analytics;
using Hermes.Agent.Core;
using Hermes.Agent.Dreamer;
using Hermes.Agent.LLM;
using Hermes.Agent.Skills;
using Hermes.Agent.Soul;
using Hermes.Agent.Transcript;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.ApplicationModel.Resources;

namespace HermesDesktop.Views;

public sealed class SessionDisplayItem
{
    public string Preview { get; set; } = "";
    public string MessageCount { get; set; } = "";
    public string TimeAgo { get; set; } = "";
    public SolidColorBrush StatusColor { get; set; } = new(ColorHelper.FromArgb(255, 100, 100, 100));
}

public sealed partial class DashboardPage : Page
{
    private static readonly ResourceLoader ResourceLoader = new();
    private readonly RuntimeStatusService _runtimeStatusService = App.Services.GetRequiredService<RuntimeStatusService>();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dreamerTimer;

    /// <summary>
    /// Initializes a new instance of DashboardPage and configures page unload cleanup.
    /// </summary>
    public DashboardPage()
    {
        InitializeComponent();
        this.Unloaded += OnPageUnloaded;
    }

    private void OnDreamerTimerTick(object? sender, object e)
    {
        RefreshDreamerStatus();
    }

    /// <summary>
    /// Cleans up the Dreamer status refresh timer when the page is unloaded.
    /// </summary>
    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_dreamerTimer is not null)
        {
            _dreamerTimer.Stop();
            _dreamerTimer.Tick -= OnDreamerTimerTick;
            _dreamerTimer = null;
        }
    }

    // ── Data Properties (for x:Bind) ──
    public string HermesHomePath => HermesEnvironment.DisplayHermesHomePath;
    public string HermesConfigPath => HermesEnvironment.DisplayHermesConfigPath;

    // ── Lifecycle ──

    /// <summary>
    /// Initializes dashboard data and UI when the page is loaded.
    /// </summary>
    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        LoadStats();
        LoadPlatformBadges();
        LoadInsights();
        await LoadRecentSessionsAsync();
        await RefreshRuntimeStatusAsync();
        StartDreamerStatusRefresh();
    }

    /// <summary>
    /// Starts periodic updates of the Dreamer status at four-second intervals.
    /// </summary>
    private void StartDreamerStatusRefresh()
    {
        // Guard against duplicate timers (DispatcherQueueTimer has no IsEnabled; null means not created or already torn down)
        if (_dreamerTimer is not null)
            return;

        RefreshDreamerStatus();
        _dreamerTimer = DispatcherQueue.CreateTimer();
        _dreamerTimer.Interval = TimeSpan.FromSeconds(4);
        _dreamerTimer.Tick += OnDreamerTimerTick;
        _dreamerTimer.Start();
    }

    /// <summary>
    /// Updates Dreamer-related UI text fields from the current Dreamer status snapshot.
    /// </summary>
    private void RefreshDreamerStatus()
    {
        var st = App.Services.GetService<DreamerStatus>();
        if (st is null) return;
        var s = st.GetSnapshot();
        var culture = CultureInfo.CurrentCulture;
        DreamerPhaseText.Text = string.Format(culture, ResourceLoader.GetString("DashboardDreamerPhaseFormat"), s.Phase);
        DreamerWalkCountText.Text = string.Format(culture, ResourceLoader.GetString("DashboardDreamerWalksFormat"), s.WalkCount);
        DreamerSignalText.Text = string.IsNullOrEmpty(s.TopSignalSlug)
            ? ResourceLoader.GetString("DashboardDreamerTopSignalEmpty")
            : string.Format(culture, ResourceLoader.GetString("DashboardDreamerTopSignalFormat"), s.TopSignalSlug, s.TopSignalScore);
        DreamerLocalDigestText.Text = string.IsNullOrWhiteSpace(s.LastLocalDigestHint)
            ? ResourceLoader.GetString("DashboardDreamerLocalDigestEmpty")
            : string.Format(culture, ResourceLoader.GetString("DashboardDreamerLocalDigestFormat"), s.LastLocalDigestHint);
        DreamerPostcardText.Text = string.IsNullOrWhiteSpace(s.LastPostcardPreview)
            ? ResourceLoader.GetString("DashboardDreamerPostcardEmpty")
            : s.LastPostcardPreview.Trim();
        var alertBrush = (Brush?)Application.Current.Resources["ConnectionOfflineBrush"];
        var secondaryBrush = (Brush?)Application.Current.Resources["AppTextSecondaryBrush"];
        if (!string.IsNullOrWhiteSpace(s.StartupFailureMessage))
        {
            DreamerPostcardText.Text = string.Format(culture, ResourceLoader.GetString("DashboardDreamerStartupIssueFormat"), s.StartupFailureMessage);
            if (alertBrush is not null)
                DreamerPostcardText.Foreground = alertBrush;
        }
        else if (secondaryBrush is not null)
        {
            DreamerPostcardText.Foreground = secondaryBrush;
        }
    }

    // ── KPI Stats ──

    private void LoadStats()
    {
        // Session count
        var transcripts = App.Services?.GetService<TranscriptStore>();
        var sessionCount = transcripts?.GetAllSessionIds().Count ?? 0;
        SessionCountText.Text = sessionCount.ToString();

        // Tool count
        var agent = App.Services?.GetService<Agent>();
        ToolCountText.Text = (agent?.Tools.Count ?? 0).ToString();

        // Skill count
        var skillManager = App.Services?.GetService<SkillManager>();
        SkillCountText.Text = (skillManager?.ListSkills().Count ?? 0).ToString();

        // Active soul
        var profileManager = App.Services?.GetService<AgentProfileManager>();
        ActiveSoulText.Text = profileManager?.GetActiveProfileName()
            ?? ResourceLoader.GetString("DashboardSoulDefaultProfile");
    }

    // ── Platform Badges ──

    private void LoadPlatformBadges()
    {
        PlatformBadges.Children.Clear();
        ServiceBadges.Children.Clear();

        // Messaging platforms
        AddBadge(PlatformBadges, ResourceLoader.GetString("BadgeTelegram"), HermesEnvironment.TelegramConfigured, "#2AABEE");
        AddBadge(PlatformBadges, ResourceLoader.GetString("BadgeDiscord"), HermesEnvironment.DiscordConfigured, "#5865F2");
        AddBadge(PlatformBadges, ResourceLoader.GetString("BadgeSlack"), HermesEnvironment.SlackConfigured, "#4A154B");
        AddBadge(PlatformBadges, ResourceLoader.GetString("BadgeWhatsApp"), HermesEnvironment.WhatsAppConfigured, "#25D366");
        AddBadge(PlatformBadges, ResourceLoader.GetString("BadgeMatrix"), HermesEnvironment.MatrixConfigured, "#0DBD8B");
        AddBadge(PlatformBadges, ResourceLoader.GetString("BadgeWebhook"), HermesEnvironment.WebhookConfigured, "#F59E0B");

        // Services
        AddBadge(ServiceBadges, ResourceLoader.GetString("BadgeMemory"), true, "#6BCB77");
        AddBadge(ServiceBadges, ResourceLoader.GetString("BadgeSkills"), (App.Services?.GetService<SkillManager>()?.ListSkills().Count ?? 0) > 0, "#818CF8");
        AddBadge(ServiceBadges, ResourceLoader.GetString("BadgeDream"), true, "#C084FC");

        var soulService = App.Services?.GetService<SoulService>();
        AddBadge(ServiceBadges, ResourceLoader.GetString("BadgeSoul"), soulService is not null && !soulService.IsFirstRun(), "#FFD700");
    }

    private static void AddBadge(StackPanel parent, string label, bool active, string colorHex)
    {
        var color = ParseColor(colorHex);
        var border = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
                (byte)(active ? 40 : 15), color.R, color.G, color.B)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            Opacity = active ? 1.0 : 0.5
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        stack.Children.Add(new Ellipse
        {
            Width = 6, Height = 6,
            Fill = new SolidColorBrush(active ? color : Windows.UI.Color.FromArgb(255, 100, 100, 100)),
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = new SolidColorBrush(active ? color : Windows.UI.Color.FromArgb(255, 150, 150, 150))
        });

        border.Child = stack;
        parent.Children.Add(border);
    }

    // ── Runtime Status ──

    private async System.Threading.Tasks.Task RefreshRuntimeStatusAsync()
    {
        ApplyRuntimeStatusSnapshot(_runtimeStatusService.GetConfiguredSnapshot());
        var snapshot = await _runtimeStatusService.RefreshAsync(CancellationToken.None);
        ApplyRuntimeStatusSnapshot(snapshot);
    }

    private void ApplyRuntimeStatusSnapshot(RuntimeStatusSnapshot snapshot)
    {
        ProviderText.Text = snapshot.DisplayProvider;
        ModelNameText.Text = snapshot.DisplayModel;
        EndpointText.Text = snapshot.DisplayBaseUrl;
        LlmModelText.Text = snapshot.DisplayModel;
        SetLlmStatus(snapshot.ConnectionState);
    }

    // ── Recent Sessions ──

    private async System.Threading.Tasks.Task LoadRecentSessionsAsync()
    {
        var transcripts = App.Services?.GetService<TranscriptStore>();
        if (transcripts is null) return;

        var sessionIds = transcripts.GetAllSessionIds();
        var items = new List<SessionDisplayItem>();

        foreach (var id in sessionIds.TakeLast(15).Reverse())
        {
            try
            {
                var messages = await transcripts.LoadSessionAsync(id, CancellationToken.None);
                if (messages.Count == 0) continue;

                var firstUser = messages.FirstOrDefault(m => m.Role == "user");
                var preview = firstUser?.Content ?? ResourceLoader.GetString("DashboardSessionNoMessages");
                if (preview.Length > 80) preview = preview[..80] + "...";

                var lastMsg = messages[^1];
                var age = DateTime.UtcNow - lastMsg.Timestamp;

                items.Add(new SessionDisplayItem
                {
                    Preview = preview,
                    MessageCount = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("DashboardSessionMessageCountFormat"), messages.Count),
                    TimeAgo = FormatTimeAgo(age),
                    StatusColor = age.TotalMinutes < 5
                        ? new SolidColorBrush(ColorHelper.FromArgb(255, 34, 197, 94))
                        : new SolidColorBrush(ColorHelper.FromArgb(255, 100, 100, 100))
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DashboardPage skipping unreadable session {id}: {ex}");
            }
        }

        RecentSessionsList.ItemsSource = items;
        NoSessionsText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentSessionsList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetLlmStatus(RuntimeConnectionState state)
    {
        var color = state switch
        {
            RuntimeConnectionState.Connected => ColorHelper.FromArgb(255, 34, 197, 94),
            RuntimeConnectionState.Checking => ColorHelper.FromArgb(255, 245, 158, 11),
            _ => ColorHelper.FromArgb(255, 239, 68, 68),
        };

        LlmStatusDot.Fill = new SolidColorBrush(color);
        LlmStatusText.Text = state switch
        {
            RuntimeConnectionState.Connected => ResourceLoader.GetString("StatusConnected"),
            RuntimeConnectionState.Checking => ResourceLoader.GetString("ChatStatusChecking"),
            _ => ResourceLoader.GetString("StatusOffline"),
        };
    }

    // ── Test Connection ──

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        TestConnectionResult.Text = ResourceLoader.GetString("DashboardTestTesting");
        try
        {
            var chatClient = App.Services?.GetService<IChatClient>();
            if (chatClient is null)
            {
                TestConnectionResult.Text = ResourceLoader.GetString("DashboardTestNotConfigured");
                return;
            }

            var messages = new List<Message> { new() { Role = "user", Content = "Reply with exactly: OK" } };
            var result = await chatClient.CompleteAsync(messages, CancellationToken.None);
            TestConnectionResult.Text = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("DashboardTestSuccessFormat"), result.Trim());
            TestConnectionResult.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 34, 197, 94));
            await RefreshRuntimeStatusAsync();
        }
        catch (Exception ex)
        {
            TestConnectionResult.Text = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("DashboardTestFailedFormat"), ex.Message);
            TestConnectionResult.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 239, 68, 68));
            await RefreshRuntimeStatusAsync();
        }
    }

    // ── Usage Insights ──

    private void LoadInsights()
    {
        var insights = App.Services?.GetService<InsightsService>();
        if (insights is null)
        {
            InsightsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        InsightsPanel.Visibility = Visibility.Visible;
        var data = insights.GetInsights();

        InsightsTurnsText.Text = data.TotalTurns.ToString("N0");

        long totalToolCalls = 0;
        foreach (var ts in data.ToolUsage.Values)
            totalToolCalls += ts.TotalCalls;
        InsightsToolCallsText.Text = totalToolCalls.ToString("N0");

        InsightsCostText.Text = string.Format(CultureInfo.CurrentCulture, "{0:C4}", data.EstimatedCostUsd);

        TopToolsList.Children.Clear();
        var topTools = data.ToolUsage
            .OrderByDescending(kv => kv.Value.TotalCalls)
            .Take(5);

        foreach (var kv in topTools)
        {
            var avgMs = kv.Value.TotalCalls > 0 ? kv.Value.TotalDurationMs / kv.Value.TotalCalls : 0;
            TopToolsList.Children.Add(new TextBlock
            {
                Text = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("DashboardTopToolLineFormat"),
                    kv.Key, kv.Value.TotalCalls, avgMs),
                Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"],
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
    }

    // ── Actions ──

    private void LaunchHermesChat_Click(object sender, RoutedEventArgs e)
    {
        // Navigate to Chat page
        if (this.Frame is not null)
            this.Frame.Navigate(typeof(ChatPage));
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => HermesEnvironment.OpenLogs();
    private void OpenConfig_Click(object sender, RoutedEventArgs e) => HermesEnvironment.OpenConfig();

    // ── Helpers ──

    private string FormatTimeAgo(TimeSpan age)
    {
        var culture = CultureInfo.CurrentCulture;
        if (age.TotalSeconds < 60) return ResourceLoader.GetString("DashboardTimeAgoNow");
        if (age.TotalMinutes < 60) return string.Format(culture, ResourceLoader.GetString("DashboardTimeAgoMinutesFormat"), (int)age.TotalMinutes);
        if (age.TotalHours < 24) return string.Format(culture, ResourceLoader.GetString("DashboardTimeAgoHoursFormat"), (int)age.TotalHours);
        return string.Format(culture, ResourceLoader.GetString("DashboardTimeAgoDaysFormat"), (int)age.TotalDays);
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        return Windows.UI.Color.FromArgb(255,
            byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber),
            byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber));
    }
}
