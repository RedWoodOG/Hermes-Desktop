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
        var provider = HermesEnvironment.ModelProvider.ToLowerInvariant();

        // Map legacy "custom" and "local" to "ollama"
        if (provider is "custom" or "local") provider = "ollama";

        var matchIndex = 7; // default to ollama (last item)
        for (int i = 0; i < ProviderCombo.Items.Count; i++)
        {
            if (ProviderCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), provider, StringComparison.OrdinalIgnoreCase))
            {
                matchIndex = i;
                break;
            }
        }

        ProviderCombo.SelectedIndex = matchIndex;

        BaseUrlBox.Text = HermesEnvironment.ModelBaseUrl;
        ModelBox.Text = HermesEnvironment.DefaultModel;
        ApiKeyBox.Password = HermesEnvironment.ModelApiKey ?? "";

        PopulateModelCombo(provider);
        SelectCurrentModel(HermesEnvironment.DefaultModel);
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var providerTag = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ollama";
        PopulateModelCombo(providerTag);

        if (ModelCatalog.ProviderBaseUrls.TryGetValue(providerTag, out var defaultUrl))
        {
            BaseUrlBox.Text = defaultUrl;
        }
    }

    private void PopulateModelCombo(string provider)
    {
        if (provider is "ollama" or "local" or "custom")
        {
            PopulateOllamaModelsAsync();
            return;
        }

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

        if (models.Count > 0)
            ContextLengthLabel.Text = $"Context: {ModelCatalog.FormatContextLength(models[0].ContextLength)}";
        else
            ContextLengthLabel.Text = "Context: --";
    }

    private async void PopulateOllamaModelsAsync()
    {
        _suppressModelComboEvent = true;
        ModelCombo.Items.Clear();
        ModelCombo.Items.Add(new ComboBoxItem { Content = "Scanning Ollama...", Tag = "", IsEnabled = false });
        ModelCombo.SelectedIndex = 0;
        ContextLengthLabel.Text = "Fetching models from Ollama...";
        _suppressModelComboEvent = false;

        var baseUrl = BaseUrlBox.Text?.Trim() ?? "http://127.0.0.1:11434/v1";
        // Strip /v1 suffix to get the Ollama API root
        var ollamaRoot = baseUrl.Replace("/v1", "").TrimEnd('/');

        var models = await ModelCatalog.FetchOllamaModelsAsync(ollamaRoot);

        _suppressModelComboEvent = true;
        ModelCombo.Items.Clear();

        if (models.Count == 0)
        {
            ModelCombo.Items.Add(new ComboBoxItem
            {
                Content = "No models found (is Ollama running?)",
                Tag = "",
                IsEnabled = false
            });
            ContextLengthLabel.Text = "Context: -- (Ollama not reachable)";
        }
        else
        {
            foreach (var m in models)
            {
                ModelCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{m.DisplayName}  ({ModelCatalog.FormatContextLength(m.ContextLength)})",
                    Tag = m.Id
                });
            }
            ContextLengthLabel.Text = $"Context: {ModelCatalog.FormatContextLength(models[0].ContextLength)}";
        }

        if (ModelCombo.Items.Count > 0)
            ModelCombo.SelectedIndex = 0;

        _suppressModelComboEvent = false;

        // Try to select the currently configured model
        SelectCurrentModel(HermesEnvironment.DefaultModel);
    }

    private void SelectCurrentModel(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return;

        // Exact match first
        for (int i = 0; i < ModelCombo.Items.Count; i++)
        {
            if (ModelCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), modelId, StringComparison.OrdinalIgnoreCase))
            {
                _suppressModelComboEvent = true;
                ModelCombo.SelectedIndex = i;
                _suppressModelComboEvent = false;
                UpdateContextLabel(item.Tag?.ToString() ?? modelId);
                return;
            }
        }

        // Partial/contains match (e.g. "glm-5:cloud" matches Ollama's "glm-5:cloud")
        for (int i = 0; i < ModelCombo.Items.Count; i++)
        {
            if (ModelCombo.Items[i] is ComboBoxItem item)
            {
                var tag = item.Tag?.ToString() ?? "";
                if (!string.IsNullOrEmpty(tag) &&
                    (tag.Contains(modelId, StringComparison.OrdinalIgnoreCase) ||
                     modelId.Contains(tag, StringComparison.OrdinalIgnoreCase)))
                {
                    _suppressModelComboEvent = true;
                    ModelCombo.SelectedIndex = i;
                    _suppressModelComboEvent = false;
                    ModelBox.Text = tag; // Update to the actual Ollama model name
                    UpdateContextLabel(tag);
                    return;
                }
            }
        }

        // No match found — keep the custom model text as-is
        ModelBox.Text = modelId;
    }

    private void UpdateContextLabel(string modelId)
    {
        // Check catalog first, then check Ollama-fetched models
        var ctx = ModelCatalog.GetContextLength(modelId);
        ContextLengthLabel.Text = $"Context: {ModelCatalog.FormatContextLength(ctx)}";
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
            var providerTag = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ollama";
            // Save as "local" in config.yaml for backward compat with OpenAI-compat endpoint
            if (providerTag == "ollama") providerTag = "local";
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
