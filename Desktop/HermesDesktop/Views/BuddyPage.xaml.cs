using System;
using System.Linq;
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
    private Buddy? _currentBuddy;
    private string _buddyUserId = "";
    private bool _isApplyingBuddy;

    public BuddyPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _buddyService = App.Services.GetRequiredService<BuddyService>();
        _buddyUserId = ResolveBuddyUserId(_buddyService);
        PopulateCraftCombos();
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

    private void PopulateCraftCombos()
    {
        SpeciesCombo.Items.Clear();
        PaletteCombo.Items.Clear();
        EyesCombo.Items.Clear();
        AccessoryCombo.Items.Clear();
        EditPaletteCombo.Items.Clear();
        EditEyesCombo.Items.Clear();
        EditAccessoryCombo.Items.Clear();

        SpeciesCombo.Items.Add(new ComboBoxItem { Content = "Surprise (full random roll)", Tag = (string?)null });
        AddSpeciesGroup("Common", global::Hermes.Agent.Buddy.BuddySpecies.Common);
        AddSpeciesGroup("Uncommon", global::Hermes.Agent.Buddy.BuddySpecies.Uncommon);
        AddSpeciesGroup("Rare", global::Hermes.Agent.Buddy.BuddySpecies.Rare);
        AddSpeciesGroup("Legendary", global::Hermes.Agent.Buddy.BuddySpecies.Legendary);
        SpeciesCombo.SelectedIndex = 0;

        AddChoice(PaletteCombo, "Hermes gold", BuddyPalettes.Gold);
        AddChoice(PaletteCombo, "Tide blue", BuddyPalettes.Tide);
        AddChoice(PaletteCombo, "Moss green", BuddyPalettes.Moss);
        AddChoice(PaletteCombo, "Ember coral", BuddyPalettes.Ember);
        AddChoice(PaletteCombo, "Violet tech", BuddyPalettes.Violet);
        AddChoice(PaletteCombo, "Mono steel", BuddyPalettes.Mono);
        PaletteCombo.SelectedIndex = 0;

        AddChoice(EyesCombo, "Normal", "normal");
        AddChoice(EyesCombo, "Wide", "wide");
        AddChoice(EyesCombo, "Sleepy", "sleepy");
        AddChoice(EyesCombo, "Excited", "excited");
        AddChoice(EyesCombo, "Curious", "curious");
        AddChoice(EyesCombo, "Determined", "determined");
        AddChoice(EyesCombo, "Sparkly", "sparkly");
        AddChoice(EyesCombo, "Tired", "tired");
        EyesCombo.SelectedIndex = 0;

        AddChoice(AccessoryCombo, "None", "");
        AddChoice(AccessoryCombo, "Cap", "cap");
        AddChoice(AccessoryCombo, "Beanie", "beanie");
        AddChoice(AccessoryCombo, "Bow", "bow");
        AddChoice(AccessoryCombo, "Crown", "crown");
        AddChoice(AccessoryCombo, "Wizard hat", "wizard");
        AddChoice(AccessoryCombo, "Halo", "halo");
        AddChoice(AccessoryCombo, "Headphones", "headphones");
        AccessoryCombo.SelectedIndex = 0;

        CloneComboChoices(PaletteCombo, EditPaletteCombo);
        CloneComboChoices(EyesCombo, EditEyesCombo);
        CloneComboChoices(AccessoryCombo, EditAccessoryCombo);
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

    private static void AddChoice(ComboBox combo, string label, string tag) =>
        combo.Items.Add(new ComboBoxItem { Content = label, Tag = tag });

    private static void CloneComboChoices(ComboBox source, ComboBox target)
    {
        target.Items.Clear();
        foreach (var sourceItem in source.Items.OfType<ComboBoxItem>())
        {
            target.Items.Add(new ComboBoxItem
            {
                Content = sourceItem.Content,
                Tag = sourceItem.Tag
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
            WireSetupPreviewHandlers();
            UpdateSpeciesPreview();
        }
    }

    private void WireSetupPreviewHandlers()
    {
        SpeciesCombo.SelectionChanged -= SpeciesCombo_SelectionChanged;
        PaletteCombo.SelectionChanged -= SpeciesCombo_SelectionChanged;
        EyesCombo.SelectionChanged -= SpeciesCombo_SelectionChanged;
        AccessoryCombo.SelectionChanged -= SpeciesCombo_SelectionChanged;

        SpeciesCombo.SelectionChanged += SpeciesCombo_SelectionChanged;
        PaletteCombo.SelectionChanged += SpeciesCombo_SelectionChanged;
        EyesCombo.SelectionChanged += SpeciesCombo_SelectionChanged;
        AccessoryCombo.SelectionChanged += SpeciesCombo_SelectionChanged;
    }

    private void UnwireSetupPreviewHandlers()
    {
        SpeciesCombo.SelectionChanged -= SpeciesCombo_SelectionChanged;
        PaletteCombo.SelectionChanged -= SpeciesCombo_SelectionChanged;
        EyesCombo.SelectionChanged -= SpeciesCombo_SelectionChanged;
        AccessoryCombo.SelectionChanged -= SpeciesCombo_SelectionChanged;
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
        preview = ApplySelectedSetupChoices(preview);

        SetupAvatar.SetBuddy(preview);
        SetupPreviewText.Text = $"{preview.Species} / {preview.Rarity.ToUpperInvariant()}";
        SetupStatusText.Text =
            $"INT {preview.Stats.Intelligence}  ENR {preview.Stats.Energy}  " +
            $"CRE {preview.Stats.Creativity}  FRN {preview.Stats.Friendliness}" +
            (preview.IsShiny ? "  SHINY roll" : "");
    }

    private string? GetSelectedSpeciesKey()
    {
        if (SpeciesCombo.SelectedItem is not ComboBoxItem item)
            return null;
        return item.Tag as string;
    }

    private string GetSelectedTag(ComboBox combo, string fallback = "")
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return tag;
        return fallback;
    }

    private Buddy ApplySelectedSetupChoices(Buddy buddy) =>
        CopyWithAvatar(
            buddy,
            GetSelectedTag(EyesCombo, buddy.Eyes),
            GetSelectedTag(AccessoryCombo, buddy.Hat),
            GetSelectedTag(PaletteCombo, buddy.Palette));

    private async void HatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_buddyService is null)
            return;

        HatchButton.IsEnabled = false;
        SetupStatusText.Text = "Hatching...";
        try
        {
            var chosen = GetSelectedSpeciesKey();
            await _buddyService.GetBuddyAsync(
                _buddyUserId,
                chosen,
                GetSelectedTag(EyesCombo),
                GetSelectedTag(AccessoryCombo),
                GetSelectedTag(PaletteCombo, BuddyPalettes.Gold),
                CancellationToken.None);
            UnwireSetupPreviewHandlers();
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
            BuddyName.Text = "Error";
            BuddyDetails.Text = $"Error: {ex.Message}";
        }
    }

    private void ApplyBuddyToUi(Buddy buddy)
    {
        _currentBuddy = buddy;
        MainAvatar.SetBuddy(buddy);
        BuddyName.Text = buddy.Name ?? "Unnamed";
        BuddySpecies.Text = buddy.Species;
        RarityText.Text = buddy.Rarity.ToUpperInvariant();

        RarityBadge.Background = buddy.Rarity switch
        {
            "legendary" => new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 200, 160, 50)),
            "rare" => new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 100, 140, 200)),
            "uncommon" => new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 100, 180, 100)),
            _ => new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 58, 58, 0))
        };

        ShinyBadge.Visibility = buddy.IsShiny ? Visibility.Visible : Visibility.Collapsed;

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
                           + $"Palette: {buddy.Palette}\n"
                           + $"Total Stats: {buddy.Stats.Total}\n"
                           + $"Hatched: {buddy.HatchedAt:yyyy-MM-dd}\n"
                           + $"Identity key: {_buddyUserId}";

        ApplyBuddyToCraftControls(buddy);
    }

    private void ApplyBuddyToCraftControls(Buddy buddy)
    {
        _isApplyingBuddy = true;
        SelectComboByTag(EditPaletteCombo, buddy.Palette);
        SelectComboByTag(EditEyesCombo, buddy.Eyes);
        SelectComboByTag(EditAccessoryCombo, buddy.Hat);
        _isApplyingBuddy = false;
    }

    private static void SelectComboByTag(ComboBox combo, string? tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag ?? "", StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
    }

    private void EditCraft_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingBuddy || _currentBuddy is null)
            return;

        var preview = CopyWithAvatar(
            _currentBuddy,
            GetSelectedTag(EditEyesCombo, _currentBuddy.Eyes),
            GetSelectedTag(EditAccessoryCombo, _currentBuddy.Hat),
            GetSelectedTag(EditPaletteCombo, _currentBuddy.Palette));
        MainAvatar.SetBuddy(preview);
        BuddyActionStatus.Text = "Previewing new look. Save to keep it.";
    }

    private async void SaveLookButton_Click(object sender, RoutedEventArgs e)
    {
        if (_buddyService is null)
            return;

        SaveLookButton.IsEnabled = false;
        BuddyActionStatus.Text = "Saving look...";
        try
        {
            var buddy = await _buddyService.UpdateAvatarAsync(
                _buddyUserId,
                GetSelectedTag(EditEyesCombo),
                GetSelectedTag(EditAccessoryCombo),
                GetSelectedTag(EditPaletteCombo, BuddyPalettes.Gold),
                CancellationToken.None);
            ApplyBuddyToUi(buddy);
            BuddyActionStatus.Text = "Look saved.";
        }
        catch (Exception ex)
        {
            BuddyActionStatus.Text = $"Save failed: {ex.Message}";
        }
        finally
        {
            SaveLookButton.IsEnabled = true;
        }
    }

    private static Buddy CopyWithAvatar(Buddy buddy, string eyes, string hat, string palette) =>
        new()
        {
            Species = buddy.Species,
            Rarity = buddy.Rarity,
            Eyes = eyes,
            Hat = hat,
            IsShiny = buddy.IsShiny,
            Stats = buddy.Stats,
            Palette = BuddyPalettes.Normalize(palette),
            Name = buddy.Name,
            Personality = buddy.Personality,
            HatchedAt = buddy.HatchedAt
        };

    private async void RefreshSoulButton_Click(object sender, RoutedEventArgs e)
    {
        if (_buddyService is null)
            return;

        RefreshSoulButton.IsEnabled = false;
        BuddyActionStatus.Text = "Refreshing personality...";
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
        SetupStatusText.Text = "Clearing saved buddy...";
        try
        {
            _buddyService.ClearSavedBuddy();
            _currentBuddy = null;
            MainBuddyGrid.Visibility = Visibility.Collapsed;
            SetupPanel.Visibility = Visibility.Visible;
            SpeciesCombo.SelectedIndex = 0;
            PaletteCombo.SelectedIndex = 0;
            EyesCombo.SelectedIndex = 0;
            AccessoryCombo.SelectedIndex = 0;
            WireSetupPreviewHandlers();
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
