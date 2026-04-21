using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hermes.Agent.Dreamer;
using Hermes.Agent.LLM;
using HermesDesktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;

namespace HermesDesktop.Views;

public sealed partial class SettingsPage : Page
{
    private static readonly ResourceLoader ResourceLoader = new();
    private readonly RuntimeStatusService _runtimeStatusService = App.Services.GetRequiredService<RuntimeStatusService>();

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

    /// <summary>
    /// Initializes the settings UI by loading all configuration sections and then refreshes the runtime status.
    /// </summary>
    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        LoadUserProfile();
        LoadModelSettings();
        LoadAgentSettings();
        LoadGatewayStatus();
        LoadPlatformSettings();
        LoadMemorySettings();
        LoadDisplaySettings();
        LoadExecutionSettings();
        LoadPluginSettings();
        LoadDreamerSettings();
        LoadSearchSettings();
        await RefreshRuntimeStatusAsync();
    }

    // ── User Profile ──

    private void LoadUserProfile()
    {
        var userMdPath = Path.Combine(HermesEnvironment.HermesHomePath, "USER.md");
        UserProfilePathLabel.Text = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("SettingsUserProfilePathFormat"), userMdPath);

        if (!File.Exists(userMdPath)) return;

        try
        {
            var content = File.ReadAllText(userMdPath);

            // Parse simple fields from the markdown structure
            UserNameBox.Text = ExtractSection(content, "Who They Are", "Technical Expertise")?.Trim() ?? "";
            UserRoleBox.Text = ExtractSection(content, "Technical Expertise", "How They Work")?.Trim() ?? "";
            UserStyleBox.Text = ExtractSection(content, "How They Work", "What I've Learned")?.Trim() ?? "";

            // Project directory from config
            UserProjectDirBox.Text = HermesEnvironment.AgentWorkingDirectory;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load user profile: {ex.Message}");
        }
    }

    private static string? ExtractSection(string md, string startHeader, string? endHeader)
    {
        var startMarker = $"## {startHeader}";
        var startIdx = md.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0) return null;

        startIdx = md.IndexOf('\n', startIdx);
        if (startIdx < 0) return null;
        startIdx++; // skip the newline

        int endIdx;
        if (endHeader is not null)
        {
            var endMarker = $"## {endHeader}";
            endIdx = md.IndexOf(endMarker, startIdx, StringComparison.OrdinalIgnoreCase);
            if (endIdx < 0) endIdx = md.Length;
        }
        else
        {
            endIdx = md.Length;
        }

        return md[startIdx..endIdx].Trim();
    }

    private async void SaveUserProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var userMdPath = Path.Combine(HermesEnvironment.HermesHomePath, "USER.md");
            var name = UserNameBox.Text.Trim();
            var role = UserRoleBox.Text.Trim();
            var style = UserStyleBox.Text.Trim();
            var projectDir = UserProjectDirBox.Text.Trim();

            var content = $@"# User Profile

This file is a living document about the human I work with. It helps me provide continuity across sessions and personalized assistance.

## Who They Are
{(string.IsNullOrEmpty(name) ? "Not configured yet." : name)}

## Technical Expertise
{(string.IsNullOrEmpty(role) ? "Not specified." : role)}

## How They Work
{(string.IsNullOrEmpty(style) ? "No preferences specified." : style)}

## What I've Learned
(This section is updated automatically by the agent as it learns about you.)
";

            await File.WriteAllTextAsync(userMdPath, content);

            // Save project directory to environment if changed
            if (!string.IsNullOrEmpty(projectDir) && Directory.Exists(projectDir))
            {
                Environment.SetEnvironmentVariable("HERMES_DESKTOP_WORKSPACE", projectDir, EnvironmentVariableTarget.User);
            }

            UserProfileSaveStatus.Text = ResourceLoader.GetString("SettingsProfileSaved");
            UserProfileSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOnlineBrush"];
        }
        catch (Exception ex)
        {
            UserProfileSaveStatus.Text = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("SettingsErrorFormat"), ex.Message);
            UserProfileSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
        }
    }

    private void EditUserMd_Click(object sender, RoutedEventArgs e)
    {
        var userMdPath = Path.Combine(HermesEnvironment.HermesHomePath, "USER.md");
        if (File.Exists(userMdPath))
        {
            var psi = new System.Diagnostics.ProcessStartInfo(userMdPath) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
    }

    private void BrowseProjectDir_Click(object sender, RoutedEventArgs e)
    {
        // Open folder picker — WinUI 3 doesn't have a simple folder picker inline,
        // so we'll use the environment variable approach and let users type/paste the path
        var current = UserProjectDirBox.Text;
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
        {
            var psi = new System.Diagnostics.ProcessStartInfo(current) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
    }

    // ── Model ──
    private void LoadModelSettings()
    {
        var provider = HermesEnvironment.ModelProvider.ToLowerInvariant();
        var normalizedProvider = provider == "custom" ? "local" : provider;
        SelectComboByTag(
            ProviderCombo,
            normalizedProvider,
            Math.Max(0, ProviderCombo.Items.Count - 1));
        BaseUrlBox.Text = HermesEnvironment.ModelBaseUrl;
        ModelBox.Text = HermesEnvironment.DefaultModel;
        ApiKeyBox.Password = HermesEnvironment.ModelApiKey ?? "";
        ApiKeyEnvBox.Text = HermesEnvironment.ModelApiKeyEnv ?? "";
        AuthHeaderBox.Text = HermesEnvironment.ModelAuthHeader;
        AuthSchemeBox.Text = HermesEnvironment.ModelAuthScheme;
        AuthTokenEnvBox.Text = HermesEnvironment.ModelAuthTokenEnv ?? "";
        AuthTokenCommandBox.Text = HermesEnvironment.ModelAuthTokenCommand ?? "";
        SelectComboByTag(AuthModeCombo, HermesEnvironment.ModelAuthMode, fallbackIndex: 0);
        UpdateAuthFieldState(HermesEnvironment.ModelAuthMode);

        PopulateModelCombo(normalizedProvider);
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
        GatewayStatusText.Text = running
            ? ResourceLoader.GetString("SettingsGatewayStatusRunning")
            : ResourceLoader.GetString("SettingsGatewayStatusStopped");
        GatewayToggleBtn.Content = running
            ? ResourceLoader.GetString("SettingsGatewayStop")
            : ResourceLoader.GetString("SettingsGatewayStart");
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
                Content = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("SettingsModelComboItemFormat"),
                    m.DisplayName, ModelCatalog.FormatContextLength(m.ContextLength)),
                Tag = m.Id
            });
        }

        if (ModelCombo.Items.Count > 0)
            ModelCombo.SelectedIndex = 0;

        _suppressModelComboEvent = false;

        if (models.Count > 0)
            ContextLengthLabel.Text = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("SettingsModelContextFormat"),
                ModelCatalog.FormatContextLength(models[0].ContextLength));
        else
            ContextLengthLabel.Text = ResourceLoader.GetString("SettingsModelContextUnknown");
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
                ContextLengthLabel.Text = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("SettingsModelContextFormat"),
                    ModelCatalog.FormatContextLength(ctx));
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
            ContextLengthLabel.Text = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("SettingsModelContextFormat"),
                ModelCatalog.FormatContextLength(ctx));
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

    private void AuthModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var authMode = (AuthModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "api_key";
        UpdateAuthFieldState(authMode);
    }

    // ═══════════════════════════════════════════
    //  Save handlers
    // ═══════════════════════════════════════════

    private async void SaveModelConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var providerTag = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "local";
            var baseUrl = BaseUrlBox.Text.Trim();
            var model = ModelBox.Text.Trim();
            var apiKey = ApiKeyBox.Password.Trim();
            var apiKeyEnv = ApiKeyEnvBox.Text.Trim();
            var authMode = (AuthModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "api_key";
            var authHeader = AuthHeaderBox.Text.Trim();
            var authScheme = AuthSchemeBox.Text.Trim();
            var authTokenEnv = AuthTokenEnvBox.Text.Trim();
            var authTokenCommand = AuthTokenCommandBox.Text.Trim();

            if (string.IsNullOrEmpty(model))
            {
                ModelSaveStatus.Text = ResourceLoader.GetString("SettingsModelRequired");
                ModelSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
                await RefreshRuntimeStatusAsync();
                return;
            }

            if (authMode == "api_key_env" && string.IsNullOrWhiteSpace(apiKeyEnv))
            {
                ModelSaveStatus.Text = ResourceLoader.GetString("SettingsModelApiKeyEnvRequired");
                ModelSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
                return;
            }

            if (authMode == "oauth_proxy_env" && string.IsNullOrWhiteSpace(authTokenEnv))
            {
                ModelSaveStatus.Text = ResourceLoader.GetString("SettingsModelOAuthEnvRequired");
                ModelSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
                return;
            }

            if (authMode == "oauth_proxy_command" && string.IsNullOrWhiteSpace(authTokenCommand))
            {
                ModelSaveStatus.Text = ResourceLoader.GetString("SettingsModelOAuthCommandRequired");
                ModelSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
                return;
            }

            await HermesEnvironment.SaveModelConfigAsync(
                providerTag,
                baseUrl,
                model,
                apiKey,
                apiKeyEnv,
                authMode,
                authHeader,
                authScheme,
                authTokenEnv,
                authTokenCommand,
                TemperatureSlider.Value.ToString("F1", CultureInfo.InvariantCulture),
                ((int)MaxTokensBox.Value).ToString(CultureInfo.InvariantCulture));
            ModelSaveStatus.Text = ResourceLoader.GetString("SettingsSaveSuccessRestart");
            ModelSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOnlineBrush"];
            await RefreshRuntimeStatusAsync();
        }
        catch (Exception ex)
        {
            ModelSaveStatus.Text = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("SettingsErrorFormat"), ex.Message);
            ModelSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
            await RefreshRuntimeStatusAsync();
        }
    }

    private async Task RefreshRuntimeStatusAsync()
    {
        ApplyRuntimeStatusSnapshot(_runtimeStatusService.GetConfiguredSnapshot());
        var snapshot = await _runtimeStatusService.RefreshAsync(CancellationToken.None);
        ApplyRuntimeStatusSnapshot(snapshot);
    }

    private void ApplyRuntimeStatusSnapshot(RuntimeStatusSnapshot snapshot)
    {
        RuntimeProviderStatusText.Text = snapshot.DisplayProvider;
        RuntimeModelStatusText.Text = snapshot.DisplayModel;

        RuntimeConnectionStatusText.Text = snapshot.ConnectionState switch
        {
            RuntimeConnectionState.Connected => ResourceLoader.GetString("StatusConnected"),
            RuntimeConnectionState.Checking => ResourceLoader.GetString("ChatStatusChecking"),
            _ => ResourceLoader.GetString("StatusOffline"),
        };

        RuntimeConnectionStatusText.Foreground = snapshot.ConnectionState switch
        {
            RuntimeConnectionState.Connected => (Brush)Application.Current.Resources["ConnectionOnlineBrush"],
            RuntimeConnectionState.Checking => (Brush)Application.Current.Resources["AppTextSecondaryBrush"],
            _ => (Brush)Application.Current.Resources["ConnectionOfflineBrush"],
        };
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

            AgentSaveStatus.Text = ResourceLoader.GetString("SettingsSaveSuccessRestart");
            AgentSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOnlineBrush"];
        }
        catch (Exception ex)
        {
            AgentSaveStatus.Text = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("SettingsErrorFormat"), ex.Message);
            AgentSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
        }
    }

    private async void SavePlatformConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Telegram — always persist so clearing a token actually takes effect
            await HermesEnvironment.SavePlatformSettingAsync("telegram", "token", TelegramBotTokenBox.Password.Trim());

            // Discord
            await HermesEnvironment.SavePlatformSettingAsync("discord", "token", DiscordBotTokenBox.Password.Trim());
            await HermesEnvironment.SavePlatformSettingAsync("discord", "require_mention", DiscordRequireMentionToggle.IsOn.ToString().ToLowerInvariant());
            await HermesEnvironment.SavePlatformSettingAsync("discord", "auto_thread", DiscordAutoThreadToggle.IsOn.ToString().ToLowerInvariant());
            await HermesEnvironment.SavePlatformSettingAsync("discord", "reactions", DiscordReactionsToggle.IsOn.ToString().ToLowerInvariant());

            // Slack
            await HermesEnvironment.SavePlatformSettingAsync("slack", "token", SlackBotTokenBox.Password.Trim());
            await HermesEnvironment.SavePlatformSettingAsync("slack", "app_token", SlackAppTokenBox.Password.Trim());

            // Matrix
            await HermesEnvironment.SavePlatformSettingAsync("matrix", "token", MatrixAccessTokenBox.Password.Trim());
            await HermesEnvironment.SavePlatformSettingAsync("matrix", "homeserver", MatrixHomeserverBox.Text.Trim());

            // Webhook
            await HermesEnvironment.SavePlatformSettingAsync("webhook", "enabled", WebhookEnabledToggle.IsOn.ToString().ToLowerInvariant());
            await HermesEnvironment.SavePlatformSettingAsync("webhook", "port", ((int)WebhookPortBox.Value).ToString(CultureInfo.InvariantCulture));
            await HermesEnvironment.SavePlatformSettingAsync("webhook", "secret", WebhookHmacSecretBox.Password.Trim());

            // Email
            await HermesEnvironment.SavePlatformSettingAsync("email", "address", EmailAddressBox.Text.Trim());
            await HermesEnvironment.SavePlatformSettingAsync("email", "password", EmailPasswordBox.Password.Trim());
            await HermesEnvironment.SavePlatformSettingAsync("email", "imap_host", EmailImapHostBox.Text.Trim());
            await HermesEnvironment.SavePlatformSettingAsync("email", "imap_port", ((int)EmailImapPortBox.Value).ToString(CultureInfo.InvariantCulture));
            await HermesEnvironment.SavePlatformSettingAsync("email", "smtp_host", EmailSmtpHostBox.Text.Trim());
            await HermesEnvironment.SavePlatformSettingAsync("email", "smtp_port", ((int)EmailSmtpPortBox.Value).ToString(CultureInfo.InvariantCulture));

            PlatformSaveStatus.Text = ResourceLoader.GetString("SettingsSaveAllPlatformsSuccess");
            PlatformSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOnlineBrush"];
        }
        catch (Exception ex)
        {
            PlatformSaveStatus.Text = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("SettingsErrorFormat"), ex.Message);
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

            MemorySaveStatus.Text = ResourceLoader.GetString("SettingsSaveSuccessRestart");
            MemorySaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOnlineBrush"];
        }
        catch (Exception ex)
        {
            MemorySaveStatus.Text = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("SettingsErrorFormat"), ex.Message);
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

            DisplaySaveStatus.Text = ResourceLoader.GetString("SettingsSaveSuccessRestart");
            DisplaySaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOnlineBrush"];
        }
        catch (Exception ex)
        {
            DisplaySaveStatus.Text = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("SettingsErrorFormat"), ex.Message);
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
    //  Execution Environment
    // ═══════════════════════════════════════════

    private void LoadExecutionSettings()
    {
        SelectComboByTag(ExecBackendCombo,
            HermesEnvironment.ReadConfigSetting("terminal", "backend") ?? "local");

        ExecWorkingDirBox.Text = HermesEnvironment.ReadConfigSetting("terminal", "working_directory") ?? ".";

        var timeout = HermesEnvironment.ReadConfigSetting("terminal", "timeout");
        if (double.TryParse(timeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out var to))
            ExecTimeoutBox.Value = to;

        DockerImageBox.Text = HermesEnvironment.ReadConfigSetting("terminal", "docker_image")
            ?? "nikolaik/python-nodejs:python3.11-nodejs20";

        var cpu = HermesEnvironment.ReadConfigSetting("terminal", "container_cpu");
        if (double.TryParse(cpu, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
            DockerCpuBox.Value = c;

        var mem = HermesEnvironment.ReadConfigSetting("terminal", "container_memory");
        if (double.TryParse(mem, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
            DockerMemoryBox.Value = m;

        UpdateDockerVisibility();
    }

    private void ExecBackendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDockerVisibility();
    }

    private void UpdateDockerVisibility()
    {
        if (DockerOptionsPanel is null) return;
        var tag = (ExecBackendCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "local";
        DockerOptionsPanel.Visibility = string.Equals(tag, "docker", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SaveExecutionConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = new Dictionary<string, string>
            {
                ["backend"] = (ExecBackendCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "local",
                ["working_directory"] = ExecWorkingDirBox.Text.Trim(),
                ["timeout"] = ((int)ExecTimeoutBox.Value).ToString(CultureInfo.InvariantCulture),
                ["docker_image"] = DockerImageBox.Text.Trim(),
                ["container_cpu"] = ((int)DockerCpuBox.Value).ToString(CultureInfo.InvariantCulture),
                ["container_memory"] = ((int)DockerMemoryBox.Value).ToString(CultureInfo.InvariantCulture),
            };

            await HermesEnvironment.SaveConfigSectionAsync("terminal", settings);

            ExecutionSaveStatus.Text = ResourceLoader.GetString("SettingsSaveSuccessRestart");
            ExecutionSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOnlineBrush"];
        }
        catch (Exception ex)
        {
            ExecutionSaveStatus.Text = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("SettingsErrorFormat"), ex.Message);
            ExecutionSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
        }
    }

    // ═══════════════════════════════════════════
    //  Plugins & Extensions
    // ═══════════════════════════════════════════

    private void LoadPluginSettings()
    {
        var builtinEnabled = HermesEnvironment.ReadConfigSetting("plugins", "builtin_memory");
        BuiltinMemoryPluginToggle.IsOn = !string.Equals(builtinEnabled, "false", StringComparison.OrdinalIgnoreCase);

        ExternalMemoryProviderBox.Text = HermesEnvironment.ReadConfigSetting("plugins", "external_memory_provider") ?? "";
    }

    private async void SavePluginConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = new Dictionary<string, string>
            {
                ["builtin_memory"] = BuiltinMemoryPluginToggle.IsOn.ToString().ToLowerInvariant(),
                ["external_memory_provider"] = ExternalMemoryProviderBox.Text.Trim(),
            };

            await HermesEnvironment.SaveConfigSectionAsync("plugins", settings);

            PluginSaveStatus.Text = ResourceLoader.GetString("SettingsSaveSuccessRestart");
            PluginSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOnlineBrush"];
        }
        catch (Exception ex)
        {
            PluginSaveStatus.Text = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("SettingsErrorFormat"), ex.Message);
            PluginSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
        }
    }

    // ── Web Search ──
    private void LoadSearchSettings()
    {
        SelectComboByTag(SearchProviderCombo, HermesEnvironment.WebSearchProvider, fallbackIndex: 0);
        SearchGoogleApiKeyBox.Password = HermesEnvironment.WebSearchGoogleApiKey ?? "";
        SearchGoogleEngineIdBox.Text = HermesEnvironment.WebSearchGoogleEngineId ?? "";
        SearchBingApiKeyBox.Password = HermesEnvironment.WebSearchBingApiKey ?? "";
        UpdateSearchFieldState((SearchProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString());
    }

    private void SearchProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSearchFieldState((SearchProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString());
    }

    private void UpdateSearchFieldState(string? provider)
    {
        var p = (provider ?? "duckduckgo").ToLowerInvariant();
        SearchGoogleApiKeyBox.IsEnabled = p == "google";
        SearchGoogleEngineIdBox.IsEnabled = p == "google";
        SearchBingApiKeyBox.IsEnabled = p == "bing";
    }

    private async void SaveSearchConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var provider = (SearchProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "duckduckgo";
            var googleKey = SearchGoogleApiKeyBox.Password.Trim();
            var googleEngineId = SearchGoogleEngineIdBox.Text.Trim();
            var bingKey = SearchBingApiKeyBox.Password.Trim();

            if (provider == "google" && (string.IsNullOrEmpty(googleKey) || string.IsNullOrEmpty(googleEngineId)))
            {
                SearchSaveStatus.Text = ResourceLoader.GetString("SettingsSearchMissingGoogleKey");
                SearchSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
                return;
            }

            if (provider == "bing" && string.IsNullOrEmpty(bingKey))
            {
                SearchSaveStatus.Text = ResourceLoader.GetString("SettingsSearchMissingBingKey");
                SearchSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
                return;
            }

            var settings = new Dictionary<string, string>
            {
                ["provider"] = provider,
                ["google_api_key"] = googleKey,
                ["google_engine_id"] = googleEngineId,
                ["bing_api_key"] = bingKey,
            };

            await HermesEnvironment.SaveConfigSectionAsync("search", settings);

            SearchSaveStatus.Text = ResourceLoader.GetString("SettingsSaveSuccessRestart");
            SearchSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOnlineBrush"];
        }
        catch (Exception ex)
        {
            SearchSaveStatus.Text = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("SettingsErrorFormat"), ex.Message);
            SearchSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
        }
    }

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

    private static void SelectComboByTag(ComboBox combo, string? tag, int fallbackIndex)
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

        combo.SelectedIndex = fallbackIndex;
    }

    /// <summary>
    /// Enable or disable the authentication-related input fields to match the selected authentication mode.
    /// </summary>
    private void UpdateAuthFieldState(string? authMode)
    {
        var mode = (authMode ?? "api_key").ToLowerInvariant();
        var usesProxyToken = mode is "oauth_proxy_env" or "oauth_proxy_command";

        ApiKeyBox.IsEnabled = mode == "api_key";
        ApiKeyEnvBox.IsEnabled = mode == "api_key_env";
        AuthHeaderBox.IsEnabled = usesProxyToken;
        AuthSchemeBox.IsEnabled = usesProxyToken;
        AuthTokenEnvBox.IsEnabled = mode == "oauth_proxy_env";
        AuthTokenCommandBox.IsEnabled = mode == "oauth_proxy_command";
    }

    // ── Dreamer ──
    private static string NormalizeDreamerDigestTimesForSave(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var validated = new List<string>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var timeParts = part.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (timeParts.Length == 2 &&
                int.TryParse(timeParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) &&
                int.TryParse(timeParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) &&
                h >= 0 && h <= 23 && m >= 0 && m <= 59)
                validated.Add(part);
        }

        return string.Join(",", validated);
    }

    /// <summary>
    /// Loads the Dreamer configuration from config.yaml and applies its values to the Dreamer UI controls.
    /// </summary>
    private void LoadDreamerSettings()
    {
        var cfgPath = Path.Combine(HermesEnvironment.HermesHomePath, "config.yaml");
        var c = DreamerConfig.Load(cfgPath);
        DreamerEnabledToggle.IsOn = c.Enabled;
        DreamerWalkIntervalBox.Value = c.WalkIntervalMinutes;
        DreamerWalkModelBox.Text = c.WalkModel;
        DreamerWalkBaseUrlBox.Text = c.WalkBaseUrl;
        DreamerDigestTimesBox.Text = string.Join(", ", c.DigestTimes);
        DreamerTriggerThresholdBox.Value = c.TriggerThreshold;
        DreamerMinWalksToTriggerBox.Value = c.MinWalksToTrigger;
        DreamerDiscordChannelBox.Text = c.DiscordChannelId;
    }

    /// <summary>
    /// Persists the Dreamer settings from the UI into the application's "dreamer" configuration section.
    /// </summary>
    private async void SaveDreamerConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfgPath = Path.Combine(HermesEnvironment.HermesHomePath, "config.yaml");
            var cur = DreamerConfig.Load(cfgPath);
            var rss = string.Join(",", cur.RssFeeds);
            var digest = NormalizeDreamerDigestTimesForSave(DreamerDigestTimesBox.Text);
            var dict = new Dictionary<string, string>
            {
                ["enabled"] = DreamerEnabledToggle.IsOn ? "true" : "false",
                ["walk_provider"] = cur.WalkProvider,
                ["walk_model"] = string.IsNullOrWhiteSpace(DreamerWalkModelBox.Text) ? cur.WalkModel : DreamerWalkModelBox.Text.Trim(),
                ["walk_base_url"] = string.IsNullOrWhiteSpace(DreamerWalkBaseUrlBox.Text) ? cur.WalkBaseUrl : DreamerWalkBaseUrlBox.Text.Trim(),
                ["walk_temperature"] = cur.WalkTemperature.ToString(CultureInfo.InvariantCulture),
                ["walk_max_tokens"] = cur.WalkMaxTokens.ToString(CultureInfo.InvariantCulture),
                ["build_provider"] = cur.BuildProvider,
                ["build_model"] = cur.BuildModel,
                ["walk_interval_minutes"] = ((int)DreamerWalkIntervalBox.Value).ToString(CultureInfo.InvariantCulture),
                ["digest_times"] = digest,
                ["discord_channel_id"] = DreamerDiscordChannelBox.Text.Trim(),
                ["trigger_threshold"] = DreamerTriggerThresholdBox.Value.ToString(CultureInfo.InvariantCulture),
                ["min_walks_to_trigger"] = ((int)DreamerMinWalksToTriggerBox.Value).ToString(CultureInfo.InvariantCulture),
                ["autonomy"] = cur.Autonomy,
                ["input_transcripts"] = cur.InputTranscripts ? "true" : "false",
                ["input_inbox"] = cur.InputInbox ? "true" : "false",
                ["rss_feeds"] = rss
            };
            if (!string.IsNullOrWhiteSpace(cur.BuildBaseUrl))
                dict["build_base_url"] = cur.BuildBaseUrl;

            await HermesEnvironment.SaveConfigSectionAsync("dreamer", dict);
            DreamerSaveStatus.Text = ResourceLoader.GetString("SettingsDreamerSaved");
            DreamerSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOnlineBrush"];
        }
        catch (Exception ex)
        {
            DreamerSaveStatus.Text = string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("SettingsErrorFormat"), ex.Message);
            DreamerSaveStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectionOfflineBrush"];
        }
    }

    /// <summary>
    /// Ensures the dreamer directory exists and opens it in the system file explorer.
    /// </summary>
    private void OpenDreamerRoom_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(HermesEnvironment.HermesHomePath, "dreamer");
        Directory.CreateDirectory(dir);
        var psi = new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true };
        System.Diagnostics.Process.Start(psi);
    }
}
