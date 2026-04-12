using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Hermes.Agent.Soul;
using HermesDesktop.Services;

namespace HermesDesktop.Views.Panels;

public sealed class MemoryListItem
{
    public string Filename { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Type { get; set; } = "unknown";
    public string Content { get; set; } = "";
    public string Age { get; set; } = "";
    public SolidColorBrush TypeColor { get; set; } = new(ColorHelper.FromArgb(255, 100, 100, 100));
    public SolidColorBrush AgeBrush { get; set; } = new(ColorHelper.FromArgb(255, 149, 162, 177));
}

public sealed class MistakeDisplayItem
{
    public string Lesson { get; set; } = "";
    public string TimestampText { get; set; } = "";
}

public sealed class HabitDisplayItem
{
    public string Habit { get; set; } = "";
    public string TimestampText { get; set; } = "";
}

public sealed partial class MemoryPanel : UserControl
{
    private readonly string _memoryDir;
    private SoulService? _soulService;
    private string _activeTab = "memories";

    public ObservableCollection<MemoryListItem> Memories { get; } = new();

    public MemoryPanel()
    {
        InitializeComponent();
        _memoryDir = Path.Combine(HermesEnvironment.HermesHomePath, "hermes-cs", "memory");
        Loaded += (_, _) =>
        {
            _soulService = App.Services?.GetService<SoulService>();
            Refresh();
        };
    }

    public void Refresh()
    {
        if (_activeTab == "memories") RefreshMemories();
        else if (_activeTab == "soul") _ = RefreshSoulAsync();
        else if (_activeTab == "project") _ = RefreshProjectAsync();
    }

    // ── Tab switching ──

    private void SubTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _activeTab = tag;

            // Update tab button styles
            var accentBrush = Application.Current.Resources["AppAccentTextBrush"] as Brush;
            var secondaryBrush = Application.Current.Resources["AppTextSecondaryBrush"] as Brush;
            TabMemories.Foreground = tag == "memories" ? accentBrush : secondaryBrush;
            TabSoul.Foreground = tag == "soul" ? accentBrush : secondaryBrush;
            TabProject.Foreground = tag == "project" ? accentBrush : secondaryBrush;

            // Switch content visibility
            MemoriesContent.Visibility = tag == "memories" ? Visibility.Visible : Visibility.Collapsed;
            SoulContent.Visibility = tag == "soul" ? Visibility.Visible : Visibility.Collapsed;
            ProjectContent.Visibility = tag == "project" ? Visibility.Visible : Visibility.Collapsed;

            Refresh();
        }
    }

    // ── Memories tab ──

    private void RefreshMemories()
    {
        Memories.Clear();
        if (!Directory.Exists(_memoryDir))
        {
            MemoryList.ItemsSource = Memories;
            EmptyState.Visibility = Visibility.Visible;
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

                Memories.Add(new MemoryListItem
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
                System.Diagnostics.Debug.WriteLine($"MemoryPanel skipping unreadable memory file {file}: {ex}");
            }
        }
        MemoryList.ItemsSource = Memories;
        EmptyState.Visibility = Memories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MemoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MemoryList.SelectedItem is MemoryListItem item)
        {
            EditorText.Text = item.Content;
            EditorBorder.Visibility = Visibility.Visible;
        }
    }

    // ── Soul tab ──

    private async System.Threading.Tasks.Task RefreshSoulAsync()
    {
        if (_soulService is null) return;

        try
        {
            SoulEditor.Text = await _soulService.LoadFileAsync(SoulFileType.Soul);
            UserEditor.Text = await _soulService.LoadFileAsync(SoulFileType.User);

            // Load mistakes
            var mistakes = await _soulService.LoadMistakesAsync();
            var mistakeItems = mistakes.TakeLast(20).Reverse().Select(m => new MistakeDisplayItem
            {
                Lesson = m.Lesson,
                TimestampText = m.Timestamp.ToString("yyyy-MM-dd HH:mm")
            }).ToList();
            MistakesList.ItemsSource = mistakeItems;
            NoMistakes.Visibility = mistakeItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MistakesList.Visibility = mistakeItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Load habits
            var habits = await _soulService.LoadHabitsAsync();
            var habitItems = habits.TakeLast(20).Reverse().Select(h => new HabitDisplayItem
            {
                Habit = h.Habit,
                TimestampText = h.Timestamp.ToString("yyyy-MM-dd HH:mm")
            }).ToList();
            HabitsList.ItemsSource = habitItems;
            NoHabits.Visibility = habitItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HabitsList.Visibility = habitItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load soul data: {ex.Message}");
        }
    }

    private async void SaveSoul_Click(object sender, RoutedEventArgs e)
    {
        if (_soulService is null) return;
        await _soulService.SaveFileAsync(SoulFileType.Soul, SoulEditor.Text);
    }

    private async void SaveUser_Click(object sender, RoutedEventArgs e)
    {
        if (_soulService is null) return;
        await _soulService.SaveFileAsync(SoulFileType.User, UserEditor.Text);
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
            System.Diagnostics.Debug.WriteLine($"Failed to load project rules: {ex.Message}");
        }
    }

    private async void SaveAgents_Click(object sender, RoutedEventArgs e)
    {
        if (_soulService is null) return;
        await _soulService.SaveFileAsync(SoulFileType.ProjectRules, AgentsEditor.Text);
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
        "user" => new SolidColorBrush(ColorHelper.FromArgb(255, 80, 140, 200)),
        "feedback" => new SolidColorBrush(ColorHelper.FromArgb(255, 200, 140, 80)),
        "project" => new SolidColorBrush(ColorHelper.FromArgb(255, 100, 180, 100)),
        "reference" => new SolidColorBrush(ColorHelper.FromArgb(255, 160, 100, 180)),
        _ => new SolidColorBrush(ColorHelper.FromArgb(255, 120, 120, 120))
    };
}
