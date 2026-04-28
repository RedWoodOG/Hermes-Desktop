using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Hermes.Agent.LLM;
using Hermes.Agent.Soul;

using HermesDesktop.Views;

namespace HermesDesktop.Views.Panels;

public sealed partial class AgentPanel : UserControl
{
    private SoulService? _soulService;
    private SoulRegistry? _soulRegistry;
    private AgentProfileManager? _profileManager;
    private ChatClientFactory? _chatClientFactory;
    private string _activeTab = "identity";
    private SoulTemplate? _selectedSoul;

    public AgentPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _soulService = App.Services?.GetService<SoulService>();
            _soulRegistry = App.Services?.GetService<SoulRegistry>();
            _profileManager = App.Services?.GetService<AgentProfileManager>();
            _chatClientFactory = App.Services?.GetService<ChatClientFactory>();
            Refresh();
        };
    }

    public void Refresh()
    {
        if (_activeTab == "identity") _ = RefreshIdentityAsync();
        else if (_activeTab == "souls") RefreshSouls();
        else if (_activeTab == "agents") RefreshAgents();
    }

    // ── Tab switching ──

    private void SubTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _activeTab = tag;
            var accent = Application.Current.Resources["AppAccentTextBrush"] as Brush;
            var secondary = Application.Current.Resources["AppTextSecondaryBrush"] as Brush;
            TabIdentity.Foreground = tag == "identity" ? accent : secondary;
            TabSouls.Foreground = tag == "souls" ? accent : secondary;
            TabAgents.Foreground = tag == "agents" ? accent : secondary;

            IdentityContent.Visibility = tag == "identity" ? Visibility.Visible : Visibility.Collapsed;
            SoulsContent.Visibility = tag == "souls" ? Visibility.Visible : Visibility.Collapsed;
            AgentsContent.Visibility = tag == "agents" ? Visibility.Visible : Visibility.Collapsed;

            Refresh();
        }
    }

    // ── Identity Tab ──

    private async System.Threading.Tasks.Task RefreshIdentityAsync()
    {
        if (_soulService is null) return;
        try
        {
            SoulEditor.Text = await _soulService.LoadFileAsync(SoulFileType.Soul);
            UserEditor.Text = await _soulService.LoadFileAsync(SoulFileType.User);

            ActiveSoulLabel.Text = await _soulService.GetRuntimeSoulNameAsync(_soulRegistry, _profileManager);

            // Mistakes
            var mistakes = await _soulService.LoadMistakesAsync();
            MistakesList.ItemsSource = mistakes.TakeLast(15).Reverse()
                .Select(m => $"• {m.Lesson}").ToList();

            // Habits
            var habits = await _soulService.LoadHabitsAsync();
            HabitsList.ItemsSource = habits.TakeLast(15).Reverse()
                .Select(h => $"• {h.Habit}").ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AgentPanel identity error: {ex.Message}");
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

    private async void ResetSoul_Click(object sender, RoutedEventArgs e)
    {
        if (_soulService is null) return;
        // Apply the default soul template
        var defaultSoul = _soulRegistry?.GetSoul("Default");
        if (defaultSoul is not null)
        {
            await _soulService.SaveFileAsync(SoulFileType.Soul, defaultSoul.Content);
            SoulEditor.Text = defaultSoul.Content;
            ActiveSoulLabel.Text = await _soulService.GetRuntimeSoulNameAsync(_soulRegistry, _profileManager);
        }
    }

    // ── Souls Browser Tab ──

    private void RefreshSouls()
    {
        if (_soulRegistry is null) return;
        var souls = _soulRegistry.ListSouls();
        SoulsList.ItemsSource = souls;
    }

    private void SoulsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SoulsList.SelectedItem is SoulTemplate soul)
        {
            _selectedSoul = soul;
            SoulPreviewText.Text = soul.Content.Length > 800
                ? soul.Content[..800] + "\n...(truncated)"
                : soul.Content;
            SoulPreviewBorder.Visibility = Visibility.Visible;
            ApplySoulBtn.IsEnabled = true;
            CustomizeSoulBtn.IsEnabled = true;
        }
    }

    private async void ApplySoul_Click(object sender, RoutedEventArgs e)
    {
        if (_soulService is null || _selectedSoul is null) return;
        await _soulService.SaveFileAsync(SoulFileType.Soul, _selectedSoul.Content);
        ActiveSoulLabel.Text = await _soulService.GetRuntimeSoulNameAsync(_soulRegistry, _profileManager);

        // Switch to identity tab to show the applied soul
        _activeTab = "identity";
        SubTab_Click(TabIdentity, new RoutedEventArgs());
    }

    private void CustomizeSoul_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSoul is null) return;
        // Switch to identity tab with the soul content loaded for editing
        SoulEditor.Text = _selectedSoul.Content;
        _activeTab = "identity";
        SubTab_Click(TabIdentity, new RoutedEventArgs());
    }

    // ── Agents Tab ──

    private void RefreshAgents()
    {
        if (_profileManager is null) return;
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
        if (_profileManager is null || _soulService is null) return;

        var dialog = new ContentDialog
        {
            Title = "New Agent",
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };

        var panel = new StackPanel { Spacing = 8 };
        var nameBox = new TextBox { PlaceholderText = "Agent name", Background = Application.Current.Resources["AppInsetBrush"] as Brush };
        var descBox = new TextBox { PlaceholderText = "Short description", Background = Application.Current.Resources["AppInsetBrush"] as Brush };
        panel.Children.Add(new TextBlock { Text = "Name", FontSize = 12 });
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock { Text = "Description", FontSize = 12 });
        panel.Children.Add(descBox);
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
                PreferredProvider = _chatClientFactory?.CurrentProvider,
                PreferredModel = _chatClientFactory?.CurrentModel,
                IsActive = false
            };
            await _profileManager.SaveProfileAsync(profile);
            RefreshAgents();
        }
    }

    private async void ActivateAgent_Click(object sender, RoutedEventArgs e)
    {
        if (_profileManager is null || sender is not Button btn || btn.Tag is not string name) return;
        var profiles = _profileManager.ListProfiles();
        var profile = profiles.FirstOrDefault(p => p.Name == name);
        if (profile is not null)
        {
            await _profileManager.ActivateProfileAsync(profile);
            if (_soulService is not null)
                ActiveSoulLabel.Text = await _soulService.GetRuntimeSoulNameAsync(_soulRegistry, _profileManager);
            RefreshAgents();
        }
    }

    private void DeleteAgent_Click(object sender, RoutedEventArgs e)
    {
        if (_profileManager is null || sender is not Button btn || btn.Tag is not string name) return;
        _profileManager.DeleteProfile(name);
        RefreshAgents();
    }
}
