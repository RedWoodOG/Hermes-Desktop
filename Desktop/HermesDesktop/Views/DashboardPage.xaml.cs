using System.Globalization;
using HermesDesktop.Models;
using HermesDesktop.Services;
using Hermes.Agent.Core;
using Hermes.Agent.Memory;
using Hermes.Agent.Skills;
using Hermes.Agent.Transcript;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;

namespace HermesDesktop.Views;

public sealed class JourneyDisplayItem
{
    public string Title { get; set; } = "";
    public string Meta { get; set; } = "";
    public SolidColorBrush StatusColor { get; set; } = new(ColorHelper.FromArgb(255, 42, 53, 69));
}

public sealed partial class DashboardPage : Page
{
    private static readonly ResourceLoader ResourceLoader = new();

    public DashboardPage()
    {
        InitializeComponent();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // Header
        var projectName = Path.GetFileName(HermesEnvironment.AgentWorkingDirectory);
        var workspaceName = GetWorkspaceName();
        var version = typeof(DashboardPage).Assembly.GetName().Version;
        var versionStr = version is not null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "";

        PageTitle.Text = projectName;
        BreadcrumbWorkspace.Text = workspaceName;
        BreadcrumbProject.Text = projectName;

        // Brain graph
        var graphService = App.Services.GetRequiredService<BrainGraphService>();
        var graph = await graphService.BuildGraphAsync(CancellationToken.None);
        BrainCanvas.SetGraphData(graph);

        // Stats from graph
        PopulateVitals(graph);

        // Subtitle (after graph so we have session count)
        var sessionCount = graph.Nodes.Count(n => n.Type == BrainNodeType.Session);
        PageSubtitle.Text = $"{workspaceName} \u2014 {sessionCount} active sessions \u2014 {versionStr}";

        // Recent journeys
        await PopulateJourneysAsync();
    }

    private void PopulateVitals(BrainGraphData graph)
    {
        var sessions = graph.Nodes.Count(n => n.Type == BrainNodeType.Session);
        var tools = graph.Nodes.Count(n => n.Type == BrainNodeType.Tool);
        var memories = graph.Nodes.Count(n => n.Type == BrainNodeType.Memory);

        StatSessions.Text = sessions.ToString();
        StatTools.Text = tools.ToString();
        StatMemories.Text = memories.ToString();

        // Sub labels
        var transcripts = App.Services?.GetService<TranscriptStore>();
        var totalSessions = transcripts?.GetAllSessionIds().Count ?? sessions;
        StatSessionsSub.Text = $"{totalSessions} total";

        var agent = App.Services?.GetService<Agent>();
        StatToolsSub.Text = $"{agent?.Tools.Count ?? tools} registered";

        StatMemoriesSub.Text = $"{memories} this project";
    }

    private async Task PopulateJourneysAsync()
    {
        var transcripts = App.Services?.GetService<TranscriptStore>();
        if (transcripts is null) return;

        var sessionIds = transcripts.GetAllSessionIds();
        var items = new List<JourneyDisplayItem>();

        foreach (var id in sessionIds.TakeLast(5).Reverse())
        {
            try
            {
                var messages = await transcripts.LoadSessionAsync(id, CancellationToken.None);
                if (messages.Count == 0) continue;

                var firstUser = messages.FirstOrDefault(m => m.Role == "user");
                var title = firstUser?.Content ?? "Untitled";
                if (title.Length > 60) title = title[..60] + "...";

                var lastMsg = messages[^1];
                var age = DateTime.UtcNow - lastMsg.Timestamp;

                items.Add(new JourneyDisplayItem
                {
                    Title = title,
                    Meta = $"{messages.Count}t \u00B7 {FormatTimeAgo(age)}",
                    StatusColor = age.TotalMinutes < 5
                        ? new SolidColorBrush(ColorHelper.FromArgb(255, 73, 194, 125))  // #49C27D
                        : age.TotalDays < 7
                            ? new SolidColorBrush(ColorHelper.FromArgb(255, 212, 160, 23)) // #D4A017
                            : new SolidColorBrush(ColorHelper.FromArgb(255, 42, 53, 69)),  // #2A3545
                });
            }
            catch
            {
                // Skip unreadable sessions
            }
        }

        JourneysList.ItemsSource = items;
    }

    private string GetWorkspaceName()
    {
        var workDir = HermesEnvironment.AgentWorkingDirectory;
        var parent = Path.GetDirectoryName(workDir);
        return parent is not null ? Path.GetFileName(parent) : "workspace";
    }

    private static string FormatTimeAgo(TimeSpan age)
    {
        if (age.TotalSeconds < 60) return "now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }
}
