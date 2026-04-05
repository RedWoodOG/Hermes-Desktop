using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Hermes.Agent.Soul;

namespace HermesDesktop.Views;

public sealed partial class AgentPage : Page
{
    private readonly SoulService _soulService = App.Services.GetRequiredService<SoulService>();
    private readonly SoulRegistry _soulRegistry = App.Services.GetRequiredService<SoulRegistry>();
    private readonly AgentProfileManager _profileManager = App.Services.GetRequiredService<AgentProfileManager>();

    private string _activeTab = "agents";
    private SoulTemplate? _selectedSoul;

    public AgentPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        // Update active label
        var activeName = _profileManager.GetActiveProfileName();
        ActiveSoulLabel.Text = activeName ?? "Default";

        if (_activeTab == "identity") _ = RefreshIdentityAsync();
        else if (_activeTab == "souls") RefreshSouls();
        else if (_activeTab == "agents") RefreshAgents();
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
        var inactiveBorder = Application.Current.Resources["AppSubtleStrokeBrush"] as Brush;
        var activeBorder = Application.Current.Resources["AppAccentDarkBrush"] as Brush;

        foreach (var (tab, isActive) in new[] { (TabIdentity, tag == "identity"), (TabSouls, tag == "souls"), (TabAgents, tag == "agents") })
        {
            tab.Background = isActive ? activeBg : inactiveBg;
            tab.Foreground = isActive ? activeFg : inactiveFg;
            tab.BorderBrush = isActive ? activeBorder : inactiveBorder;
        }

        IdentityContent.Visibility = tag == "identity" ? Visibility.Visible : Visibility.Collapsed;
        SoulsContent.Visibility = tag == "souls" ? Visibility.Visible : Visibility.Collapsed;
        AgentsContent.Visibility = tag == "agents" ? Visibility.Visible : Visibility.Collapsed;

        Refresh();
    }

    // ── Identity Tab ──

    private async System.Threading.Tasks.Task RefreshIdentityAsync()
    {
        try
        {
            SoulEditor.Text = await _soulService.LoadFileAsync(SoulFileType.Soul);
            UserEditor.Text = await _soulService.LoadFileAsync(SoulFileType.User);

            var mistakes = await _soulService.LoadMistakesAsync();
            var mistakeItems = mistakes.TakeLast(15).Reverse().Select(m => $"\u2022 {m.Lesson}").ToList();
            MistakesList.ItemsSource = mistakeItems;
            NoMistakes.Visibility = mistakeItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MistakesList.Visibility = mistakeItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            var habits = await _soulService.LoadHabitsAsync();
            var habitItems = habits.TakeLast(15).Reverse().Select(h => $"\u2022 {h.Habit}").ToList();
            HabitsList.ItemsSource = habitItems;
            NoHabits.Visibility = habitItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HabitsList.Visibility = habitItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AgentPage identity error: {ex.Message}");
        }
    }

    private async void SaveSoul_Click(object sender, RoutedEventArgs e)
    {
        await _soulService.SaveFileAsync(SoulFileType.Soul, SoulEditor.Text);
    }

    private async void SaveUser_Click(object sender, RoutedEventArgs e)
    {
        await _soulService.SaveFileAsync(SoulFileType.User, UserEditor.Text);
    }

    private async void ResetSoul_Click(object sender, RoutedEventArgs e)
    {
        var defaultSoul = _soulRegistry.GetSoul("Hermes Default");
        if (defaultSoul is not null)
        {
            await _soulService.SaveFileAsync(SoulFileType.Soul, defaultSoul.Content);
            SoulEditor.Text = defaultSoul.Content;
        }
    }

    // ── Souls Browser Tab ──

    private void RefreshSouls()
    {
        var souls = _soulRegistry.ListSouls();
        SoulsList.ItemsSource = souls;
    }

    private void SoulsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SoulsList.SelectedItem is SoulTemplate soul)
        {
            _selectedSoul = soul;
            SoulPreviewTitle.Text = soul.Name;
            SoulPreviewText.Text = soul.Content;
            ApplySoulBtn.IsEnabled = true;
            CustomizeSoulBtn.IsEnabled = true;
        }
    }

    private async void ApplySoul_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSoul is null) return;
        await _soulService.SaveFileAsync(SoulFileType.Soul, _selectedSoul.Content);
        ActiveSoulLabel.Text = _selectedSoul.Name;

        // Switch to identity tab
        _activeTab = "identity";
        Tab_Click(TabIdentity, new RoutedEventArgs());
    }

    private void CustomizeSoul_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSoul is null) return;
        SoulEditor.Text = _selectedSoul.Content;
        _activeTab = "identity";
        Tab_Click(TabIdentity, new RoutedEventArgs());
    }

    // ── Agents Tab ──

    private void RefreshAgents()
    {
        var profiles = _profileManager.ListProfiles();
        AgentsList.ItemsSource = profiles.Select(p => new AgentDisplayItem
        {
            Name = p.Name,
            Description = p.Description,
            IsActive = p.IsActive
        }).ToList();
        NoAgentsText.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void NewAgent_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "New Agent",
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };

        var panel = new StackPanel { Spacing = 10 };
        var nameBox = new TextBox { PlaceholderText = "Agent name (e.g. Code Reviewer)",
            Background = Application.Current.Resources["AppInsetBrush"] as Brush };
        var descBox = new TextBox { PlaceholderText = "Short description",
            Background = Application.Current.Resources["AppInsetBrush"] as Brush };
        panel.Children.Add(new TextBlock { Text = "Name", FontSize = 13 });
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock { Text = "Description", FontSize = 13 });
        panel.Children.Add(descBox);
        panel.Children.Add(new TextBlock { Text = "The current soul will be saved with this agent.",
            FontSize = 11, Opacity = 0.6 });
        dialog.Content = panel;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(nameBox.Text))
        {
            var currentSoul = await _soulService.LoadFileAsync(SoulFileType.Soul);
            var profile = new AgentProfile
            {
                Name = nameBox.Text,
                Description = descBox.Text ?? "",
                SoulContent = currentSoul,
                IsActive = false
            };
            await _profileManager.SaveProfileAsync(profile);
            RefreshAgents();
        }
    }

    private async void ActivateAgent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string name) return;
        var profiles = _profileManager.ListProfiles();
        var profile = profiles.FirstOrDefault(p => p.Name == name);
        if (profile is not null)
        {
            await _profileManager.ActivateProfileAsync(profile);
            ActiveSoulLabel.Text = profile.Name;
            RefreshAgents();
        }
    }

    private void DeleteAgent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string name) return;
        _profileManager.DeleteProfile(name);
        RefreshAgents();
    }
}

// Shared display model
public sealed class AgentDisplayItem
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsActive { get; set; }
    public Visibility ActiveVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
}
