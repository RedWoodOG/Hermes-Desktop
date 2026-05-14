using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using Hermes.Agent.Skills;

namespace HermesDesktop.Views;

public sealed partial class SkillsPage : Page
{
    private static readonly ResourceLoader StringResources = new();

    private List<SkillDisplayItem> _allSkills = new();
    private string _selectedCategory = "All";
    private bool _initialized;
    private bool _suppressToggleHandler;

    public SkillsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { _initialized = true; Refresh(); };
    }

    private SkillManager Manager => App.Services.GetRequiredService<SkillManager>();
    private SkillsHub Hub => App.Services.GetRequiredService<SkillsHub>();

    private void Refresh()
    {
        try
        {
            var skills = Manager.ListSkills();

            _allSkills = skills.Select(s =>
            {
                var item = new SkillDisplayItem
                {
                    Name = s.Name,
                    Description = s.Description,
                    Category = DeriveCategory(s),
                    SystemPrompt = s.SystemPrompt,
                    Tools = string.Join(", ", s.Tools),
                    ToolCount = s.Tools.Count switch
                    {
                        0 => "",
                        1 => "1 tool",
                        _ => $"{s.Tools.Count} tools"
                    },
                    IsEnabled = s.IsEnabled
                };
                item.ApplyCategoryColor();
                return item;
            }).OrderBy(s => s.Name).ToList();

            BuildCategoryChips();
            ApplyFilter();
            UpdateQuarantineBadge();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SkillsPage error: {ex.Message}");
            _allSkills = new List<SkillDisplayItem>();
            ApplyFilter();
        }
    }

    private void BuildCategoryChips()
    {
        CategoryChips.Children.Clear();

        var categories = _allSkills
            .Select(s => s.Category)
            .Where(c => !string.IsNullOrEmpty(c))
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .Take(8)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .ToList();

        AddChip("All", _allSkills.Count, "All");

        foreach (var (name, count) in categories)
            AddChip(name, count, name);
    }

    private void AddChip(string label, int count, string tag)
    {
        var isSelected = tag == _selectedCategory;
        var btn = new Button
        {
            Content = count > 0 ? $"{label} ({count})" : label,
            Tag = tag,
            FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(12),
            Background = isSelected
                ? (Brush)Application.Current.Resources["AppAccentBrush"]
                : (Brush)Application.Current.Resources["AppInsetBrush"],
            Foreground = isSelected
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 22, 17, 13))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            BorderThickness = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0
        };
        btn.Click += CategoryChip_Click;
        CategoryChips.Children.Add(btn);
    }

    private void CategoryChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string cat)
        {
            _selectedCategory = cat;
            BuildCategoryChips();
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var filtered = _allSkills.AsEnumerable();

        if (_selectedCategory != "All")
            filtered = filtered.Where(s => s.Category.Equals(_selectedCategory, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(query))
            filtered = filtered.Where(s =>
                s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Tools.Contains(query, StringComparison.OrdinalIgnoreCase));

        var sortTag = (SortSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "NameAsc";
        var sorted = sortTag switch
        {
            "NameDesc" => filtered.OrderByDescending(s => s.Name),
            "Category" => filtered.OrderBy(s => s.Category).ThenBy(s => s.Name),
            _ => filtered.OrderBy(s => s.Name)
        };

        var list = sorted.ToList();
        // try/finally so a throwing ItemsSource setter cannot leave the page in a state
        // where user toggles are silently ignored forever (CodeRabbit, 2026-05-14).
        _suppressToggleHandler = true;
        try
        {
            SkillsList.ItemsSource = list;
        }
        finally
        {
            _suppressToggleHandler = false;
        }
        SkillCountBadge.Text = $"{list.Count} skill{(list.Count == 1 ? "" : "s")}";
        if (ListHeader is not null)
            ListHeader.Text = _selectedCategory == "All" ? "All Skills" : _selectedCategory;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized) ApplyFilter();
    }

    private void SortSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized) ApplyFilter();
    }

    private void SkillsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SkillsList.SelectedItem is SkillDisplayItem item)
        {
            PreviewTitle.Text = item.Name;
            PreviewDescription.Text = item.Description;
            PreviewContent.Text = item.SystemPrompt;

            PreviewCategoryChip.Text = item.Category;
            PreviewToolsChip.Text = string.IsNullOrEmpty(item.Tools) ? "No tools" : $"Tools: {item.Tools}";
            PreviewChips.Visibility = Visibility.Visible;
            DeleteSkillButton.IsEnabled = true;
            UpdatePreviewStateChip(item.IsEnabled);
        }
        else
        {
            DeleteSkillButton.IsEnabled = false;
        }
    }

    private void UpdatePreviewStateChip(bool isEnabled)
    {
        if (isEnabled)
        {
            PreviewStateChipText.Text = StringResources.GetString("SkillsStateEnabled");
            PreviewStateChip.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1A, 0x3C, 0x28));
            PreviewStateChipText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x60, 0xE0, 0x90));
        }
        else
        {
            PreviewStateChipText.Text = StringResources.GetString("SkillsStateDisabled");
            PreviewStateChip.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x3C, 0x28, 0x1A));
            PreviewStateChipText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0xA0, 0x60));
        }
    }

    private void SkillToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleHandler) return;
        if (sender is not ToggleSwitch ts || ts.Tag is not string name) return;

        // Remember the prior position so a SetEnabled failure can restore the switch
        // and keep UI consistent with the on-disk skill state (CodeRabbit, 2026-05-14).
        bool requestedState = ts.IsOn;
        bool priorState = !requestedState;

        try
        {
            var skill = Manager.SetEnabled(name, requestedState);
            var item = _allSkills.FirstOrDefault(s => s.Name == name);
            if (item is not null) item.IsEnabled = skill.IsEnabled;
            if (SkillsList.SelectedItem is SkillDisplayItem selected && selected.Name == name)
                UpdatePreviewStateChip(skill.IsEnabled);
        }
        catch (Exception ex)
        {
            // Persistence failed — revert the switch to the prior position so the UI
            // does not drift from the underlying skill state. Suppress the revert's
            // own Toggled event so it doesn't recurse.
            _suppressToggleHandler = true;
            try
            {
                ts.IsOn = priorState;
            }
            finally
            {
                _suppressToggleHandler = false;
            }
            ShowStatus(string.Format(StringResources.GetString("SkillsInstallFailureFormat"), ex.Message), isError: true);
        }
    }

    private async void InstallSkill_Click(object sender, RoutedEventArgs e)
    {
        var name = (InstallNameBox.Text ?? string.Empty).Trim();
        var url = (InstallUrlBox.Text ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowStatus(StringResources.GetString("SkillsInstallNameRequired"), isError: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowStatus(StringResources.GetString("SkillsInstallUrlRequired"), isError: true);
            return;
        }

        InstallButton.IsEnabled = false;
        ShowStatus("Installing...", isError: false);
        try
        {
            var result = await Hub.InstallAsync(name, url, CancellationToken.None);
            if (result.Success)
            {
                ShowStatus(string.Format(StringResources.GetString("SkillsInstallSuccessFormat"), result.Skill?.Name ?? name),
                    isError: false);
                InstallNameBox.Text = string.Empty;
                InstallUrlBox.Text = string.Empty;
                Refresh();
            }
            else
            {
                ShowStatus(string.Format(StringResources.GetString("SkillsInstallFailureFormat"), result.Error ?? "unknown"),
                    isError: true);
            }
        }
        catch (Exception ex)
        {
            ShowStatus(string.Format(StringResources.GetString("SkillsInstallFailureFormat"), ex.Message), isError: true);
        }
        finally
        {
            InstallButton.IsEnabled = true;
            UpdateQuarantineBadge();
        }
    }

    private async void BrowseHub_Click(object sender, RoutedEventArgs e)
    {
        var repoBox = new TextBox
        {
            PlaceholderText = "owner/repo",
            Margin = new Thickness(0, 0, 0, 8)
        };
        var resultsList = new ListView
        {
            Height = 240,
            Background = (Brush)Application.Current.Resources["AppInsetBrush"]
        };
        var emptyText = new TextBlock
        {
            Text = StringResources.GetString("SkillsBrowseEmpty"),
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"],
            Visibility = Visibility.Collapsed
        };

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = StringResources.GetString("SkillsBrowseDialogBody"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"]
        });
        content.Children.Add(repoBox);
        content.Children.Add(resultsList);
        content.Children.Add(emptyText);

        var dialog = new ContentDialog
        {
            Title = StringResources.GetString("SkillsBrowseDialogTitle"),
            Content = content,
            PrimaryButtonText = StringResources.GetString("SkillsBrowseDialogSearch"),
            CloseButtonText = StringResources.GetString("SkillsBrowseDialogClose"),
            XamlRoot = XamlRoot
        };

        async Task RunSearchAsync()
        {
            var repo = (repoBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(repo)) return;
            try
            {
                var remote = await Hub.SearchGitHubAsync(repo, CancellationToken.None);
                resultsList.ItemsSource = remote
                    .Where(r => !r.IsCategory)
                    .Select(r => new RemoteSkillRow
                    {
                        Name = r.Name,
                        Source = r.Source,
                        DownloadUrl = r.DownloadUrl ?? string.Empty
                    })
                    .ToList();
                emptyText.Visibility = remote.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                emptyText.Text = string.Format(StringResources.GetString("SkillsInstallFailureFormat"), ex.Message);
                emptyText.Visibility = Visibility.Visible;
            }
        }

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                await RunSearchAsync();
                args.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        };

        resultsList.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is RemoteSkillRow row)
            {
                InstallNameBox.Text = row.Name;
                InstallUrlBox.Text = row.DownloadUrl;
                dialog.Hide();
            }
        };
        resultsList.IsItemClickEnabled = true;
        resultsList.SelectionMode = ListViewSelectionMode.None;
        resultsList.ItemTemplate = BuildRemoteSkillRowTemplate();

        await dialog.ShowAsync();
    }

    private static DataTemplate BuildRemoteSkillRowTemplate()
    {
        const string xaml = @"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
    <StackPanel Margin='8,6'>
        <TextBlock Text='{Binding Name}' FontWeight='SemiBold' Foreground='White'/>
        <TextBlock Text='{Binding Source}' FontSize='11' Foreground='#A0A0A0'/>
    </StackPanel>
</DataTemplate>";
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    private async void DeleteSkill_Click(object sender, RoutedEventArgs e)
    {
        if (SkillsList.SelectedItem is not SkillDisplayItem selected) return;
        var name = selected.Name;

        var confirm = new ContentDialog
        {
            Title = string.Format(StringResources.GetString("SkillsDeleteConfirmTitleFormat"), name),
            Content = StringResources.GetString("SkillsDeleteConfirmBody"),
            PrimaryButtonText = StringResources.GetString("SkillsDeleteConfirmYes"),
            CloseButtonText = StringResources.GetString("SkillsDeleteConfirmCancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        var result = await confirm.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        try
        {
            await Manager.DeleteSkillAsync(name, CancellationToken.None);
            ShowStatus($"Deleted {name}.", isError: false);
            ClearPreview();
            Refresh();
        }
        catch (Exception ex)
        {
            ShowStatus(string.Format(StringResources.GetString("SkillsInstallFailureFormat"), ex.Message), isError: true);
        }
    }

    private void ClearPreview()
    {
        PreviewTitle.Text = "Select a skill to preview";
        PreviewDescription.Text = "Click a skill from the list to see its full system prompt here.";
        PreviewContent.Text = string.Empty;
        PreviewChips.Visibility = Visibility.Collapsed;
        DeleteSkillButton.IsEnabled = false;
    }

    private void ShowStatus(string message, bool isError)
    {
        InstallStatus.Text = message;
        InstallStatus.Foreground = isError
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0x70, 0x70))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x60, 0xE0, 0x90));
        InstallStatus.Visibility = Visibility.Visible;
    }

    private void UpdateQuarantineBadge()
    {
        try
        {
            var quarantineDir = Path.Combine(
                HermesDesktop.Services.HermesEnvironment.HermesHomePath,
                "hermes-cs", "skills", ".quarantine");
            if (!Directory.Exists(quarantineDir))
            {
                QuarantineBadge.Visibility = Visibility.Collapsed;
                return;
            }
            var count = Directory.EnumerateFiles(quarantineDir, "*.md").Count();
            if (count > 0)
            {
                QuarantineBadgeText.Text = string.Format(
                    StringResources.GetString("SkillsQuarantineBadgeFormat"), count);
                QuarantineBadge.Visibility = Visibility.Visible;
            }
            else
            {
                QuarantineBadge.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            QuarantineBadge.Visibility = Visibility.Collapsed;
        }
    }

    private static string DeriveCategory(Skill skill)
    {
        if (!string.IsNullOrEmpty(skill.FilePath))
        {
            var skillDir = Path.GetDirectoryName(skill.FilePath);
            if (skillDir is not null)
            {
                var categoryDir = Path.GetDirectoryName(skillDir);
                if (categoryDir is not null)
                {
                    var category = Path.GetFileName(categoryDir);
                    if (!string.IsNullOrEmpty(category) && !category.Equals("skills", StringComparison.OrdinalIgnoreCase))
                        return category;
                }
                var folder = Path.GetFileName(skillDir);
                if (!string.IsNullOrEmpty(folder) && !folder.Equals("skills", StringComparison.OrdinalIgnoreCase))
                    return folder;
            }
        }

        var tools = string.Join(" ", skill.Tools).ToLower();
        if (tools.Contains("bash") || tools.Contains("terminal")) return "automation";
        if (tools.Contains("read_file") && tools.Contains("write_file")) return "code";
        if (tools.Contains("grep") || tools.Contains("glob")) return "analysis";
        return "general";
    }
}

public sealed class SkillDisplayItem
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public string Tools { get; set; } = "";
    public string ToolCount { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public SolidColorBrush BadgeBackground { get; set; } = new(Windows.UI.Color.FromArgb(255, 58, 58, 0));
    public SolidColorBrush BadgeForeground { get; set; } = new(Windows.UI.Color.FromArgb(255, 230, 190, 60));

    private static readonly Dictionary<string, (byte R, byte G, byte B, byte FR, byte FG, byte FB)> CategoryColors = new()
    {
        ["claude-code"]           = (0x1A, 0x3A, 0x5C, 0x60, 0xB0, 0xE0),
        ["apple"]                 = (0x1A, 0x3C, 0x1A, 0x60, 0xE0, 0x60),
        ["creative"]              = (0x3A, 0x1A, 0x3C, 0xC0, 0x80, 0xE0),
        ["autonomous-ai-agents"]  = (0x3C, 0x1A, 0x1A, 0xE0, 0x70, 0x70),
        ["note-taking"]           = (0x3A, 0x3A, 0x00, 0xE6, 0xBE, 0x3C),
        ["productivity"]          = (0x0A, 0x3A, 0x2A, 0x50, 0xE0, 0xA0),
        ["smart-home"]            = (0x1A, 0x2A, 0x3C, 0x60, 0xA0, 0xE0),
        ["data-science"]          = (0x3C, 0x2A, 0x0A, 0xE0, 0xA0, 0x50),
        ["mlops"]                 = (0x2A, 0x1A, 0x3C, 0xA0, 0x70, 0xE0),
        ["devops"]                = (0x1A, 0x30, 0x3C, 0x50, 0xC0, 0xE0),
        ["research"]              = (0x30, 0x30, 0x1A, 0xC0, 0xC0, 0x60),
        ["software-development"]  = (0x1A, 0x3C, 0x28, 0x60, 0xE0, 0x90),
        ["github"]                = (0x2A, 0x2A, 0x2A, 0xC0, 0xC0, 0xC0),
        ["media"]                 = (0x3C, 0x1A, 0x2A, 0xE0, 0x60, 0xA0),
        ["gaming"]                = (0x2A, 0x1A, 0x30, 0xB0, 0x70, 0xD0),
        ["email"]                 = (0x1A, 0x2A, 0x30, 0x60, 0xA0, 0xC0),
        ["mcp"]                   = (0x20, 0x35, 0x20, 0x80, 0xD0, 0x80),
        ["feeds"]                 = (0x35, 0x25, 0x10, 0xD0, 0x90, 0x50),
        ["diagramming"]           = (0x25, 0x20, 0x35, 0x90, 0x80, 0xD0),
        ["inference-sh"]          = (0x30, 0x20, 0x20, 0xD0, 0x80, 0x80),
        ["social-media"]          = (0x20, 0x20, 0x38, 0x80, 0x80, 0xE0),
        ["red-teaming"]           = (0x3C, 0x10, 0x10, 0xE0, 0x50, 0x50),
        ["domain"]                = (0x28, 0x28, 0x1A, 0xB0, 0xB0, 0x60),
        ["leisure"]               = (0x1A, 0x30, 0x30, 0x60, 0xC0, 0xC0),
    };

    public void ApplyCategoryColor()
    {
        if (CategoryColors.TryGetValue(Category.ToLower(), out var c))
        {
            BadgeBackground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
            BadgeForeground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.FR, c.FG, c.FB));
        }
    }
}

internal sealed class RemoteSkillRow
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
}
