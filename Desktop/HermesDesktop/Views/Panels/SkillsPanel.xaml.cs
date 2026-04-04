using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Hermes.Agent.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace HermesDesktop.Views.Panels;

public sealed class SkillListItem
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed partial class SkillsPanel : UserControl
{
    private readonly SkillManager _skillManager;
    private readonly ObservableCollection<SkillListItem> _allSkills = new();

    public SkillsPanel()
    {
        InitializeComponent();
        _skillManager = App.Services.GetRequiredService<SkillManager>();
        Loaded += async (_, _) => await RefreshAsync();
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        _allSkills.Clear();
        var skills = _skillManager.ListSkills();
        foreach (var skill in skills)
        {
            _allSkills.Add(new SkillListItem
            {
                Name = skill.Name,
                Description = skill.Description ?? "",
                Content = skill.SystemPrompt ?? ""
            });
        }
        SkillsList.ItemsSource = _allSkills;
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.ToLowerInvariant();
        SkillsList.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _allSkills
            : new ObservableCollection<SkillListItem>(
                _allSkills.Where(s => s.Name.Contains(query, System.StringComparison.OrdinalIgnoreCase) ||
                                      s.Description.Contains(query, System.StringComparison.OrdinalIgnoreCase)));
    }

    private void SkillsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SkillsList.SelectedItem is SkillListItem item)
        {
            PreviewText.Text = item.Content;
            PreviewBorder.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        }
    }
}
