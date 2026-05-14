using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Hermes.Agent.Mcp;
using HermesDesktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;

namespace HermesDesktop.Views;

public sealed partial class McpPage : Page
{
    private static readonly ResourceLoader Loader = new();

    public McpPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildConfigPaths();
        RefreshStatuses();
    }

    private void BuildConfigPaths()
    {
        ConfigPathsPanel.Children.Clear();

        // Prefer the exact ordered list that App bootstrap recorded so this dashboard never
        // claims to inspect a different set of paths than the agent actually inspected. Fall
        // back to a fresh computation only if bootstrap never ran (e.g. tests, errors).
        var mgr = App.Services.GetRequiredService<McpManager>();
        IReadOnlyList<string> paths = mgr.BootstrapConfigSearchPaths.Count > 0
            ? mgr.BootstrapConfigSearchPaths
            : McpBootstrap.BuildMcpConfigSearchPaths(
                Path.Combine(HermesEnvironment.HermesHomePath, "hermes-cs"),
                HermesEnvironment.HermesHomePath);

        foreach (string path in paths)
        {
            bool exists = File.Exists(path);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = exists ? "✓" : "—",
                Width = 22,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = (Brush)Application.Current.Resources["AppAccentTextBrush"],
            });
            row.Children.Add(new TextBlock
            {
                Text = path,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
            ConfigPathsPanel.Children.Add(row);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshStatuses();

    private void RefreshStatuses()
    {
        var mgr = App.Services.GetRequiredService<McpManager>();
        ServerStatusPanel.Children.Clear();
        foreach (var s in mgr.GetRuntimeStatuses())
        {
            string connected = s.IsConnected
                ? Loader.GetString("McpStatusConnected")
                : Loader.GetString("McpStatusDisconnected");
            string line = string.Format(
                CultureInfo.CurrentCulture,
                Loader.GetString("McpStatusLineFormat"),
                s.Name,
                s.TransportLabel,
                connected,
                s.RegisteredToolCount);
            ServerStatusPanel.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 13,
                Foreground = (Brush)Application.Current.Resources["AppTextPrimaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }

        LoadIssuesPanel.Children.Clear();
        var issues = mgr.ConfigLoadIssues;
        if (issues.Count == 0)
        {
            LoadIssuesPanel.Children.Add(new TextBlock
            {
                Text = Loader.GetString("McpIssuesNone"),
                FontSize = 13,
                Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"],
            });
            return;
        }

        foreach (var issue in issues)
        {
            string text = string.Format(
                CultureInfo.CurrentCulture,
                Loader.GetString("McpIssueLineFormat"),
                issue.ServerName,
                issue.Reason);
            LoadIssuesPanel.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = (Brush)Application.Current.Resources["AppAccentTextBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }
}
