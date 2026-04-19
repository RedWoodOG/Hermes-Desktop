using System;
using System.Collections.Generic;
using System.Threading;
using Hermes.Agent.Gateway;
using Hermes.Agent.Gateway.Platforms;
using Platform = Hermes.Agent.Gateway.Platform;
using HermesDesktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;

namespace HermesDesktop.Views;

public sealed partial class IntegrationsPage : Page
{
    private static readonly ResourceLoader ResourceLoader = new();
    private bool _suppressWhatsAppToggle;
    private bool _suppressWebhookToggle;

    public IntegrationsPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshNativeGatewayStatus();
        RefreshGatewayStatus();
        RefreshTelegramDisplay();
        RefreshDiscordDisplay();
        RefreshSlackDisplay();
        RefreshWhatsAppDisplay();
        RefreshMatrixDisplay();
        RefreshWebhookDisplay();
    }

    // =========================================================================
    // Native Gateway (C#) Status
    // =========================================================================

    private void RefreshNativeGatewayStatus()
    {
        bool running = HermesEnvironment.IsNativeGatewayRunning();

        NativeGatewayStatusText.Text = running ? "Running" : "Stopped";
        NativeGatewayIndicator.Fill = running
            ? (Brush)Application.Current.Resources["ConnectionOnlineBrush"]
            : (Brush)Application.Current.Resources["ConnectionOfflineBrush"];

        var adapterStatus = HermesEnvironment.GetNativeAdapterStatus();
        if (running && adapterStatus.Count > 0)
        {
            var parts = new List<string>();
            foreach (var (platform, connected) in adapterStatus)
                parts.Add($"{platform}: {(connected ? "Connected" : "Disconnected")}");
            NativeGatewayStateText.Text = string.Join(" | ", parts);
        }
        else
        {
            var tgToken = HermesEnvironment.ReadPlatformSetting("telegram", "token");
            var dcToken = HermesEnvironment.ReadPlatformSetting("discord", "token");
            bool hasNativeTokens = !string.IsNullOrWhiteSpace(tgToken) || !string.IsNullOrWhiteSpace(dcToken);

            NativeGatewayStateText.Text = hasNativeTokens
                ? "Native gateway is not running. Click Start to launch it."
                : "No Telegram or Discord tokens configured. Save a token below to get started.";
        }

        NativeGatewayToggleButton.Content = running ? "Stop Native Gateway" : "Start Native Gateway";

        // Build per-adapter status indicators
        AdapterStatusPanel.Children.Clear();
        foreach (var platform in new[] { Platform.Telegram, Platform.Discord })
        {
            var indicator = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            var key = platform.ToString();
            if (adapterStatus.TryGetValue(key, out var connected) && connected)
                indicator.Fill = (Brush)Application.Current.Resources["ConnectionOnlineBrush"];
            else
                indicator.Fill = (Brush)Application.Current.Resources["ConnectionOfflineBrush"];

            var label = new TextBlock
            {
                Text = platform.ToString(),
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["AppTextSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 12, 0)
            };

            AdapterStatusPanel.Children.Add(indicator);
            AdapterStatusPanel.Children.Add(label);
        }
    }

    private async void NativeGatewayToggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var gateway = App.Services.GetRequiredService<GatewayService>();

            if (gateway.IsRunning)
            {
                await gateway.StopAsync();
            }
            else
            {
                StartNativeGatewayFromUI(gateway);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Native gateway toggle error: {ex.Message}");
        }

        DispatcherQueue.TryEnqueue(async () =>
        {
            await System.Threading.Tasks.Task.Delay(1500);
            RefreshNativeGatewayStatus();
        });
    }

    private async void RestartNativeGateway_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var gateway = App.Services.GetRequiredService<GatewayService>();

            if (gateway.IsRunning)
                await gateway.StopAsync();

            // Brief pause to let connections close
            await System.Threading.Tasks.Task.Delay(500);

            StartNativeGatewayFromUI(gateway);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Native gateway restart error: {ex.Message}");
        }

        DispatcherQueue.TryEnqueue(async () =>
        {
            await System.Threading.Tasks.Task.Delay(2000);
            RefreshNativeGatewayStatus();
        });
    }

    private static void StartNativeGatewayFromUI(GatewayService gateway)
    {
        // Wire agent handler if not already set
        var agent = App.Services.GetRequiredService<Hermes.Agent.Core.Agent>();
        gateway.SetAgentHandler(async (sessionId, userMessage, platform) =>
        {
            var session = new Hermes.Agent.Core.Session { Id = sessionId, Platform = platform };
            return await agent.ChatAsync(userMessage, session, CancellationToken.None);
        });

        var adapters = new List<IPlatformAdapter>();

        var tgToken = HermesEnvironment.ReadPlatformSetting("telegram", "token");
        if (!string.IsNullOrWhiteSpace(tgToken))
            adapters.Add(new TelegramAdapter(tgToken));

        var dcToken = HermesEnvironment.ReadPlatformSetting("discord", "token");
        if (!string.IsNullOrWhiteSpace(dcToken))
            adapters.Add(new DiscordAdapter(dcToken));

        if (adapters.Count > 0)
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try { await gateway.StartAsync(adapters, CancellationToken.None); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Gateway start failed: {ex.Message}"); }
            });
        }
    }

    // =========================================================================
    // Python Gateway Status (advanced platforms)
    // =========================================================================

    private void RefreshGatewayStatus()
    {
        bool running = HermesEnvironment.IsGatewayRunning();
        bool installed = HermesEnvironment.HermesInstalled;

        GatewayStatusText.Text = running ? "Running" : "Stopped";
        GatewayIndicator.Fill = running
            ? (Brush)Application.Current.Resources["ConnectionOnlineBrush"]
            : (Brush)Application.Current.Resources["ConnectionOfflineBrush"];

        string state = HermesEnvironment.ReadGatewayState();
        GatewayStateText.Text = running
            ? $"State: {state}"
            : installed
                ? "Gateway is not running. Click Start to launch it."
                : HermesEnvironment.HasPythonSidecarRelevantConfig
                    ? "Hermes CLI / hermes-agent not found. Install it to run Slack, WhatsApp, Matrix, and webhooks. Telegram and Discord use the native gateway above."
                    : "Optional: hermes-agent is not installed. Telegram and Discord work through the native gateway; install the Python gateway only if you need Slack, WhatsApp, Matrix, or webhooks.";

        GatewayToggleButton.Content = running ? "Stop Gateway" : "Start Gateway";
        GatewayToggleButton.IsEnabled = installed;
    }

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

        // Small delay then refresh
        DispatcherQueue.TryEnqueue(async () =>
        {
            await System.Threading.Tasks.Task.Delay(1500);
            RefreshGatewayStatus();
        });
    }

    private void GatewayRefresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshAll();
    }

    // =========================================================================
    // Telegram
    // =========================================================================

    private void RefreshTelegramDisplay()
    {
        var configToken = HermesEnvironment.ReadPlatformSetting("telegram", "token");
        var legacyToken = HermesEnvironment.ReadIntegrationSetting("telegram_bot_token");
        var envConfigured = HermesEnvironment.TelegramConfigured;
        var token = configToken ?? legacyToken;
        var hasToken = !string.IsNullOrWhiteSpace(token) || envConfigured;

        TelegramStatusText.Text = hasToken
            ? ResourceLoader.GetString("StatusConfigured")
            : ResourceLoader.GetString("StatusNotDetected");

        TelegramMaskedText.Text = !string.IsNullOrWhiteSpace(token)
            ? MaskToken(token)
            : envConfigured ? "Set via environment variable" : "Not configured";
    }

    private async void SaveTelegram_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var token = TelegramTokenBox.Password.Trim();
            if (string.IsNullOrEmpty(token))
            {
                SetStatus(TelegramSaveStatus, "Token cannot be empty.", false);
                return;
            }

            await HermesEnvironment.SavePlatformSettingAsync("telegram", "token", token);
            await HermesEnvironment.SavePlatformSettingAsync("telegram", "enabled", "true");
            SetStatus(TelegramSaveStatus, "Saved to config.yaml.", true);
            TelegramTokenBox.Password = "";
            RefreshTelegramDisplay();
        }
        catch (Exception ex)
        {
            SetStatus(TelegramSaveStatus, $"Error: {ex.Message}", false);
        }
    }

    // =========================================================================
    // Discord
    // =========================================================================

    private void RefreshDiscordDisplay()
    {
        var configToken = HermesEnvironment.ReadPlatformSetting("discord", "token");
        var legacyToken = HermesEnvironment.ReadIntegrationSetting("discord_bot_token");
        var envConfigured = HermesEnvironment.DiscordConfigured;
        var token = configToken ?? legacyToken;
        var hasToken = !string.IsNullOrWhiteSpace(token) || envConfigured;

        DiscordStatusText.Text = hasToken
            ? ResourceLoader.GetString("StatusConfigured")
            : ResourceLoader.GetString("StatusNotDetected");

        DiscordMaskedText.Text = !string.IsNullOrWhiteSpace(token)
            ? MaskToken(token)
            : envConfigured ? "Set via environment variable" : "Not configured";
    }

    private async void SaveDiscord_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var token = DiscordTokenBox.Password.Trim();
            if (string.IsNullOrEmpty(token))
            {
                SetStatus(DiscordSaveStatus, "Token cannot be empty.", false);
                return;
            }

            await HermesEnvironment.SavePlatformSettingAsync("discord", "token", token);
            await HermesEnvironment.SavePlatformSettingAsync("discord", "enabled", "true");
            SetStatus(DiscordSaveStatus, "Saved to config.yaml.", true);
            DiscordTokenBox.Password = "";
            RefreshDiscordDisplay();
        }
        catch (Exception ex)
        {
            SetStatus(DiscordSaveStatus, $"Error: {ex.Message}", false);
        }
    }

    // =========================================================================
    // Slack
    // =========================================================================

    private void RefreshSlackDisplay()
    {
        var configToken = HermesEnvironment.ReadPlatformSetting("slack", "token");
        var envConfigured = HermesEnvironment.SlackConfigured;
        var hasToken = !string.IsNullOrWhiteSpace(configToken) || envConfigured;

        SlackStatusText.Text = hasToken
            ? ResourceLoader.GetString("StatusConfigured")
            : ResourceLoader.GetString("StatusNotDetected");

        SlackMaskedText.Text = !string.IsNullOrWhiteSpace(configToken)
            ? MaskToken(configToken)
            : envConfigured ? "Set via environment variable" : "Not configured";
    }

    private async void SaveSlack_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var botToken = SlackBotTokenBox.Password.Trim();
            var appToken = SlackAppTokenBox.Password.Trim();

            if (string.IsNullOrEmpty(botToken))
            {
                SetStatus(SlackSaveStatus, "Bot token cannot be empty.", false);
                return;
            }

            await HermesEnvironment.SavePlatformSettingAsync("slack", "token", botToken);
            await HermesEnvironment.SavePlatformSettingAsync("slack", "enabled", "true");

            if (!string.IsNullOrEmpty(appToken))
            {
                await HermesEnvironment.SavePlatformSettingAsync("slack", "app_token", appToken);
            }

            SetStatus(SlackSaveStatus, "Saved to config.yaml.", true);
            SlackBotTokenBox.Password = "";
            SlackAppTokenBox.Password = "";
            RefreshSlackDisplay();
        }
        catch (Exception ex)
        {
            SetStatus(SlackSaveStatus, $"Error: {ex.Message}", false);
        }
    }

    // =========================================================================
    // WhatsApp
    // =========================================================================

    private void RefreshWhatsAppDisplay()
    {
        var envConfigured = HermesEnvironment.WhatsAppConfigured;

        WhatsAppStatusText.Text = envConfigured
            ? ResourceLoader.GetString("StatusConfigured")
            : ResourceLoader.GetString("StatusNotDetected");

        WhatsAppMaskedText.Text = envConfigured ? "Enabled" : "Not enabled";

        _suppressWhatsAppToggle = true;
        WhatsAppEnabledToggle.IsOn = envConfigured;
        _suppressWhatsAppToggle = false;
    }

    private async void WhatsAppToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressWhatsAppToggle) return;

        try
        {
            string value = WhatsAppEnabledToggle.IsOn ? "true" : "false";
            await HermesEnvironment.SavePlatformSettingAsync("whatsapp", "enabled", value);
            SetStatus(WhatsAppSaveStatus, "Saved to config.yaml.", true);
            RefreshWhatsAppDisplay();
        }
        catch (Exception ex)
        {
            SetStatus(WhatsAppSaveStatus, $"Error: {ex.Message}", false);
        }
    }

    // =========================================================================
    // Matrix
    // =========================================================================

    private void RefreshMatrixDisplay()
    {
        var configToken = HermesEnvironment.ReadPlatformSetting("matrix", "token");
        var envConfigured = HermesEnvironment.MatrixConfigured;
        var hasToken = !string.IsNullOrWhiteSpace(configToken) || envConfigured;

        MatrixStatusText.Text = hasToken
            ? ResourceLoader.GetString("StatusConfigured")
            : ResourceLoader.GetString("StatusNotDetected");

        MatrixMaskedText.Text = !string.IsNullOrWhiteSpace(configToken)
            ? MaskToken(configToken)
            : envConfigured ? "Set via environment variable" : "Not configured";

        // Load homeserver into text box if present in config
        var homeserver = HermesEnvironment.ReadPlatformSetting("matrix", "homeserver");
        if (!string.IsNullOrWhiteSpace(homeserver) && string.IsNullOrEmpty(MatrixHomeserverBox.Text))
        {
            MatrixHomeserverBox.Text = homeserver;
        }
    }

    private async void SaveMatrix_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var token = MatrixTokenBox.Password.Trim();
            var homeserver = MatrixHomeserverBox.Text.Trim();

            if (string.IsNullOrEmpty(token))
            {
                SetStatus(MatrixSaveStatus, "Access token cannot be empty.", false);
                return;
            }

            await HermesEnvironment.SavePlatformSettingAsync("matrix", "token", token);
            await HermesEnvironment.SavePlatformSettingAsync("matrix", "enabled", "true");

            if (!string.IsNullOrEmpty(homeserver))
            {
                await HermesEnvironment.SavePlatformSettingAsync("matrix", "homeserver", homeserver);
            }

            SetStatus(MatrixSaveStatus, "Saved to config.yaml.", true);
            MatrixTokenBox.Password = "";
            RefreshMatrixDisplay();
        }
        catch (Exception ex)
        {
            SetStatus(MatrixSaveStatus, $"Error: {ex.Message}", false);
        }
    }

    // =========================================================================
    // Webhook
    // =========================================================================

    private void RefreshWebhookDisplay()
    {
        var envConfigured = HermesEnvironment.WebhookConfigured;

        WebhookStatusText.Text = envConfigured
            ? ResourceLoader.GetString("StatusConfigured")
            : ResourceLoader.GetString("StatusNotDetected");

        WebhookMaskedText.Text = envConfigured ? "Enabled" : "Not enabled";

        _suppressWebhookToggle = true;
        WebhookEnabledToggle.IsOn = envConfigured;
        _suppressWebhookToggle = false;

        // Load port from config
        var port = HermesEnvironment.ReadPlatformSetting("webhook", "port");
        if (!string.IsNullOrWhiteSpace(port) && string.IsNullOrEmpty(WebhookPortBox.Text))
        {
            WebhookPortBox.Text = port;
        }
    }

    private async void WebhookToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressWebhookToggle) return;

        try
        {
            string value = WebhookEnabledToggle.IsOn ? "true" : "false";
            await HermesEnvironment.SavePlatformSettingAsync("webhook", "enabled", value);
            RefreshWebhookDisplay();
        }
        catch (Exception ex)
        {
            SetStatus(WebhookSaveStatus, $"Error: {ex.Message}", false);
        }
    }

    private async void SaveWebhook_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await HermesEnvironment.SavePlatformSettingAsync("webhook", "enabled", "true");

            var port = WebhookPortBox.Text.Trim();
            if (!string.IsNullOrEmpty(port))
            {
                await HermesEnvironment.SavePlatformSettingAsync("webhook", "port", port);
            }

            var secret = WebhookSecretBox.Password.Trim();
            if (!string.IsNullOrEmpty(secret))
            {
                await HermesEnvironment.SavePlatformSettingAsync("webhook", "secret", secret);
            }

            SetStatus(WebhookSaveStatus, "Saved to config.yaml.", true);
            WebhookSecretBox.Password = "";
            RefreshWebhookDisplay();
        }
        catch (Exception ex)
        {
            SetStatus(WebhookSaveStatus, $"Error: {ex.Message}", false);
        }
    }

    // =========================================================================
    // Shared helpers
    // =========================================================================

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        HermesEnvironment.OpenLogs();
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        HermesEnvironment.OpenConfig();
    }

    private static string MaskToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "";
        if (token.Length <= 4) return "****";
        return "****" + token[^4..];
    }

    private void SetStatus(TextBlock statusBlock, string message, bool success)
    {
        statusBlock.Text = message;
        statusBlock.Foreground = success
            ? (Brush)Application.Current.Resources["ConnectionOnlineBrush"]
            : (Brush)Application.Current.Resources["ConnectionOfflineBrush"];
    }
}
