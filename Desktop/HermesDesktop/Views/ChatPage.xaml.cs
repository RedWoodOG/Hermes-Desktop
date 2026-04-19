using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Hermes.Agent.Permissions;
using Hermes.Agent.Skills;
using Hermes.Agent.Soul;
using Hermes.Agent.Transcript;
using HermesDesktop.Models;
using HermesDesktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.ApplicationModel.DataTransfer;

namespace HermesDesktop.Views;

public sealed partial class ChatPage : Page
{
    private static readonly ResourceLoader ResourceLoader = new();

    private readonly HermesChatService _chatService = App.Services.GetRequiredService<HermesChatService>();
    private readonly RuntimeStatusService _runtimeStatusService = App.Services.GetRequiredService<RuntimeStatusService>();
    private readonly Agent _agent = App.Services.GetRequiredService<Agent>();
    private readonly TranscriptStore _transcriptStore = App.Services.GetRequiredService<TranscriptStore>();
    private readonly SessionRecorder _sessionRecorder = new();
    private readonly SoulService _soulService = App.Services.GetRequiredService<SoulService>();
    private readonly ChatClientFactory _clientFactory = App.Services.GetRequiredService<ChatClientFactory>();
    private bool _suppressModelSwitch;
    private readonly Brush _assistantBackgroundBrush;
    private readonly Brush _assistantBorderBrush;
    private readonly Brush _userBackgroundBrush;
    private readonly Brush _userBorderBrush;
    private readonly Brush _systemBackgroundBrush;
    private readonly Brush _systemBorderBrush;
    private readonly Brush _accentLabelBrush;
    private readonly Brush _secondaryLabelBrush;

    private bool _initialized;
    private bool _isBusy;
    private OnboardingState _onboarding = OnboardingState.None;

    public ChatPage()
    {
        InitializeComponent();

        _assistantBackgroundBrush = GetBrush("AppPanelBrush");
        _assistantBorderBrush = GetBrush("AppStrokeBrush");
        _userBackgroundBrush = GetBrush("AppUserBubbleBrush");
        _userBorderBrush = GetBrush("AppAccentGradientBrush");
        _systemBackgroundBrush = GetBrush("AppInsetBrush");
        _systemBorderBrush = GetBrush("AppSubtleStrokeBrush");
        _accentLabelBrush = GetBrush("AppAccentTextBrush");
        _secondaryLabelBrush = GetBrush("AppTextSecondaryBrush");

        // Set ItemsSource once in code — avoids x:Bind re-evaluation during layout passes
        MessagesList.ItemsSource = Messages;

        // Refresh Sanctum header (title, subtitle, breadcrumb) whenever messages change.
        Messages.CollectionChanged += (_, _) => UpdateHeader();
    }

    public ObservableCollection<ChatMessageItem> Messages { get; } = new();

    // ── Sanctum Header ──

    private static string? _cachedWorkspaceName;
    private static string? _cachedProjectName;

    private void UpdateHeader()
    {
        if (_cachedProjectName is null)
        {
            var workDir = HermesEnvironment.AgentWorkingDirectory;
            _cachedProjectName = System.IO.Path.GetFileName(workDir) ?? "project";
            var parent = System.IO.Path.GetDirectoryName(workDir);
            _cachedWorkspaceName = parent is null ? "" : System.IO.Path.GetFileName(parent) ?? "";
        }

        BreadcrumbWorkspace.Text = _cachedWorkspaceName ?? "";
        BreadcrumbProject.Text = _cachedProjectName ?? "";

        var title = GetJourneyTitle(maxLen: 80);
        var crumbTitle = GetJourneyTitle(maxLen: 28);
        JourneyTitleText.Text = title;
        BreadcrumbJourney.Text = $"\u00AB {crumbTitle} \u00BB";
    }

    private string GetJourneyTitle(int maxLen)
    {
        var userLabel = ResourceLoader.GetString("ChatUserLabel");
        var firstUser = Messages.FirstOrDefault(m =>
            string.Equals(m.AuthorLabel, userLabel, StringComparison.OrdinalIgnoreCase));
        if (firstUser is null || string.IsNullOrWhiteSpace(firstUser.Content))
            return "New Journey";
        var t = firstUser.Content.Trim();
        if (t.Length > maxLen) t = t[..maxLen].TrimEnd() + "\u2026";
        return t;
    }

    // ── Lifecycle ──

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        UpdateHeader();
        ApplyConnectionStatusSnapshot(_runtimeStatusService.GetConfiguredSnapshot());
        UpdateSessionFooterLabel();
        UpdateSessionFooterCopyButton();
        SetPermissionModeUi(_chatService.CurrentPermissionMode, applyToService: false);

        // Wire session panel click → load session into chat
        SessionPanelView.SessionSelected += OnSessionSelected;

        // Wire session panel delete → reset if current session deleted
        SessionPanelView.SessionDeleted += sessionId =>
        {
            if (_chatService.CurrentSessionId == sessionId)
                NewChat_Click(this, new RoutedEventArgs());
        };

        // Wire agent activity tracking → replay panel + screen capture
        _agent.ActivityEntryAdded += async entry =>
        {
            ReplayPanelView.AddActivity(entry);

            // Capture screenshot when recording and tool execution completes
            if (_sessionRecorder.IsRecording && entry.Status != ActivityStatus.Running)
            {
                try
                {
                    // Must dispatch to UI thread for RenderTargetBitmap
                    if (DispatcherQueue.HasThreadAccess)
                    {
                        var path = await _sessionRecorder.CaptureAsync(MainGrid, entry.ToolName);
                        if (path is not null) entry.ScreenshotPath = path;
                    }
                    else
                    {
                        DispatcherQueue.TryEnqueue(async () =>
                        {
                            var path = await _sessionRecorder.CaptureAsync(MainGrid, entry.ToolName);
                            if (path is not null) entry.ScreenshotPath = path;
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ChatPage screenshot capture failed for {entry.ToolName}: {ex}");
                }
            }
        };

        // Wire recording toggle
        ReplayPanelView.RecordingToggled += isRecording =>
        {
            if (isRecording)
            {
                var sessionId = _chatService.CurrentSessionId ?? "unsaved";
                _sessionRecorder.StartRecording(sessionId);
            }
            else
            {
                _sessionRecorder.StopRecording();
            }
        };

        PopulateModelSwitcher();

        if (_soulService.IsFirstRun())
            ShowOnboarding();
        else
            AppendWelcomeMessage();

        await RefreshConnectionStatusAsync();
    }

    // ── Model Switcher ──

    private void PopulateModelSwitcher()
    {
        _suppressModelSwitch = true;

        ModelSwitchCombo.Items.Clear();

        // Always show the user's currently-configured model first
        var currentProvider = HermesEnvironment.ModelProvider;
        var currentModel = HermesEnvironment.DefaultModel;
        var currentBaseUrl = HermesEnvironment.ModelBaseUrl;
        var currentApiKey = HermesEnvironment.ModelApiKey ?? "";
        var currentLabel = $"{currentModel} ({currentProvider})";

        // Build list: current config first, then any additional configured providers
        var items = new List<(string Label, string Provider, string Model, string BaseUrl, string ApiKey)>
        {
            (currentLabel, currentProvider, currentModel, currentBaseUrl, currentApiKey)
        };

        // Only add additional presets if API keys are actually configured for them
        var anthropicKey = HermesEnvironment.ReadConfigSetting("provider_keys", "anthropic") ?? "";
        var openaiKey = HermesEnvironment.ReadConfigSetting("provider_keys", "openai") ?? "";
        var qwenKey = HermesEnvironment.ReadConfigSetting("provider_keys", "qwen") ?? "";
        var ollamaUrl = HermesEnvironment.ReadConfigSetting("provider_keys", "ollama_url") ?? "";

        // Don't duplicate the current model
        if (!string.IsNullOrEmpty(anthropicKey) && !currentProvider.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
            items.Add(("Claude Sonnet 4.6", "anthropic", "claude-sonnet-4-6", "https://api.anthropic.com", anthropicKey));
        if (!string.IsNullOrEmpty(openaiKey) && !currentProvider.Equals("openai", StringComparison.OrdinalIgnoreCase))
            items.Add(("GPT-5.4", "openai", "gpt-5.4", "https://api.openai.com/v1", openaiKey));
        if (!string.IsNullOrEmpty(ollamaUrl) && !currentProvider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
            items.Add(("Ollama (Local)", "ollama", "glm-4.7-flash:latest", ollamaUrl, ""));
        if (!string.IsNullOrEmpty(qwenKey) && !currentProvider.Equals("qwen", StringComparison.OrdinalIgnoreCase))
            items.Add(("Qwen", "qwen", "qwen-plus", "https://dashscope.aliyuncs.com/compatible-mode/v1", qwenKey));

        for (int i = 0; i < items.Count; i++)
        {
            var p = items[i];
            ModelSwitchCombo.Items.Add(new ComboBoxItem
            {
                Content = p.Label,
                Tag = $"{p.Provider}|{p.Model}|{p.BaseUrl}|{p.ApiKey}"
            });
        }

        ModelSwitchCombo.SelectedIndex = 0; // Current config is always first
        _suppressModelSwitch = false;
    }

    private async void ModelSwitchCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelSwitch || ModelSwitchCombo.SelectedItem is not ComboBoxItem item)
            return;

        var tag = item.Tag?.ToString() ?? "";
        var parts = tag.Split('|', 4);
        if (parts.Length < 3) return;

        var provider = parts[0];
        var model = parts[1];
        var baseUrl = parts[2];
        var apiKey = parts.Length > 3 ? parts[3] : "";

        var newConfig = new LlmConfig
        {
            Provider = provider,
            Model = model,
            BaseUrl = baseUrl,
            ApiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey,
            Temperature = 0.7,
            MaxTokens = 4096
        };

        _clientFactory.SwitchProvider(newConfig);

        await RefreshConnectionStatusAsync();
        AppendSystemMessage($"Model switched to **{item.Content}** ({provider}/{model})");
    }

    private async void OnSessionSelected(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            // "+" button — new session
            NewChat_Click(this, new RoutedEventArgs());
            return;
        }

        // Load session from transcript store
        try
        {
            Messages.Clear();
            await _chatService.LoadSessionAsync(sessionId, CancellationToken.None);

            // Replay messages into the UI
            var session = _chatService.CurrentSession;
            if (session is null) return;

            foreach (var msg in session.Messages)
            {
                switch (msg.Role)
                {
                    case "user":
                        AppendUserMessage(msg.Content);
                        break;
                    case "assistant":
                        AppendAssistantMessage(msg.Content);
                        break;
                    case "system":
                        AppendSystemMessage(msg.Content);
                        break;
                }
            }

            ScrollToBottom();
            UpdateSessionFooterLabel();
            UpdateSessionFooterCopyButton();
            ApplyConnectionState(RuntimeConnectionState.Connected);

            // Load activity entries for replay panel
            try
            {
                var activityEntries = await _transcriptStore.LoadActivityAsync(sessionId, CancellationToken.None);
                ReplayPanelView.LoadSession(activityEntries);
            }
            catch (Exception ex)
            {
                // Activity log is optional — don't fail the session load
                System.Diagnostics.Debug.WriteLine($"ChatPage activity replay load failed for {sessionId}: {ex}");
                ReplayPanelView.Clear();
            }
        }
        catch (Exception ex)
        {
            AppendSystemMessage($"Failed to load session: {ex.Message}");
        }
    }

    // ── Send ──

    private async void SendPrompt_Click(object sender, RoutedEventArgs e) => await SendPromptAsync();

    private async void PromptTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;

        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        if (shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            // Shift+Enter → newline (AcceptsReturn handles this natively)
            return;
        }

        // Enter → send (prevent the newline from being inserted)
        e.Handled = true;
        await SendPromptAsync();
    }

    private void PromptTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SendButton.IsEnabled = !string.IsNullOrWhiteSpace(PromptTextBox.Text) && !_isBusy;
    }

    private async Task SendPromptAsync()
    {
        if (_isBusy) return;
        var prompt = PromptTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt)) return;

        PromptTextBox.Text = "";
        AppendUserMessage(prompt);
        ScrollToBottom();

        // ── Slash command interception ──
        if (prompt.StartsWith("/", StringComparison.Ordinal))
        {
            await HandleSlashCommandAsync(prompt);
            return;
        }

        // ── Onboarding interception ──
        if (_onboarding != OnboardingState.None)
        {
            await HandleOnboardingInputAsync(prompt);
            return;
        }

        SetBusy(true);
        ShowThinking(true, ResourceLoader.GetString("ChatThinkingShort"));

        // Dot timer declared outside try so finally can always stop it
        var dotCount = 0;
        ChatMessageItem? assistantItem = null;
        var dotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        dotTimer.Tick += (_, _) =>
        {
            dotCount = (dotCount + 1) % 4;
            var line = assistantItem is null
                ? ResourceLoader.GetString("ChatThinkingLineThinking")
                : ResourceLoader.GetString("ChatThinkingLineReasoning");
            ThinkingText.Text = line + new string('.', dotCount);
        };
        dotTimer.Start();

        try
        {
            // Don't create the assistant bubble yet — wait for real content.
            // This prevents an empty bubble sitting on screen during the thinking phase.
            var thinkingBuffer = new System.Text.StringBuilder();
            var hasContent = false;

            // Stream structured events (tokens + thinking)
            await foreach (var evt in _chatService.StreamStructuredAsync(prompt, CancellationToken.None))
            {
                switch (evt.Type)
                {
                    case ChatStreamEventType.Thinking:
                        if (assistantItem is not null)
                            assistantItem.AppendThinking(evt.Text);
                        thinkingBuffer.Append(evt.Text);
                        break;

                    case ChatStreamEventType.Token:
                        if (!hasContent)
                        {
                            hasContent = true;

                            // NOW create the bubble — user sees it appear with content, not empty
                            assistantItem = AddMessage(
                                ResourceLoader.GetString("ChatAssistantLabel"), "",
                                HorizontalAlignment.Left,
                                _assistantBackgroundBrush, _assistantBorderBrush, _secondaryLabelBrush);
                            assistantItem.IsStreaming = true;

                            // Attach buffered thinking content if any
                            if (thinkingBuffer.Length > 0)
                                assistantItem.ThinkingContent = thinkingBuffer.ToString();

                            ShowThinking(false);
                        }
                        assistantItem!.AppendToken(evt.Text);
                        break;

                    case ChatStreamEventType.Error:
                        AppendSystemMessage($"Stream error: {evt.Text}");
                        break;
                }
            }

            if (assistantItem is not null)
            {
                assistantItem.IsStreaming = false;
            }

            if (!hasContent)
            {
                // Stream completed without producing any content tokens.
                // Don't fall back to SendAsync — StreamStructuredAsync already
                // saved the user message to the session, so SendAsync would
                // duplicate it and corrupt the conversation history.
                ShowThinking(false);
                if (thinkingBuffer.Length > 0)
                    AppendSystemMessage("Model produced only reasoning with no response. Try again or use a different model.");
                else
                    AppendSystemMessage("LLM returned an empty response.");
            }

            // Scroll to the final message
            ScrollToBottom();

            ApplyConnectionState(RuntimeConnectionState.Connected);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowThinking(false);
            ApplyConnectionState(RuntimeConnectionState.Error);
            AppendSystemMessage($"Error: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            ShowThinking(false);
            AppendSystemMessage("Generation cancelled.");
        }
        finally
        {
            if (assistantItem is not null)
                assistantItem.IsStreaming = false;
            dotTimer.Stop();
            SetBusy(false);
            UpdateSessionFooterLabel();
            UpdateSessionFooterCopyButton();
            PromptTextBox.Focus(FocusState.Programmatic);
        }
    }

    // ── Slash Commands ──

    private async Task HandleSlashCommandAsync(string input)
    {
        var parts = input.TrimStart('/').Split(' ', 2);
        var command = parts[0].ToLowerInvariant();
        var args = parts.Length > 1 ? parts[1] : "";

        if (command is "help" or "skills")
        {
            var skillManager = App.Services.GetRequiredService<SkillManager>();
            var skills = skillManager.ListSkills();
            var lines = new System.Text.StringBuilder();
            lines.AppendLine("Available slash commands:");
            lines.AppendLine("  /help, /skills  - Show this help");
            lines.AppendLine("  /new            - Start a new chat");
            lines.AppendLine();
            if (skills.Count > 0)
            {
                lines.AppendLine("Installed skills:");
                foreach (var s in skills)
                    lines.AppendLine($"  /{s.Name}  - {s.Description}");
            }
            else
            {
                lines.AppendLine("No custom skills installed. Add .md files to your skills directory.");
            }
            AppendSystemMessage(lines.ToString());
            return;
        }

        if (command == "new")
        {
            NewChat_Click(this, new RoutedEventArgs());
            return;
        }

        // Try to invoke as a skill
        try
        {
            var invoker = App.Services.GetRequiredService<SkillInvoker>();
            SetBusy(true);
            ShowThinking(true, ResourceLoader.GetString("ChatThinkingRunningSkill"));

            var response = await invoker.InvokeAsync(command, args, CancellationToken.None);
            ShowThinking(false);

            if (!string.IsNullOrWhiteSpace(response))
                AppendAssistantMessage(response);
            else
                AppendSystemMessage("Skill returned an empty response.");
        }
        catch (SkillNotFoundException)
        {
            AppendSystemMessage($"Unknown command: /{command}. Type /help for available commands.");
        }
        catch (Exception ex)
        {
            ShowThinking(false);
            AppendSystemMessage($"Skill error: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            PromptTextBox.Focus(FocusState.Programmatic);
        }
    }

    // ── Stop Generation ──

    private void StopGeneration_Click(object sender, RoutedEventArgs e)
    {
        _chatService.CancelStream();
    }

    // ── New Chat ──

    private async void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _chatService.ResetConversation();
        _agent.ClearActivityLog();
        ReplayPanelView.Clear();
        _sessionRecorder.StopRecording();
        Messages.Clear();
        UpdateSessionFooterLabel();
        UpdateSessionFooterCopyButton();
        _onboarding = OnboardingState.None;

        if (_soulService.IsFirstRun())
            ShowOnboarding();
        else
            AppendWelcomeMessage();

        await RefreshConnectionStatusAsync();
    }

    // ── Permission Mode ──

    private static readonly PermissionMode[] PermissionModeMenuOrder =
    [
        PermissionMode.Default,
        PermissionMode.Plan,
        PermissionMode.Auto,
        PermissionMode.AcceptEdits,
        PermissionMode.BypassPermissions,
    ];

    private void PermissionModeToggle_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        var current = _chatService.CurrentPermissionMode;
        foreach (var pm in PermissionModeMenuOrder)
        {
            var item = new MenuFlyoutItem { Text = GetPermissionModeDisplayName(pm), Tag = pm };
            if (pm == current)
                item.Icon = new FontIcon { Glyph = "\uE73E" }; // checkmark
            var captured = pm;
            item.Click += (_, _) => SetPermissionModeUi(captured);
            flyout.Items.Add(item);
        }
        flyout.ShowAt((FrameworkElement)sender);
    }

    private void SetPermissionModeUi(PermissionMode mode, bool applyToService = true)
    {
        PermissionModeLabel.Text = string.Format(
            CultureInfo.CurrentCulture,
            ResourceLoader.GetString("ChatPermissionModeFormat"),
            GetPermissionModeDisplayName(mode));
        if (applyToService)
            _chatService.SetPermissionMode(mode);
    }

    private string GetPermissionModeDisplayName(PermissionMode mode) => mode switch
    {
        PermissionMode.Plan => ResourceLoader.GetString("ChatPermissionModeNamePlan"),
        PermissionMode.Auto => ResourceLoader.GetString("ChatPermissionModeNameAuto"),
        PermissionMode.AcceptEdits => ResourceLoader.GetString("ChatPermissionModeNameAcceptEdits"),
        PermissionMode.BypassPermissions => ResourceLoader.GetString("ChatPermissionModeNameBypass"),
        _ => ResourceLoader.GetString("ChatPermissionModeNameDefault"),
    };

    private void UpdateSessionFooterLabel()
    {
        SessionIdLabel.Text = string.IsNullOrEmpty(_chatService.CurrentSessionId)
            ? ResourceLoader.GetString("ChatSessionFooterNew")
            : string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("ChatSessionFooterFormat"), _chatService.CurrentSessionId);
    }

    // ── Connection Check ──

    private async Task RefreshConnectionStatusAsync()
    {
        ApplyConnectionStatusSnapshot(_runtimeStatusService.GetConfiguredSnapshot());
        var snapshot = await _runtimeStatusService.RefreshAsync(CancellationToken.None);
        ApplyConnectionStatusSnapshot(snapshot);
    }

    private void ApplyConnectionState(RuntimeConnectionState state)
    {
        var snapshot = _runtimeStatusService.GetConfiguredSnapshot() with { ConnectionState = state };
        ApplyConnectionStatusSnapshot(snapshot);
    }

    private void ApplyConnectionStatusSnapshot(RuntimeStatusSnapshot snapshot)
    {
        var statusText = snapshot.ConnectionState switch
        {
            RuntimeConnectionState.Connected => ResourceLoader.GetString("StatusConnected"),
            RuntimeConnectionState.Checking => ResourceLoader.GetString("ChatStatusChecking"),
            _ => ResourceLoader.GetString("StatusOffline"),
        };

        ConnectionStateText.Text = $"{statusText} | {snapshot.DisplayProvider} | {snapshot.DisplayModel}";

        // Sanctum subtitle: model  \u00B7  N turns  \u00B7  state
        var userLabel = ResourceLoader.GetString("ChatUserLabel");
        var turns = Messages.Count(m =>
            string.Equals(m.AuthorLabel, userLabel, StringComparison.OrdinalIgnoreCase));
        JourneySubtitleText.Text = $"{snapshot.DisplayModel}  \u00B7  {turns} turns  \u00B7  {statusText}";
    }

    private void UpdateSessionFooterCopyButton()
    {
        var id = _chatService.CurrentSessionId;
        CopySessionIdButton.IsEnabled = !string.IsNullOrEmpty(id);
    }

    private void CopySessionId_Click(object sender, RoutedEventArgs e)
    {
        var id = _chatService.CurrentSessionId;
        if (string.IsNullOrEmpty(id)) return;

        var package = new DataPackage();
        package.SetText(id);
        Clipboard.SetContent(package);
    }

    // ── UI Helpers ──

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        SendButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(PromptTextBox.Text);
        PromptTextBox.IsEnabled = !busy;
    }

    private void ShowThinking(bool show, string? label = null)
    {
        ThinkingIndicator.Opacity = show ? 1.0 : 0.0;
        ThinkingRing.IsActive = show;
        if (label is not null)
            ThinkingText.Text = label;
    }

    private void AppendUserMessage(string text) =>
        AddMessage(ResourceLoader.GetString("ChatUserLabel"), text, HorizontalAlignment.Right,
            _userBackgroundBrush, _userBorderBrush, _accentLabelBrush);

    private void AppendAssistantMessage(string text) =>
        AddMessage(ResourceLoader.GetString("ChatAssistantLabel"), text, HorizontalAlignment.Left,
            _assistantBackgroundBrush, _assistantBorderBrush, _secondaryLabelBrush);

    private void AppendSystemMessage(string text) =>
        AddMessage(ResourceLoader.GetString("ChatSystemLabel"), text, HorizontalAlignment.Left,
            _systemBackgroundBrush, _systemBorderBrush, _secondaryLabelBrush);

    private void AppendWelcomeMessage()
    {
        var caduceus =
            "            ⠀⠀⠀⢀⣀⡀⠀⣀⣀⠀⢀⣀⡀\n" +
            "            ⢀⣠⣴⣾⣿⣿⣇⠸⣿⣿⠇⣸⣿⣿⣷⣦⣄⡀\n" +
            "       ⢀⣠⣴⣶⠿⠋⣩⡿⣿⡿⠻⣿⡇⢠⡄⢸⣿⠟⢿⣿⢿⣍⠙⠿⣶⣦⣄⡀\n" +
            "       ⠀⠉⠉⠁⠶⠟⠋⠀⠉⠀⢀⣈⣁⡈⢁⣈⣁⡀⠀⠉⠀⠙⠻⠶⠈⠉⠉\n" +
            "            ⠀⠀⠀⠀⠀⠀⣴⣿⡿⠛⢁⡈⠛⢿⣿⣦\n" +
            "            ⠀⠀⠀⠀⠀⠀⠿⣿⣦⣤⣈⠁⢠⣴⣿⠿\n" +
            "            ⠀⠀⠀⠀⠀⠀⠀⠈⠉⠻⢿⣿⣦⡉⠁\n" +
            "            ⠀⠀⠀⠀⠀⠀⠀⠀⠘⢷⣦⣈⠛⠃\n" +
            "            ⠀⠀⠀⠀⠀⠀⢠⣴⠦⠈⠙⠿⣦⡄\n" +
            "            ⠀⠀⠀⠀⠀⠀⠸⣿⣤⡈⠁⢤⣿⠇\n" +
            "            ⠀⠀⠀⠀⠀⠀⠀⠀⠉⠛⠷⠄\n" +
            "            ⠀⠀⠀⠀⠀⠀⠀⢀⣀⠑⢶⣄⡀\n" +
            "            ⠀⠀⠀⠀⠀⠀⠀⣿⠁⢰⡆⠈⡿\n" +
            "            ⠀⠀⠀⠀⠀⠀⠀⠈⠳⠈⣡⠞⠁\n\n" +
            "         H E R M E S   A G E N T\n\n" +
            "  Ready. Type a message or /help for commands.";

        AppendSystemMessage(caduceus);
    }

    // ── First-Run Onboarding ──

    private void ShowOnboarding()
    {
        // Don't use chat-based onboarding — point to the Agent tab instead
        _onboarding = OnboardingState.None;

        AppendWelcomeMessage();
        AppendAssistantMessage(
            "Welcome! This is your first time here.\n\n" +
            "I'm Hermes — your AI agent. Before we start, you can set up my identity " +
            "and tell me about yourself.\n\n" +
            "Click the Agent tab in the right panel to:\n" +
            "  - Browse soul templates (12 different personalities)\n" +
            "  - Customize my identity, values, and working style\n" +
            "  - Tell me about you so I can be a better assistant\n" +
            "  - Create multiple agent configurations\n\n" +
            "Or just start chatting — I work great with my default soul too.");

        // Mark as configured so onboarding doesn't repeat
        _ = MarkConfiguredAsync();
    }

    private async System.Threading.Tasks.Task MarkConfiguredAsync()
    {
        try
        {
            var current = await _soulService.LoadFileAsync(SoulFileType.Soul);
            if (current.Contains("<!-- UNCONFIGURED -->"))
                await _soulService.SaveFileAsync(SoulFileType.Soul, current.Replace("<!-- UNCONFIGURED -->\n", ""));

            var userCurrent = await _soulService.LoadFileAsync(SoulFileType.User);
            if (userCurrent.Contains("<!-- UNCONFIGURED -->"))
                await _soulService.SaveFileAsync(SoulFileType.User, userCurrent.Replace("<!-- UNCONFIGURED -->\n", ""));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ChatPage onboarding configuration marker update failed: {ex}");
        }
    }

    private async Task HandleOnboardingInputAsync(string input)
    {
        if (_onboarding == OnboardingState.AwaitingSoulInput)
        {
            if (!string.Equals(input, "default", StringComparison.OrdinalIgnoreCase))
            {
                // Use the LLM to generate a personalized SOUL.md based on user preferences
                SetBusy(true);
                ShowThinking(true);

                try
                {
                    var prompt = $@"The user wants to customize their AI agent's identity. Based on their preferences below, generate a SOUL.md document in markdown format. Include sections for: Core Identity, Values, Communication Style, and Working Style. Be specific and personal, not generic.

User's preferences: {input}

Write the SOUL.md content now (markdown format, start with # Hermes Agent Identity):";

                    var reply = await Task.Run(() => _chatService.SendAsync(prompt, CancellationToken.None));
                    ShowThinking(false);

                    if (!string.IsNullOrWhiteSpace(reply.Response))
                    {
                        await _soulService.SaveFileAsync(SoulFileType.Soul, reply.Response);
                        AppendAssistantMessage("I've saved your customized soul. Here's who I'll be:\n\n" +
                            reply.Response.Substring(0, Math.Min(500, reply.Response.Length)) +
                            (reply.Response.Length > 500 ? "\n\n...(saved in full to SOUL.md)" : ""));
                    }
                }
                catch (Exception ex)
                {
                    ShowThinking(false);
                    AppendSystemMessage($"Error generating soul: {ex.Message}");
                }
                finally
                {
                    SetBusy(false);
                }
            }
            else
            {
                // Remove the UNCONFIGURED marker from the default template
                var current = await _soulService.LoadFileAsync(SoulFileType.Soul);
                await _soulService.SaveFileAsync(SoulFileType.Soul, current.Replace("<!-- UNCONFIGURED -->\n", ""));
                AppendAssistantMessage("Keeping my default soul. I'm direct, honest, and action-oriented.");
            }

            // Move to user profile phase
            _onboarding = OnboardingState.AwaitingUserInput;
            AppendAssistantMessage(
                "Now, tell me about you.\n\n" +
                "I persist across sessions through memory files, so what you share here helps me be a better agent for you every time.\n\n" +
                "  - What's your name and what do you do?\n" +
                "  - What's your technical skill level?\n" +
                "  - How do you prefer to communicate? (detailed explanations? just the answer?)\n" +
                "  - Anything else I should know about working with you?\n\n" +
                "Or say \"skip\" to fill this in later.");
            return;
        }

        if (_onboarding == OnboardingState.AwaitingUserInput)
        {
            if (!string.Equals(input, "skip", StringComparison.OrdinalIgnoreCase))
            {
                SetBusy(true);
                ShowThinking(true);

                try
                {
                    var prompt = $@"The user has described themselves for their AI agent's USER.md profile. Generate a structured user profile in markdown format based on what they shared. Include sections: Who They Are, Technical Expertise, How They Work, What I've Learned.

User's description: {input}

Write the USER.md content now (markdown format, start with # User Profile):";

                    var reply = await Task.Run(() => _chatService.SendAsync(prompt, CancellationToken.None));
                    ShowThinking(false);

                    if (!string.IsNullOrWhiteSpace(reply.Response))
                    {
                        await _soulService.SaveFileAsync(SoulFileType.User, reply.Response);
                        AppendAssistantMessage("Got it. I've saved your profile. I'll remember this across sessions.\n\n" +
                            "You can always update your profile in the Memory tab > Soul.\n\n" +
                            "Setup complete. I'm ready to work. What would you like to do?");
                    }
                }
                catch (Exception ex)
                {
                    ShowThinking(false);
                    AppendSystemMessage($"Error saving profile: {ex.Message}");
                }
                finally
                {
                    SetBusy(false);
                }
            }
            else
            {
                var current = await _soulService.LoadFileAsync(SoulFileType.User);
                await _soulService.SaveFileAsync(SoulFileType.User, current.Replace("<!-- UNCONFIGURED -->\n", ""));
                AppendAssistantMessage("No problem — you can fill in your profile anytime in the Memory tab > Soul.\n\n" +
                    "Setup complete. I'm ready to work. What would you like to do?");
            }

            _onboarding = OnboardingState.None;
            return;
        }
    }

    private ChatMessageItem AddMessage(string author, string content, HorizontalAlignment align,
        Brush bg, Brush border, Brush label, ChatMessageType type = ChatMessageType.Text)
    {
        var item = new ChatMessageItem(author, content, align, bg, border, label, type);
        Messages.Add(item);
        return item;
    }

    private void ScrollToBottom()
    {
        if (Messages.Count > 0)
            MessagesList.ScrollIntoView(Messages[^1]);
    }

    // ── Typewriter Replay ──

    private async Task ReplayResponseAsync(string fullText)
    {
        var item = AddMessage(ResourceLoader.GetString("ChatAssistantLabel"), "",
            HorizontalAlignment.Left, _assistantBackgroundBrush, _assistantBorderBrush, _secondaryLabelBrush);
        item.IsStreaming = true;

        var i = 0;
        while (i < fullText.Length)
        {
            var chunk = Math.Min(3, fullText.Length - i);
            while (i + chunk < fullText.Length && chunk < 12 && fullText[i + chunk] != ' ' && fullText[i + chunk] != '\n')
                chunk++;

            item.AppendToken(fullText.Substring(i, chunk));
            i += chunk;
            await Task.Delay(8);
        }

        item.IsStreaming = false;
        ScrollToBottom();
    }

    // ── Panel Splitter ──

    private bool _isDragging;

    private void Splitter_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isDragging = true;
        ((UIElement)sender).CapturePointer(e.Pointer);
        SplitterHandle.Opacity = 1.0;
        SplitterHandle.Background = (Brush)Application.Current.Resources["AppAccentBrush"];
        e.Handled = true;
    }

    private void Splitter_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        var col = MainGrid.ColumnDefinitions[2];
        var pos = e.GetCurrentPoint(MainGrid).Position.X;
        var newWidth = Math.Clamp(MainGrid.ActualWidth - pos, col.MinWidth, col.MaxWidth);
        col.Width = new GridLength(newWidth);
        e.Handled = true;
    }

    private void Splitter_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isDragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        SplitterHandle.Opacity = 0.5;
        SplitterHandle.Background = (Brush)Application.Current.Resources["AppStrokeBrush"];
        e.Handled = true;
    }

    private void Splitter_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            SplitterHandle.Opacity = 0.8;
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
        }
    }

    private void Splitter_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            SplitterHandle.Opacity = 0.5;
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        }
    }

    // ── Panel Tabs ──

    private void PanelTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;

        SessionPanelView.Visibility = Visibility.Collapsed;
        FileBrowserPanelView.Visibility = Visibility.Collapsed;
        TaskPanelView.Visibility = Visibility.Collapsed;
        ReplayPanelView.Visibility = Visibility.Collapsed;

        var accent = GetBrush("AppAccentTextBrush");
        var muted = GetBrush("AppTextSecondaryBrush");
        TabSessions.Foreground = muted;
        TabFiles.Foreground = muted;
        TabTasks.Foreground = muted;
        TabReplay.Foreground = muted;

        switch (tag)
        {
            case "sessions": SessionPanelView.Visibility = Visibility.Visible; TabSessions.Foreground = accent; break;
            case "files": FileBrowserPanelView.Visibility = Visibility.Visible; TabFiles.Foreground = accent; break;
            case "tasks": TaskPanelView.Visibility = Visibility.Visible; TabTasks.Foreground = accent; break;
            case "replay": ReplayPanelView.Visibility = Visibility.Visible; TabReplay.Foreground = accent; break;
        }
    }

    private static Brush GetBrush(string key) => (Brush)Application.Current.Resources[key];
}

/// <summary>Onboarding state machine for first-run soul setup.</summary>
internal enum OnboardingState
{
    None,
    AwaitingSoulInput,
    AwaitingUserInput
}
