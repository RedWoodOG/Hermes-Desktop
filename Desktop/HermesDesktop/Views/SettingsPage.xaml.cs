using System;
using System.Linq;
using Hermes.Agent.LLM;
using HermesDesktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;

namespace HermesDesktop.Views;

public sealed partial class SettingsPage : Page
{
    private static readonly ResourceLoader ResourceLoader = new();

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    public string HermesHomePath => HermesEnvironment.DisplayHermesHomePath;

    public string HermesConfigPath => HermesEnvironment.DisplayHermesConfigPath;

    public string HermesLogsPath => HermesEnvironment.DisplayHermesLogsPath;

    public string HermesWorkspacePath => HermesEnvironment.DisplayHermesWorkspacePath;

    public string TelegramStatus => HermesEnvironment.TelegramConfigured
        ? ResourceLoader.GetString("StatusDetected")
        : ResourceLoader.GetString("StatusNotDetected");

    public string DiscordStatus => HermesEnvironment.DiscordConfigured
        ? ResourceLoader.GetString("StatusDetected")
        : ResourceLoader.GetString("StatusNotDetected");

    private bool _suppressModelComboEvent;

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // Pre-populate fields from current config
        var provider = HermesEnvironment.ModelProvider.ToLowerInvariant();
        var matchIndex = 6; // default to "local"
        for (int i = 0; i < ProviderCombo.Items.Count; i++)
        {
            if (ProviderCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), provider, StringComparison.OrdinalIgnoreCase))
            {
                matchIndex = i;
                break;
            }
        }
        // Handle legacy "custom" tag mapping to "local"
        if (provider == "custom")
            matchIndex = 6;

        ProviderCombo.SelectedIndex = matchIndex;

        BaseUrlBox.Text = HermesEnvironment.ModelBaseUrl;
        ModelBox.Text = HermesEnvironment.DefaultModel;
        ApiKeyBox.Password = HermesEnvironment.ModelApiKey ?? "";

        PopulateModelCombo(provider);
        SelectCurrentModel(HermesEnvironment.DefaultModel);
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var providerTag = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "local";
        PopulateModelCombo(providerTag);

        // Auto-fill base URL for known providers
        if (ModelCatalog.ProviderBaseUrls.TryGetValue(providerTag, out var defaultUrl))
        {
            BaseUrlBox.Text = defaultUrl;
        }
    }

    private void PopulateModelCombo(string provider)
    {
        _suppressModelComboEvent = true;
        ModelCombo.Items.Clear();

        var models = ModelCatalog.GetModels(provider);
        foreach (var m in models)
        {
            ModelCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{m.DisplayName}  ({ModelCatalog.FormatContextLength(m.ContextLength)})",
                Tag = m.Id
            });
        }

        if (ModelCombo.Items.Count > 0)
            ModelCombo.SelectedIndex = 0;

        _suppressModelComboEvent = false;

        // Update context label for first item
        if (models.Count > 0)
            ContextLengthLabel.Text = $"Context: {ModelCatalog.FormatContextLength(models[0].ContextLength)}";
        else
            ContextLengthLabel.Text = "Context: --";
    }

    private void SelectCurrentModel(string modelId)
    {
        for (int i = 0; i < ModelCombo.Items.Count; i++)
        {
            if (ModelCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), modelId, StringComparison.OrdinalIgnoreCase))
            {
                _suppressModelComboEvent = true;
                ModelCombo.SelectedIndex = i;
                _suppressModelComboEvent = false;

                var ctx = ModelCatalog.GetContextLength(modelId);
                ContextLengthLabel.Text = $"Context: {ModelCatalog.FormatContextLength(ctx)}";
                return;
            }
        }
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelComboEvent) return;
        if (ModelCombo.SelectedItem is ComboBoxItem selected)
        {
            var modelId = selected.Tag?.ToString() ?? "";
            ModelBox.Text = modelId;
            var ctx = ModelCatalog.GetContextLength(modelId);
            ContextLengthLabel.Text = $"Context: {ModelCatalog.FormatContextLength(ctx)}";
        }
    }

    private async void SaveModelConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var providerTag = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "custom";
            var baseUrl = BaseUrlBox.Text.Trim();
            var model = ModelBox.Text.Trim();
            var apiKey = ApiKeyBox.Password.Trim();

            if (string.IsNullOrEmpty(model))
            {
                ModelSaveStatus.Text = "Model name is required.";
                ModelSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
                return;
            }

            await HermesEnvironment.SaveModelConfigAsync(providerTag, baseUrl, model, apiKey);
            ModelSaveStatus.Text = "Saved successfully. Restart to apply.";
            ModelSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOnlineBrush"];
        }
        catch (Exception ex)
        {
            ModelSaveStatus.Text = $"Error: {ex.Message}";
            ModelSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
        }
    }

    private void OpenHome_Click(object sender, RoutedEventArgs e)
    {
        HermesEnvironment.OpenHermesHome();
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        HermesEnvironment.OpenConfig();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        HermesEnvironment.OpenLogs();
    }

    private void OpenWorkspace_Click(object sender, RoutedEventArgs e)
    {
        HermesEnvironment.OpenWorkspace();
    }
}
