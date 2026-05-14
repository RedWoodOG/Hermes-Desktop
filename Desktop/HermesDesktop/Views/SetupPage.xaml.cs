using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HermesDesktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HermesDesktop.Views;

/// <summary>
/// Bundle E.8 — first-run provider/model setup. Hands a minimal preset list,
/// writes the result via <see cref="HermesEnvironment.SaveConfigSectionAsync"/>,
/// and marks first-run complete on success.
/// </summary>
public sealed partial class SetupPage : Page
{
    private static readonly IReadOnlyDictionary<string, (string Provider, string BaseUrl, string ModelHint)> Presets =
        new Dictionary<string, (string, string, string)>
        {
            ["lmstudio"] = ("custom", "http://localhost:1234/v1", ""),
            ["ollama"]   = ("custom", "http://localhost:11434/v1", "llama3.3"),
            ["vllm"]     = ("custom", "http://localhost:8000/v1", ""),
            ["llamacpp"] = ("custom", "http://localhost:8080/v1", ""),
            ["openai"]   = ("openai", "https://api.openai.com/v1", "gpt-5.4-mini"),
            ["anthropic"]= ("anthropic", "https://api.anthropic.com", "claude-haiku-4.5"),
            ["custom"]   = ("custom", "", ""),
        };

    private string _selectedProvider = "custom";

    public SetupPage()
    {
        InitializeComponent();
        PresetCombo.SelectedIndex = 0;
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!Presets.TryGetValue(tag, out var preset)) return;

        _selectedProvider = preset.Provider;
        BaseUrlBox.Text = preset.BaseUrl;
        if (!string.IsNullOrEmpty(preset.ModelHint))
            ModelIdBox.Text = preset.ModelHint;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var baseUrl = (BaseUrlBox.Text ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            SetTestStatus("Enter a base URL first.", success: false);
            return;
        }

        TestConnectionButton.IsEnabled = false;
        SetTestStatus("Probing...", success: null);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var probeUrl = _selectedProvider == "anthropic"
                ? baseUrl + "/v1/messages"
                : baseUrl + "/models";
            using var request = new HttpRequestMessage(HttpMethod.Get, probeUrl);
            var apiKey = ApiKeyBox.Password;
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if ((int)response.StatusCode is >= 200 and < 500)
                SetTestStatus($"Reachable ({(int)response.StatusCode}).", success: true);
            else
                SetTestStatus($"Endpoint returned {(int)response.StatusCode}.", success: false);
        }
        catch (Exception ex)
        {
            SetTestStatus($"Unreachable: {ex.Message}", success: false);
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }

    private async void Finish_Click(object sender, RoutedEventArgs e)
    {
        var baseUrl = (BaseUrlBox.Text ?? string.Empty).Trim();
        var modelId = (ModelIdBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(modelId))
        {
            SetTestStatus("Base URL and model ID are required.", success: false);
            return;
        }

        FinishButton.IsEnabled = false;
        try
        {
            var settings = new Dictionary<string, string>
            {
                ["provider"] = _selectedProvider,
                ["base_url"] = baseUrl,
                ["default"]  = modelId,
            };

            var apiKey = ApiKeyBox.Password;
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                settings["auth_mode"] = "api_key";
                settings["api_key"]   = apiKey;
            }
            else
            {
                settings["auth_mode"] = "api_key";
            }

            await HermesEnvironment.SaveConfigSectionAsync("model", settings);

            if (App.Current is App app && app.MainWindow is { } window)
            {
                // Await the marker-strip BEFORE navigating so ChatPage's IsFirstRun()
                // re-read sees the cleared state and does not loop back to onboarding.
                await window.MarkFirstRunCompleteAsync();
                window.NavigateToTag("chat");
            }
        }
        catch (Exception ex)
        {
            SetTestStatus($"Save failed: {ex.Message}", success: false);
        }
        finally
        {
            FinishButton.IsEnabled = true;
        }
    }

    private async void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is App app && app.MainWindow is { } window)
        {
            await window.MarkFirstRunCompleteAsync();
            window.NavigateToTag("chat");
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is App app && app.MainWindow is { } window)
            window.NavigateToTag("welcome");
    }

    private void SetTestStatus(string message, bool? success)
    {
        TestStatusText.Text = message;
        TestStatusText.Foreground = success switch
        {
            true => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x60, 0xE0, 0x90)),
            false => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0x70, 0x70)),
            _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xC0, 0xC0, 0xC0)),
        };
    }
}
