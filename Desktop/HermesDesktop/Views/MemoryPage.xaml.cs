using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Hermes.Agent.Memory;
using Hermes.Agent.Soul;
using HermesDesktop.Services;

namespace HermesDesktop.Views;

/// <summary>
/// Memory management page.
///
/// Bundle E.5 fixes a long-standing path drift bug: the previous implementation read
/// <c>.md</c> files directly from <c>%HERMES_HOME%\hermes-cs\memory</c>, while the agent
/// runtime (via <see cref="MemoryManager"/>) writes to <c>projectDir/memory</c>. The page
/// now uses <see cref="MemoryManager"/> as its single source of truth.
/// </summary>
public sealed partial class MemoryPage : Page
{
    private MemoryManager? _memoryManager;
    private SoulService? _soulService;
    private string _activeTab = "memories";
    private readonly List<MemoryPageItem> _memories = new();
    private MemoryPageItem? _selected;

    public MemoryPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _memoryManager = App.Services?.GetService<MemoryManager>();
            _soulService = App.Services?.GetService<SoulService>();
            Refresh();
        };
    }

    private void Refresh()
    {
        if (_activeTab == "memories") _ = RefreshMemoriesAsync();
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

    // ── Memories tab (bound to MemoryManager) ──

    private async Task RefreshMemoriesAsync()
    {
        _memories.Clear();
        _selected = null;
        ClearEditor();

        if (_memoryManager is null)
        {
            MemoryList.ItemsSource = _memories;
            EmptyState.Visibility = Visibility.Visible;
            MemoryCountBadge.Text = "0";
            return;
        }

        try
        {
            var loaded = await _memoryManager.LoadAllMemoriesAsync(CancellationToken.None);
            foreach (var mem in loaded.OrderByDescending(m => File.Exists(m.Path)
                ? File.GetLastWriteTimeUtc(m.Path)
                : DateTime.MinValue))
            {
                var path = mem.Path;
                var lastWrite = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.UtcNow;
                var daysOld = (DateTime.UtcNow - lastWrite).TotalDays;
                var type = string.IsNullOrEmpty(mem.Type) ? "unknown" : mem.Type;

                _memories.Add(new MemoryPageItem
                {
                    Filename = mem.Filename,
                    FullPath = path,
                    Type = type,
                    Content = mem.Content,
                    LastWriteUtc = lastWrite,
                    Age = FormatAge(lastWrite),
                    TypeColor = GetTypeColor(type),
                    AgeBrush = daysOld > 30 ? new SolidColorBrush(ColorHelper.FromArgb(255, 255, 100, 100))
                             : daysOld > 14 ? new SolidColorBrush(ColorHelper.FromArgb(255, 255, 200, 100))
                             : new SolidColorBrush(ColorHelper.FromArgb(255, 100, 200, 100))
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MemoryPage refresh failed: {ex}");
        }

        MemoryList.ItemsSource = _memories;
        EmptyState.Visibility = _memories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MemoryCountBadge.Text = _memories.Count.ToString();
    }

    private void MemoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MemoryList.SelectedItem is MemoryPageItem item)
        {
            _selected = item;
            MemoryPreviewTitle.Text = item.Filename;
            MemoryEditorPath.Text = item.FullPath;
            MemoryEditor.Text = item.Content;
            MemoryEditor.IsEnabled = true;
            SaveMemoryButton.IsEnabled = true;
            DeleteMemoryButton.IsEnabled = true;
            MemorySaveStatus.Text = "";

            UpdateFreshnessChip(item.LastWriteUtc);
        }
        else
        {
            ClearEditor();
        }
    }

    private async void SaveMemory_Click(object sender, RoutedEventArgs e)
    {
        if (_memoryManager is null || _selected is null) return;

        try
        {
            MemorySaveStatus.Text = "Saving…";
            await _memoryManager.SaveMemoryAsync(
                _selected.Filename,
                MemoryEditor.Text,
                _selected.Type,
                CancellationToken.None);

            MemorySaveStatus.Text = "Saved";
            await Task.Delay(1500);
            MemorySaveStatus.Text = "";
            await RefreshMemoriesAsync();
        }
        catch (Exception ex)
        {
            MemorySaveStatus.Text = $"Error: {ex.Message}";
        }
    }

    private async void DeleteMemory_Click(object sender, RoutedEventArgs e)
    {
        if (_memoryManager is null || _selected is null) return;

        var confirm = new ContentDialog
        {
            Title = "Delete memory?",
            Content = $"This will permanently delete {_selected.Filename}.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };

        var result = await confirm.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        try
        {
            await _memoryManager.DeleteMemoryAsync(_selected.Filename, CancellationToken.None);
            await RefreshMemoriesAsync();
        }
        catch (Exception ex)
        {
            MemorySaveStatus.Text = $"Error: {ex.Message}";
        }
    }

    private void ClearEditor()
    {
        _selected = null;
        MemoryPreviewTitle.Text = "Select a memory to preview";
        MemoryEditorPath.Text = string.Empty;
        MemoryEditor.Text = string.Empty;
        MemoryEditor.IsEnabled = false;
        SaveMemoryButton.IsEnabled = false;
        DeleteMemoryButton.IsEnabled = false;
        FreshnessChip.Visibility = Visibility.Collapsed;
        MemorySaveStatus.Text = "";
    }

    private void UpdateFreshnessChip(DateTime lastWriteUtc)
    {
        var days = (DateTime.UtcNow - lastWriteUtc).TotalDays;

        // Mirrors MemoryManager.GetFreshnessWarning thresholds. Less than a day → no chip
        // (matches the runtime's "too fresh, no warning" branch).
        if (days < 1)
        {
            FreshnessChip.Visibility = Visibility.Collapsed;
            return;
        }

        var label = days switch
        {
            < 2 => "1 day old — verify before relying on this",
            < 7 => $"{(int)days} days old — verify before relying on this",
            < 30 => $"{(int)(days / 7)} weeks old — verify before relying on this",
            _ => $"{(int)(days / 30)} months old — likely stale, audit before reuse",
        };

        FreshnessChipText.Text = label;
        FreshnessChip.Visibility = Visibility.Visible;
    }

    // ── Project tab (unchanged) ──

    private async Task RefreshProjectAsync()
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
            await Task.Delay(2000);
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
        "user" => new SolidColorBrush(ColorHelper.FromArgb(255, 80, 140, 200)),
        "feedback" => new SolidColorBrush(ColorHelper.FromArgb(255, 200, 140, 80)),
        "project" => new SolidColorBrush(ColorHelper.FromArgb(255, 100, 180, 100)),
        "reference" => new SolidColorBrush(ColorHelper.FromArgb(255, 160, 100, 180)),
        _ => new SolidColorBrush(ColorHelper.FromArgb(255, 120, 120, 120)),
    };
}

public sealed class MemoryPageItem
{
    public string Filename { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Type { get; set; } = "unknown";
    public string Content { get; set; } = "";
    public DateTime LastWriteUtc { get; set; } = DateTime.UtcNow;
    public string Age { get; set; } = "";
    public SolidColorBrush TypeColor { get; set; } = new(ColorHelper.FromArgb(255, 100, 100, 100));
    public SolidColorBrush AgeBrush { get; set; } = new(ColorHelper.FromArgb(255, 149, 162, 177));
}
