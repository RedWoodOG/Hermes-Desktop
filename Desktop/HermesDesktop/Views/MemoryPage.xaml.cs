using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Hermes.Agent.Soul;
using HermesDesktop.Services;

namespace HermesDesktop.Views;

public sealed partial class MemoryPage : Page
{
    private readonly string _memoryDir;
    private SoulService? _soulService;
    private string _activeTab = "memories";
    private List<MemoryPageItem> _memories = new();

    public MemoryPage()
    {
        InitializeComponent();
        _memoryDir = Path.Combine(HermesEnvironment.HermesHomePath, "hermes-cs", "memory");
        Loaded += (_, _) =>
        {
            _soulService = App.Services?.GetService<SoulService>();
            Refresh();
        };
    }

    private void Refresh()
    {
        if (_activeTab == "memories") RefreshMemories();
        else if (_activeTab == "project") _ = RefreshProjectAsync();
    }

    // ── Tab switching ──

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        _activeTab = tag;

        var activeBg = Application.Current.Resources["AppAccentGradientBrush"] as Brush;
        var inactiveBg = Application.Current.Resources["AppInsetBrush"] as Brush;
        var activeFg = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 22, 17, 13));
        var inactiveFg = Application.Current.Resources["AppTextSecondaryBrush"] as Brush;

        TabMemories.Background = tag == "memories" ? activeBg : inactiveBg;
        TabMemories.Foreground = tag == "memories" ? activeFg : inactiveFg;
        TabProject.Background = tag == "project" ? activeBg : inactiveBg;
        TabProject.Foreground = tag == "project" ? activeFg : inactiveFg;

        MemoriesContent.Visibility = tag == "memories" ? Visibility.Visible : Visibility.Collapsed;
        ProjectContent.Visibility = tag == "project" ? Visibility.Visible : Visibility.Collapsed;

        Refresh();
    }

    // ── Memories tab ──

    private void RefreshMemories()
    {
        _memories.Clear();

        if (!Directory.Exists(_memoryDir))
        {
            MemoryList.ItemsSource = _memories;
            EmptyState.Visibility = Visibility.Visible;
            MemoryCountBadge.Text = "0";
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_memoryDir, "*.md").OrderByDescending(f => File.GetLastWriteTimeUtc(f)))
        {
            try
            {
                var content = File.ReadAllText(file);
                var type = "unknown";

                if (content.StartsWith("---"))
                {
                    var end = content.IndexOf("---", 3);
                    if (end > 0)
                    {
                        var fm = content[3..end];
                        var typeLine = fm.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("type:"));
                        if (typeLine is not null) type = typeLine.Split(':', 2)[1].Trim();
                    }
                }

                var lastWrite = File.GetLastWriteTimeUtc(file);
                var age = FormatAge(lastWrite);
                var daysOld = (DateTime.UtcNow - lastWrite).TotalDays;

                _memories.Add(new MemoryPageItem
                {
                    Filename = Path.GetFileName(file),
                    FullPath = file,
                    Type = type,
                    Content = content,
                    Age = age,
                    TypeColor = GetTypeColor(type),
                    AgeBrush = daysOld > 30 ? new SolidColorBrush(ColorHelper.FromArgb(255, 255, 100, 100))
                             : daysOld > 14 ? new SolidColorBrush(ColorHelper.FromArgb(255, 255, 200, 100))
                             : new SolidColorBrush(ColorHelper.FromArgb(255, 100, 200, 100))
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MemoryPage skipping unreadable memory file {file}: {ex}");
            }
        }

        MemoryList.ItemsSource = _memories;
        EmptyState.Visibility = _memories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MemoryCountBadge.Text = _memories.Count.ToString();
    }

    private void MemoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MemoryList.SelectedItem is MemoryPageItem item)
        {
            MemoryPreviewTitle.Text = item.Filename;
            MemoryPreviewText.Text = item.Content;
        }
    }

    // ── Project tab ──

    private async System.Threading.Tasks.Task RefreshProjectAsync()
    {
        if (_soulService is null) return;

        try
        {
            AgentsEditor.Text = await _soulService.LoadFileAsync(SoulFileType.ProjectRules);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MemoryPage project error: {ex.Message}");
        }
    }

    private async void SaveAgents_Click(object sender, RoutedEventArgs e)
    {
        if (_soulService is null) return;

        try
        {
            await _soulService.SaveFileAsync(SoulFileType.ProjectRules, AgentsEditor.Text);
            SaveStatus.Text = "Saved!";
            await System.Threading.Tasks.Task.Delay(2000);
            SaveStatus.Text = "";
        }
        catch (Exception ex)
        {
            SaveStatus.Text = $"Error: {ex.Message}";
        }
    }

    // ── Helpers ──

    private static string FormatAge(DateTime timestamp)
    {
        var diff = DateTime.UtcNow - timestamp;
        if (diff.TotalHours < 1) return "just now";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 30) return $"{(int)diff.TotalDays}d ago";
        return $"{(int)(diff.TotalDays / 30)}mo ago";
    }

    private static SolidColorBrush GetTypeColor(string type) => type switch
    {
        "user" => new SolidColorBrush(ColorHelper.FromArgb(255, 80, 140, 200)),       // #508CC8
        "feedback" => new SolidColorBrush(ColorHelper.FromArgb(255, 200, 140, 80)),    // #C88C50
        "project" => new SolidColorBrush(ColorHelper.FromArgb(255, 100, 180, 100)),    // #64B464
        "reference" => new SolidColorBrush(ColorHelper.FromArgb(255, 160, 100, 180)),  // #A064B4
        _ => new SolidColorBrush(ColorHelper.FromArgb(255, 120, 120, 120))
    };
}

public sealed class MemoryPageItem
{
    public string Filename { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Type { get; set; } = "unknown";
    public string Content { get; set; } = "";
    public string Age { get; set; } = "";
    public SolidColorBrush TypeColor { get; set; } = new(ColorHelper.FromArgb(255, 100, 100, 100));
    public SolidColorBrush AgeBrush { get; set; } = new(ColorHelper.FromArgb(255, 149, 162, 177));
}
