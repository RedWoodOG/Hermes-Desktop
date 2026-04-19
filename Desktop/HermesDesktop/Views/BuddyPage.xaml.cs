using System;
using System.Threading;
using Hermes.Agent.Buddy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HermesDesktop.Views;

public sealed partial class BuddyPage : Page
{
    private BuddyService? _buddyService;
    private string _buddyUserId = "";

    public BuddyPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _buddyService = App.Services.GetRequiredService<BuddyService>();
        _buddyUserId = ResolveBuddyUserId(_buddyService);
        PopulateSpeciesCombo();
        _ = InitializeLayoutAsync();
    }

    private static string ResolveBuddyUserId(BuddyService buddyService)
    {
        var stored = buddyService.TryReadStoredUserId();
        if (!string.IsNullOrWhiteSpace(stored))
            return stored.Trim();

        var win = Environment.UserName?.Trim();
        return string.IsNullOrEmpty(win) ? "default" : win;
    }

    private void PopulateSpeciesCombo()
    {
        SpeciesCombo.Items.Clear();
        SpeciesCombo.Items.Add(new ComboBoxItem { Content = "Surprise (full random roll)", Tag = (string?)null });
        AddSpeciesGroup("Common", BuddySpecies.Common);
        AddSpeciesGroup("Uncommon", BuddySpecies.Uncommon);
        AddSpeciesGroup("Rare", BuddySpecies.Rare);
        AddSpeciesGroup("Legendary", BuddySpecies.Legendary);
        SpeciesCombo.SelectedIndex = 0;
    }

    private void AddSpeciesGroup(string tier, string[] species)
    {
        foreach (var s in species)
        {
            SpeciesCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{s} ({tier})",
                Tag = s
            });
        }
    }

    private async System.Threading.Tasks.Task InitializeLayoutAsync()
    {
        if (_buddyService is null)
            return;

        if (_buddyService.HasSavedBuddy)
        {
            SetupPanel.Visibility = Visibility.Collapsed;
            MainBuddyGrid.Visibility = Visibility.Visible;
            await LoadBuddyDisplayAsync();
        }
        else
        {
            SetupPanel.Visibility = Visibility.Visible;
            MainBuddyGrid.Visibility = Visibility.Collapsed;
            SpeciesCombo.SelectionChanged += SpeciesCombo_SelectionChanged;
            UpdateSpeciesPreview();
        }
    }

    private void SpeciesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSpeciesPreview();

    private void UpdateSpeciesPreview()
    {
        if (_buddyService is null || MainBuddyGrid.Visibility == Visibility.Visible)
            return;

        var chosen = GetSelectedSpeciesKey();
        var preview = chosen is null
            ? new BuddyGenerator(_buddyUserId).Generate()
            : BuddyService.PreviewBuddy(_buddyUserId, chosen);
        SetupStatusText.Text =
            $"Preview: {preview.Species} · {preview.Rarity} · INT {preview.Stats.Intelligence} ENR {preview.Stats.Energy} " +
            $"CRE {preview.Stats.Creativity} FRN {preview.Stats.Friendliness}" +
            (preview.IsShiny ? " · ✨ shiny roll" : "");
    }

    private string? GetSelectedSpeciesKey()
    {
        if (SpeciesCombo.SelectedItem is not ComboBoxItem item)
            return null;
        return item.Tag as string;
    }

    private async void HatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_buddyService is null)
            return;

        HatchButton.IsEnabled = false;
        SetupStatusText.Text = "Hatching…";
        try
        {
            var chosen = GetSelectedSpeciesKey();
            await _buddyService.GetBuddyAsync(_buddyUserId, chosen, CancellationToken.None);
            SpeciesCombo.SelectionChanged -= SpeciesCombo_SelectionChanged;
            SetupPanel.Visibility = Visibility.Collapsed;
            MainBuddyGrid.Visibility = Visibility.Visible;
            await LoadBuddyDisplayAsync();
        }
        catch (Exception ex)
        {
            SetupStatusText.Text = $"Could not hatch: {ex.Message}";
        }
        finally
        {
            HatchButton.IsEnabled = true;
        }
    }

    private async System.Threading.Tasks.Task LoadBuddyDisplayAsync()
    {
        if (_buddyService is null)
            return;

        try
        {
            var buddy = await _buddyService.GetBuddyAsync(_buddyUserId, CancellationToken.None);
            ApplyBuddyToUi(buddy);
            BuddyActionStatus.Text = "";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BuddyPage load error: {ex.Message}");
            AsciiArt.Text = "Could not load buddy.";
            BuddyName.Text = "Error";
            BuddyDetails.Text = $"Error: {ex.Message}";
        }
    }

    private void ApplyBuddyToUi(Buddy buddy)
    {
        AsciiArt.Text = BuddyRenderer.RenderAscii(buddy);
        BuddyName.Text = buddy.Name ?? "Unnamed";
        BuddySpecies.Text = buddy.Species;
        RarityText.Text = buddy.Rarity.ToUpperInvariant();

        RarityBadge.Background = buddy.Rarity switch
        {
            "legendary" => new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255, 200, 160, 50)),
            "rare" => new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255, 100, 140, 200)),
            "uncommon" => new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255, 100, 180, 100)),
            _ => new SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255, 58, 58, 0))
        };

        if (buddy.IsShiny)
        {
            ShinyBadge.Text = "SHINY";
            ShinyBadge.Visibility = Visibility.Visible;
        }
        else
        {
            ShinyBadge.Visibility = Visibility.Collapsed;
        }

        StatInt.Value = buddy.Stats.Intelligence;
        StatIntVal.Text = buddy.Stats.Intelligence.ToString();
        StatEnr.Value = buddy.Stats.Energy;
        StatEnrVal.Text = buddy.Stats.Energy.ToString();
        StatCre.Value = buddy.Stats.Creativity;
        StatCreVal.Text = buddy.Stats.Creativity.ToString();
        StatFrn.Value = buddy.Stats.Friendliness;
        StatFrnVal.Text = buddy.Stats.Friendliness.ToString();

        BuddyPersonality.Text = buddy.Personality ?? "";

        BuddyDetails.Text = $"Eyes: {buddy.Eyes}\n"
                           + $"Hat: {(string.IsNullOrEmpty(buddy.Hat) ? "none" : buddy.Hat)}\n"
                           + $"Total Stats: {buddy.Stats.Total}\n"
                           + $"Hatched: {buddy.HatchedAt:yyyy-MM-dd}\n"
                           + $"Identity key: {_buddyUserId}";
    }

    private async void RefreshSoulButton_Click(object sender, RoutedEventArgs e)
    {
        if (_buddyService is null)
            return;

        RefreshSoulButton.IsEnabled = false;
        BuddyActionStatus.Text = "Refreshing personality…";
        try
        {
            var buddy = await _buddyService.RefreshSoulAsync(_buddyUserId, CancellationToken.None);
            ApplyBuddyToUi(buddy);
            BuddyActionStatus.Text = "Updated.";
        }
        catch (Exception ex)
        {
            BuddyActionStatus.Text = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            RefreshSoulButton.IsEnabled = true;
        }
    }

    private async void ResetBuddyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_buddyService is null)
            return;

        var dialog = new ContentDialog
        {
            Title = "Reset buddy?",
            Content = "This deletes your saved companion and returns you to species selection. This cannot be undone.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        ResetBuddyButton.IsEnabled = false;
        SetupStatusText.Text = "Clearing saved buddy…";
        try
        {
            _buddyService.ClearSavedBuddy();
            MainBuddyGrid.Visibility = Visibility.Collapsed;
            SetupPanel.Visibility = Visibility.Visible;
            SpeciesCombo.SelectionChanged -= SpeciesCombo_SelectionChanged;
            SpeciesCombo.SelectionChanged += SpeciesCombo_SelectionChanged;
            SpeciesCombo.SelectedIndex = 0;
            UpdateSpeciesPreview();
            SetupStatusText.Text = "Pick a species and hatch again when you are ready.";
        }
        catch (Exception ex)
        {
            SetupStatusText.Text = $"Reset failed: {ex.Message}";
        }
        finally
        {
            ResetBuddyButton.IsEnabled = true;
        }
    }
}
