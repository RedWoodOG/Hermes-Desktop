using System;
using System.Collections.Generic;
using System.Globalization;
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

    // ── Bindable path properties ──
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

    // ═══════════════════════════════════════════
    //  Page Loaded — populate all sections
    // ═══════════════════════════════════════════
    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        LoadModelSettings();
        LoadAgentSettings();
        LoadGatewayStatus();
        LoadPlatformSettings();
        LoadMemorySettings();
        LoadDisplaySettings();
    }

    // ── Model ──
    private void LoadModelSettings()
    {
        var provider = HermesEnvironment.ModelProvider.ToLowerInvariant();
        var matchIndex = ProviderCombo.Items.Count - 1; // default to last (local)
        for (int i = 0; i < ProviderCombo.Items.Count; i++)
        {
            if (ProviderCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), provider, StringComparison.OrdinalIgnoreCase))
            {
                matchIndex = i;
                break;
            }
        }
        if (provider == "custom")
            matchIndex = ProviderCombo.Items.Count - 1;

        ProviderCombo.SelectedIndex = matchIndex;
        BaseUrlBox.Text = HermesEnvironment.ModelBaseUrl;
        ModelBox.Text = HermesEnvironment.DefaultModel;
        ApiKeyBox.Password = HermesEnvironment.ModelApiKey ?? "";

        PopulateModelCombo(provider);
        SelectCurrentModel(HermesEnvironment.DefaultModel);

        // Temperature
        var tempStr = HermesEnvironment.ReadConfigSetting("model", "temperature");
        if (double.TryParse(tempStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var temp))
            TemperatureSlider.Value = temp;
        TemperatureValueLabel.Text = TemperatureSlider.Value.ToString("F1", CultureInfo.InvariantCulture);

        // Max Tokens
        var maxTokStr = HermesEnvironment.ReadConfigSetting("model", "max_tokens");
        if (double.TryParse(maxTokStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxTok))
            MaxTokensBox.Value = maxTok;
    }

    // ── Agent ──
    private void LoadAgentSettings()
    {
        var maxTurns = HermesEnvironment.ReadConfigSetting("agent", "max_turns");
        if (double.TryParse(maxTurns, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mt))
            MaxTurnsBox.Value = mt;

        SelectComboByTag(ToolUseCombo,
            HermesEnvironment.ReadConfigSetting("agent", "tool_use") ?? "auto");
        SelectComboByTag(ApprovalsCombo,
            HermesEnvironment.ReadConfigSetting("agent", "approvals_mode") ?? "manual");

        var timeout = HermesEnvironment.ReadConfigSetting("agent", "approval_timeout");
        if (double.TryParse(timeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out var to))
            ApprovalTimeoutBox.Value = to;
    }

    // ── Gateway ──
    private void LoadGatewayStatus()
    {
        RefreshGatewayStatus();
    }

    private void RefreshGatewayStatus()
    {
        bool running = HermesEnvironment.IsGatewayRunning();
        GatewayStatusText.Text = running ? "Running" : "Stopped";
        GatewayToggleBtn.Content = running ? "Stop" : "Start";
    }

    // ── Platforms ──
    private void LoadPlatformSettings()
    {
        TelegramBotTokenBox.Password = HermesEnvironment.ReadPlatformSetting("telegram", "token") ?? "";
        DiscordBotTokenBox.Password = HermesEnvironment.ReadPlatformSetting("discord", "token") ?? "";
        DiscordRequireMentionToggle.IsOn = string.Equals(
            HermesEnvironment.ReadPlatformSetting("discord", "require_mention"), "true", StringComparison.OrdinalIgnoreCase);
        DiscordAutoThreadToggle.IsOn = string.Equals(
            HermesEnvironment.ReadPlatformSetting("discord", "auto_thread"), "true", StringComparison.OrdinalIgnoreCase);
        DiscordReactionsToggle.IsOn = string.Equals(
            HermesEnvironment.ReadPlatformSetting("discord", "reactions"), "true", StringComparison.OrdinalIgnoreCase);

        SlackBotTokenBox.Password = HermesEnvironment.ReadPlatformSetting("slack", "token") ?? "";
        SlackAppTokenBox.Password = HermesEnvironment.ReadPlatformSetting("slack", "app_token") ?? "";

        MatrixAccessTokenBox.Password = HermesEnvironment.ReadPlatformSetting("matrix", "token") ?? "";
        MatrixHomeserverBox.Text = HermesEnvironment.ReadPlatformSetting("matrix", "homeserver") ?? "";

        WebhookEnabledToggle.IsOn = string.Equals(
            HermesEnvironment.ReadPlatformSetting("webhook", "enabled"), "true", StringComparison.OrdinalIgnoreCase);
        var webhookPort = HermesEnvironment.ReadPlatformSetting("webhook", "port");
        if (double.TryParse(webhookPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wp))
            WebhookPortBox.Value = wp;
        WebhookHmacSecretBox.Password = HermesEnvironment.ReadPlatformSetting("webhook", "secret") ?? "";

        EmailAddressBox.Text = HermesEnvironment.ReadPlatformSetting("email", "address") ?? "";
        EmailPasswordBox.Password = HermesEnvironment.ReadPlatformSetting("email", "password") ?? "";
        EmailImapHostBox.Text = HermesEnvironment.ReadPlatformSetting("email", "imap_host") ?? "";
        var imapPort = HermesEnvironment.ReadPlatformSetting("email", "imap_port");
        if (double.TryParse(imapPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ip))
            EmailImapPortBox.Value = ip;
        EmailSmtpHostBox.Text = HermesEnvironment.ReadPlatformSetting("email", "smtp_host") ?? "";
        var smtpPort = HermesEnvironment.ReadPlatformSetting("email", "smtp_port");
        if (double.TryParse(smtpPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sp))
            EmailSmtpPortBox.Value = sp;
    }

    // ── Memory ──
    private void LoadMemorySettings()
    {
        MemoryEnabledToggle.IsOn = !string.Equals(
            HermesEnvironment.ReadConfigSetting("memory", "memory_enabled"), "false", StringComparison.OrdinalIgnoreCase);
        UserProfileEnabledToggle.IsOn = !string.Equals(
            HermesEnvironment.ReadConfigSetting("memory", "user_profile_enabled"), "false", StringComparison.OrdinalIgnoreCase);

        var memLimit = HermesEnvironment.ReadConfigSetting("memory", "memory_char_limit");
        if (double.TryParse(memLimit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ml))
            MemoryCharLimitBox.Value = ml;

        var userLimit = HermesEnvironment.ReadConfigSetting("memory", "user_char_limit");
        if (double.TryParse(userLimit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ul))
            UserCharLimitBox.Value = ul;

        CompressionEnabledToggle.IsOn = string.Equals(
            HermesEnvironment.ReadConfigSetting("compression", "enabled"), "true", StringComparison.OrdinalIgnoreCase);

        var threshold = HermesEnvironment.ReadConfigSetting("compression", "threshold");
        if (double.TryParse(threshold, NumberStyles.Float, CultureInfo.InvariantCulture, out var ct))
            CompressionThresholdSlider.Value = ct;
        CompressionThresholdLabel.Text = CompressionThresholdSlider.Value.ToString("F2", CultureInfo.InvariantCulture);
    }

    // ── Display ──
    private void LoadDisplaySettings()
    {
        ShowReasoningToggle.IsOn = string.Equals(
            HermesEnvironment.ReadConfigSetting("display", "show_reasoning"), "true", StringComparison.OrdinalIgnoreCase);
        StreamingToggle.IsOn = !string.Equals(
            HermesEnvironment.ReadConfigSetting("display", "streaming"), "false", StringComparison.OrdinalIgnoreCase);
        InlineDiffsToggle.IsOn = !string.Equals(
            HermesEnvironment.ReadConfigSetting("display", "inline_diffs"), "false", StringComparison.OrdinalIgnoreCase);
        ShowCostToggle.IsOn = string.Equals(
            HermesEnvironment.ReadConfigSetting("display", "show_cost"), "true", StringComparison.OrdinalIgnoreCase);
        RedactPiiToggle.IsOn = string.Equals(
            HermesEnvironment.ReadConfigSetting("privacy", "redact_pii"), "true", StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════
    //  Model logic (preserved from original)
    // ═══════════════════════════════════════════
    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var providerTag = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "local";
        PopulateModelCombo(providerTag);

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

    private void TemperatureSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (TemperatureValueLabel is not null)
            TemperatureValueLabel.Text = e.NewValue.ToString("F1", CultureInfo.InvariantCulture);
    }

    private void CompressionThresholdSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (CompressionThresholdLabel is not null)
            CompressionThresholdLabel.Text = e.NewValue.ToString("F2", CultureInfo.InvariantCulture);
    }

    // ═══════════════════════════════════════════
    //  Save handlers
    // ═══════════════════════════════════════════

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

            // Save model section (provider, base_url, default, temperature, max_tokens, optional api_key)
            var extras = new Dictionary<string, string>
            {
                ["provider"] = providerTag,
                ["base_url"] = baseUrl,
                ["default"] = model,
                ["temperature"] = TemperatureSlider.Value.ToString("F1", CultureInfo.InvariantCulture),
                ["max_tokens"] = ((int)MaxTokensBox.Value).ToString(CultureInfo.InvariantCulture),
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
                extras["api_key"] = apiKey;

            await HermesEnvironment.SaveConfigSectionAsync("model", extras);

            ModelSaveStatus.Text = "Saved successfully. Restart to apply.";
            ModelSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOnlineBrush"];
        }
        catch (Exception ex)
        {
            ModelSaveStatus.Text = $"Error: {ex.Message}";
            ModelSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
        }
    }

    private async void SaveAgentConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = new Dictionary<string, string>
            {
                ["max_turns"] = ((int)MaxTurnsBox.Value).ToString(CultureInfo.InvariantCulture),
                ["tool_use"] = (ToolUseCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto",
                ["approvals_mode"] = (ApprovalsCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "manual",
                ["approval_timeout"] = ((int)ApprovalTimeoutBox.Value).ToString(CultureInfo.InvariantCulture),
            };

            await HermesEnvironment.SaveConfigSectionAsync("agent", settings);

            AgentSaveStatus.Text = "Saved successfully. Restart to apply.";
            AgentSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOnlineBrush"];
        }
        catch (Exception ex)
        {
            AgentSaveStatus.Text = $"Error: {ex.Message}";
            AgentSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
        }
    }

    private async void SavePlatformConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Telegram
            if (!string.IsNullOrWhiteSpace(TelegramBotTokenBox.Password))
                await HermesEnvironment.SavePlatformSettingAsync("telegram", "token", TelegramBotTokenBox.Password.Trim());

            // Discord
            if (!string.IsNullOrWhiteSpace(DiscordBotTokenBox.Password))
                await HermesEnvironment.SavePlatformSettingAsync("discord", "token", DiscordBotTokenBox.Password.Trim());
            await HermesEnvironment.SavePlatformSettingAsync("discord", "require_mention", DiscordRequireMentionToggle.IsOn.ToString().ToLowerInvariant());
            await HermesEnvironment.SavePlatformSettingAsync("discord", "auto_thread", DiscordAutoThreadToggle.IsOn.ToString().ToLowerInvariant());
            await HermesEnvironment.SavePlatformSettingAsync("discord", "reactions", DiscordReactionsToggle.IsOn.ToString().ToLowerInvariant());

            // Slack
            if (!string.IsNullOrWhiteSpace(SlackBotTokenBox.Password))
                await HermesEnvironment.SavePlatformSettingAsync("slack", "token", SlackBotTokenBox.Password.Trim());
            if (!string.IsNullOrWhiteSpace(SlackAppTokenBox.Password))
                await HermesEnvironment.SavePlatformSettingAsync("slack", "app_token", SlackAppTokenBox.Password.Trim());

            // Matrix
            if (!string.IsNullOrWhiteSpace(MatrixAccessTokenBox.Password))
                await HermesEnvironment.SavePlatformSettingAsync("matrix", "token", MatrixAccessTokenBox.Password.Trim());
            if (!string.IsNullOrWhiteSpace(MatrixHomeserverBox.Text))
                await HermesEnvironment.SavePlatformSettingAsync("matrix", "homeserver", MatrixHomeserverBox.Text.Trim());

            // Webhook
            await HermesEnvironment.SavePlatformSettingAsync("webhook", "enabled", WebhookEnabledToggle.IsOn.ToString().ToLowerInvariant());
            await HermesEnvironment.SavePlatformSettingAsync("webhook", "port", ((int)WebhookPortBox.Value).ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(WebhookHmacSecretBox.Password))
                await HermesEnvironment.SavePlatformSettingAsync("webhook", "secret", WebhookHmacSecretBox.Password.Trim());

            // Email
            if (!string.IsNullOrWhiteSpace(EmailAddressBox.Text))
                await HermesEnvironment.SavePlatformSettingAsync("email", "address", EmailAddressBox.Text.Trim());
            if (!string.IsNullOrWhiteSpace(EmailPasswordBox.Password))
                await HermesEnvironment.SavePlatformSettingAsync("email", "password", EmailPasswordBox.Password.Trim());
            if (!string.IsNullOrWhiteSpace(EmailImapHostBox.Text))
                await HermesEnvironment.SavePlatformSettingAsync("email", "imap_host", EmailImapHostBox.Text.Trim());
            await HermesEnvironment.SavePlatformSettingAsync("email", "imap_port", ((int)EmailImapPortBox.Value).ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(EmailSmtpHostBox.Text))
                await HermesEnvironment.SavePlatformSettingAsync("email", "smtp_host", EmailSmtpHostBox.Text.Trim());
            await HermesEnvironment.SavePlatformSettingAsync("email", "smtp_port", ((int)EmailSmtpPortBox.Value).ToString(CultureInfo.InvariantCulture));

            PlatformSaveStatus.Text = "All platforms saved. Restart gateway to apply.";
            PlatformSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOnlineBrush"];
        }
        catch (Exception ex)
        {
            PlatformSaveStatus.Text = $"Error: {ex.Message}";
            PlatformSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
        }
    }

    private async void SaveMemoryConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var memSettings = new Dictionary<string, string>
            {
                ["memory_enabled"] = MemoryEnabledToggle.IsOn.ToString().ToLowerInvariant(),
                ["user_profile_enabled"] = UserProfileEnabledToggle.IsOn.ToString().ToLowerInvariant(),
                ["memory_char_limit"] = ((int)MemoryCharLimitBox.Value).ToString(CultureInfo.InvariantCulture),
                ["user_char_limit"] = ((int)UserCharLimitBox.Value).ToString(CultureInfo.InvariantCulture),
            };
            await HermesEnvironment.SaveConfigSectionAsync("memory", memSettings);

            var compSettings = new Dictionary<string, string>
            {
                ["enabled"] = CompressionEnabledToggle.IsOn.ToString().ToLowerInvariant(),
                ["threshold"] = CompressionThresholdSlider.Value.ToString("F2", CultureInfo.InvariantCulture),
            };
            await HermesEnvironment.SaveConfigSectionAsync("compression", compSettings);

            MemorySaveStatus.Text = "Saved successfully. Restart to apply.";
            MemorySaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOnlineBrush"];
        }
        catch (Exception ex)
        {
            MemorySaveStatus.Text = $"Error: {ex.Message}";
            MemorySaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
        }
    }

    private async void SaveDisplayConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var displaySettings = new Dictionary<string, string>
            {
                ["show_reasoning"] = ShowReasoningToggle.IsOn.ToString().ToLowerInvariant(),
                ["streaming"] = StreamingToggle.IsOn.ToString().ToLowerInvariant(),
                ["inline_diffs"] = InlineDiffsToggle.IsOn.ToString().ToLowerInvariant(),
                ["show_cost"] = ShowCostToggle.IsOn.ToString().ToLowerInvariant(),
            };
            await HermesEnvironment.SaveConfigSectionAsync("display", displaySettings);

            var privacySettings = new Dictionary<string, string>
            {
                ["redact_pii"] = RedactPiiToggle.IsOn.ToString().ToLowerInvariant(),
            };
            await HermesEnvironment.SaveConfigSectionAsync("privacy", privacySettings);

            DisplaySaveStatus.Text = "Saved successfully. Restart to apply.";
            DisplaySaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOnlineBrush"];
        }
        catch (Exception ex)
        {
            DisplaySaveStatus.Text = $"Error: {ex.Message}";
            DisplaySaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
        }
    }

    // ═══════════════════════════════════════════
    //  Gateway control
    // ═══════════════════════════════════════════
    private void GatewayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (HermesEnvironment.IsGatewayRunning())
        {
            HermesEnvironment.StopGateway();
        }
        else
        {
            HermesEnvironment.StartGateway();
        }

        // Slight delay before refresh — gateway needs a moment
        DispatcherQueue.TryEnqueue(async () =>
        {
            await System.Threading.Tasks.Task.Delay(1500);
            RefreshGatewayStatus();
        });
    }

    // ═══════════════════════════════════════════
    //  Path buttons (preserved from original)
    // ═══════════════════════════════════════════
    private void OpenHome_Click(object sender, RoutedEventArgs e) => HermesEnvironment.OpenHermesHome();
    private void OpenConfig_Click(object sender, RoutedEventArgs e) => HermesEnvironment.OpenConfig();
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => HermesEnvironment.OpenLogs();
    private void OpenWorkspace_Click(object sender, RoutedEventArgs e) => HermesEnvironment.OpenWorkspace();

    // ═══════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════
    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }
}
