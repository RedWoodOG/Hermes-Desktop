using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Hermes.Agent.Skills;
using Windows.UI;

namespace HermesDesktop.Views;

public sealed partial class SkillsPage : Page
{
    private List<SkillDisplayItem> _allSkills = new();
    private string _activeCategory = "All";
    private bool _loaded;

    private static readonly Dictionary<string, (Color Bg, Color Fg)> CategoryColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["automation"]       = (Color.FromArgb(255, 40, 40, 60),  Color.FromArgb(255, 140, 160, 255)),
        ["code"]             = (Color.FromArgb(255, 25, 50, 35),  Color.FromArgb(255, 100, 200, 130)),
        ["analysis"]         = (Color.FromArgb(255, 50, 40, 20),  Color.FromArgb(255, 200, 170, 80)),
        ["general"]          = (Color.FromArgb(255, 40, 40, 40),  Color.FromArgb(255, 170, 170, 170)),
        ["claude-code"]      = (Color.FromArgb(255, 50, 30, 20),  Color.FromArgb(255, 220, 160, 100)),
        ["software-development"] = (Color.FromArgb(255, 25, 45, 50), Color.FromArgb(255, 100, 190, 210)),
        ["github"]           = (Color.FromArgb(255, 35, 35, 45),  Color.FromArgb(255, 170, 170, 220)),
        ["research"]         = (Color.FromArgb(255, 45, 25, 50),  Color.FromArgb(255, 180, 130, 220)),
        ["creative"]         = (Color.FromArgb(255, 50, 25, 40),  Color.FromArgb(255, 230, 130, 180)),
        ["productivity"]     = (Color.FromArgb(255, 20, 40, 50),  Color.FromArgb(255, 100, 180, 230)),
        ["mlops"]            = (Color.FromArgb(255, 40, 30, 50),  Color.FromArgb(255, 160, 140, 230)),
        ["media"]            = (Color.FromArgb(255, 50, 30, 30),  Color.FromArgb(255, 230, 140, 140)),
        ["gaming"]           = (Color.FromArgb(255, 20, 45, 20),  Color.FromArgb(255, 120, 220, 120)),
        ["social-media"]     = (Color.FromArgb(255, 30, 40, 50),  Color.FromArgb(255, 100, 160, 230)),
        ["devops"]           = (Color.FromArgb(255, 40, 40, 25),  Color.FromArgb(255, 200, 200, 100)),
        ["souls"]            = (Color.FromArgb(255, 50, 35, 15),  Color.FromArgb(255, 212, 160, 23)),
        ["data-science"]     = (Color.FromArgb(255, 30, 35, 50),  Color.FromArgb(255, 130, 150, 230)),
        ["email"]            = (Color.FromArgb(255, 45, 30, 30),  Color.FromArgb(255, 210, 140, 140)),
        ["smart-home"]       = (Color.FromArgb(255, 25, 45, 30),  Color.FromArgb(255, 100, 210, 140)),
        ["note-taking"]      = (Color.FromArgb(255, 45, 40, 20),  Color.FromArgb(255, 210, 190, 100)),
        ["mcp"]              = (Color.FromArgb(255, 35, 35, 50),  Color.FromArgb(255, 160, 160, 230)),
        ["red-teaming"]      = (Color.FromArgb(255, 55, 20, 20),  Color.FromArgb(255, 240, 100, 100)),
        ["apple"]            = (Color.FromArgb(255, 35, 35, 45),  Color.FromArgb(255, 180, 180, 210)),
        ["autonomous-ai-agents"] = (Color.FromArgb(255, 40, 25, 50), Color.FromArgb(255, 180, 120, 230)),
    };

    public SkillsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        try { Refresh(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SkillsPage.OnLoaded crash: {ex}"); }
    }

    private void Refresh()
    {
        try
        {
            var skillManager = App.Services.GetRequiredService<SkillManager>();
            var skills = skillManager.ListSkills();

            _allSkills = skills.Select(s =>
            {
                var cat = DeriveCategory(s);
                var (bg, fg) = GetCategoryColors(cat);
                var toolList = (s.Tools ?? new List<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                return new SkillDisplayItem
                {
                    Name = s.Name ?? "(unnamed)",
                    Description = s.Description ?? "",
                    Category = cat,
                    SystemPrompt = s.SystemPrompt ?? "",
                    Tools = string.Join(", ", toolList),
                    ToolCount = toolList.Count,
                    ToolCountLabel = $"{toolList.Count} tool{(toolList.Count == 1 ? "" : "s")}",
                    Model = s.Model ?? "",
                    CategoryColor = new SolidColorBrush(bg),
                    CategoryForeground = new SolidColorBrush(fg),
                };
            }).OrderBy(s => s.Name).ToList();

            BuildCategoryChips();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SkillsPage.Refresh error: {ex}");
            _allSkills = new List<SkillDisplayItem>();
            SkillCountBadge.Text = "0 skills";
        }
    }

    private void BuildCategoryChips()
    {
        CategoryChips.Children.Clear();
        var categories = _allSkills
            .Select(s => s.Category).Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList();

        AddChip("All", _allSkills.Count, isActive: _activeCategory == "All");
        foreach (var cat in categories)
        {
            var count = _allSkills.Count(s => s.Category.Equals(cat, StringComparison.OrdinalIgnoreCase));
            AddChip(cat, count, isActive: _activeCategory.Equals(cat, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void AddChip(string label, int count, bool isActive)
    {
        var (_, fg) = GetCategoryColors(label);
        var btn = new Button
        {
            Tag = label, Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(12),
            MinWidth = 0, FontSize = 11, Content = $"{label} ({count})",
            Background = isActive ? new SolidColorBrush(fg) : new SolidColorBrush(Colors.Transparent),
            Foreground = isActive
                ? new SolidColorBrush(Color.FromArgb(255, 16, 17, 20))
                : new SolidColorBrush(Color.FromArgb(255, 160, 170, 185)),
            BorderBrush = isActive
                ? new SolidColorBrush(Colors.Transparent)
                : new SolidColorBrush(Color.FromArgb(255, 50, 58, 70)),
            BorderThickness = new Thickness(1),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, $"Filter by {label}, {count} skills");
        btn.Click += CategoryChip_Click;
        CategoryChips.Children.Add(btn);
    }

    private void CategoryChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag)
        { _activeCategory = tag; BuildCategoryChips(); ApplyFilter(); }
    }

    private void ApplyFilter()
    {
        var query = SearchBox?.Text?.Trim() ?? "";
        IEnumerable<SkillDisplayItem> filtered = _allSkills;

        if (!_activeCategory.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(s => s.Category.Equals(_activeCategory, StringComparison.OrdinalIgnoreCase));
            ListHeader.Text = _activeCategory;
        }
        else { ListHeader.Text = "All Skills"; }

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(s =>
                (s.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.Category?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (s.Tools?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var sortTag = (SortSelector?.SelectedItem as ComboBoxItem)?.Tag as string ?? "name_asc";
        var list = sortTag switch
        {
            "name_desc" => filtered.OrderByDescending(s => s.Name).ToList(),
            "category" => filtered.OrderBy(s => s.Category).ThenBy(s => s.Name).ToList(),
            _ => filtered.OrderBy(s => s.Name).ToList(),
        };

        SkillsList.ItemsSource = list;
        SkillCountBadge.Text = $"{_allSkills.Count} skill{(_allSkills.Count == 1 ? "" : "s")}";
        FilteredCountText.Text = list.Count == _allSkills.Count ? "" : $"{list.Count} shown";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (_loaded) ApplyFilter(); }
    private void SortSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_loaded) ApplyFilter(); }

    private void SkillsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SkillsList.SelectedItem is not SkillDisplayItem item) return;
        try
        {
            PreviewTitle.Text = item.Name;
            PreviewDescription.Text = item.Description;
            PreviewContent.Text = item.SystemPrompt;
            var (bg, fg) = GetCategoryColors(item.Category);
            PreviewCategoryBadge.Background = new SolidColorBrush(bg);
            PreviewCategoryText.Text = item.Category;
            PreviewCategoryText.Foreground = new SolidColorBrush(fg);
            PreviewToolsText.Text = item.ToolCount > 0 ? $"Tools: {item.Tools}" : "No tools";
            PreviewModelText.Text = string.IsNullOrEmpty(item.Model) ? "Default model" : $"Model: {item.Model}";
            PreviewMeta.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SkillsPage preview error: {ex}"); }
    }

    private static string DeriveCategory(Skill skill)
    {
        try
        {
            if (!string.IsNullOrEmpty(skill.FilePath))
            {
                var dir = Path.GetDirectoryName(skill.FilePath);
                var parts = new List<string>();
                while (dir is not null)
                {
                    var name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name) || name.Equals("skills", StringComparison.OrdinalIgnoreCase)) break;
                    parts.Add(name);
                    dir = Path.GetDirectoryName(dir);
                }
                if (parts.Count > 0) return parts[^1];
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"DeriveCategory path parsing failed: {ex.Message}"); }
        var tools = string.Join(" ", skill.Tools ?? new List<string>()).ToLower();
        if (tools.Contains("bash") || tools.Contains("terminal")) return "automation";
        if (tools.Contains("read_file") && tools.Contains("write_file")) return "code";
        if (tools.Contains("grep") || tools.Contains("glob")) return "analysis";
        return "general";
    }

    private static (Color Bg, Color Fg) GetCategoryColors(string category)
    {
        if (string.IsNullOrEmpty(category))
            return (Color.FromArgb(255, 40, 40, 40), Color.FromArgb(255, 170, 170, 170));
        if (category.Equals("All", StringComparison.OrdinalIgnoreCase))
            return (Color.FromArgb(255, 50, 45, 15), Color.FromArgb(255, 212, 160, 23));
        if (CategoryColors.TryGetValue(category, out var colors)) return colors;
        var hash = unchecked((uint)category.GetHashCode());
        var hue = (int)(hash % 360);
        return (HslToColor(hue, 0.25, 0.14), HslToColor(hue, 0.55, 0.65));
    }

    private static Color HslToColor(int h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = l - c / 2;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromArgb(255,
            (byte)Math.Clamp((r + m) * 255, 0, 255),
            (byte)Math.Clamp((g + m) * 255, 0, 255),
            (byte)Math.Clamp((b + m) * 255, 0, 255));
    }
}

public sealed class SkillDisplayItem
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public string Tools { get; set; } = "";
    public int ToolCount { get; set; }
    public string ToolCountLabel { get; set; } = "";
    public string Model { get; set; } = "";
    public SolidColorBrush CategoryColor { get; set; } = new(Colors.Gray);
    public SolidColorBrush CategoryForeground { get; set; } = new(Colors.White);
}
