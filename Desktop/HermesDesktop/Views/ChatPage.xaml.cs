using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hermes.Agent.Core;
using Hermes.Agent.Skills;
using Hermes.Agent.Transcript;
using HermesDesktop.Models;
using HermesDesktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;

namespace HermesDesktop.Views;

public sealed partial class ChatPage : Page
{
    private static readonly ResourceLoader ResourceLoader = new();

    private readonly HermesChatService _chatService = App.Services.GetRequiredService<HermesChatService>();
    private readonly Agent _agent = App.Services.GetRequiredService<Agent>();
    private readonly TranscriptStore _transcriptStore = App.Services.GetRequiredService<TranscriptStore>();
    private readonly SessionRecorder _sessionRecorder = new();
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
    }

    public ObservableCollection<ChatMessageItem> Messages { get; } = new();

    // ── Lifecycle ──

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        ConnectionStateText.Text = ResourceLoader.GetString("ChatStatusChecking");
        SessionIdLabel.Text = "New Session";

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
                catch { /* Screenshot capture is best-effort */ }
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

        AppendWelcomeMessage();

        await RefreshConnectionStatusAsync();
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

            SessionIdLabel.Text = $"Session: {sessionId}";
            ConnectionStateText.Text = ResourceLoader.GetString("StatusConnected");

            // Load activity entries for replay panel
            try
            {
                var activityEntries = await _transcriptStore.LoadActivityAsync(sessionId, CancellationToken.None);
                ReplayPanelView.LoadSession(activityEntries);
            }
            catch
            {
                // Activity log is optional — don't fail the session load
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
            // Shift+Enter → newline
            var pos = PromptTextBox.SelectionStart;
            PromptTextBox.Text = PromptTextBox.Text.Insert(pos, "\r\n");
            PromptTextBox.SelectionStart = pos + 2;
        }
        else
        {
            // Enter → send
            await SendPromptAsync();
        }
        e.Handled = true;
    }

    private async Task SendPromptAsync()
    {
        if (_isBusy) return;
        var prompt = PromptTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt)) return;

        PromptTextBox.Text = "";
        AppendUserMessage(prompt);

        // ── Slash command interception ──
        if (prompt.StartsWith("/", StringComparison.Ordinal))
        {
            await HandleSlashCommandAsync(prompt);
            return;
        }

        SetBusy(true);
        ShowThinking(true, "Hermes is thinking");

        try
        {
            // Don't create the assistant bubble yet — wait for real content.
            // This prevents an empty bubble sitting on screen during the thinking phase.
            ChatMessageItem? assistantItem = null;
            var thinkingBuffer = new System.Text.StringBuilder();
            var hasContent = false;
            var dotCount = 0;
            DispatcherTimer? dotTimer = null;

            // Animate dots in the thinking bar while waiting
            dotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            dotTimer.Tick += (_, _) =>
            {
                dotCount = (dotCount + 1) % 4;
                var phase = assistantItem is null ? "thinking" : "reasoning";
                ThinkingText.Text = $"Hermes is {phase}" + new string('.', dotCount);
            };
            dotTimer.Start();

            // Stream structured events (tokens + thinking)
            await foreach (var evt in _chatService.StreamStructuredAsync(prompt, CancellationToken.None))
            {
                switch (evt.Type)
                {
                    case ChatStreamEventType.Thinking:
                        // Buffer thinking content silently — no bubble yet.
                        // The thinking bar with animated dots is the only visual feedback.
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

            dotTimer.Stop();

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
            if (Messages.Count > 0)
                MessagesList.ScrollIntoView(Messages[^1]);

            ConnectionStateText.Text = "Connected";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowThinking(false);
            ConnectionStateText.Text = "Error";
            AppendSystemMessage($"Error: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            ShowThinking(false);
            AppendSystemMessage("Generation cancelled.");
        }
        finally
        {
            SetBusy(false);
            SessionIdLabel.Text = string.IsNullOrEmpty(_chatService.CurrentSessionId)
                ? "New Session" : $"Session: {_chatService.CurrentSessionId}";
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
            ShowThinking(true, "Running skill...");

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
        SessionIdLabel.Text = "New Session";

        AppendWelcomeMessage();

        await RefreshConnectionStatusAsync();
    }

    // ── Permission Mode ──

    private void PermissionModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // No-op for now — permission mode tracked but not enforced in UI yet
    }

    // ── Connection Check ──

    private async Task RefreshConnectionStatusAsync()
    {
        ConnectionStateText.Text = ResourceLoader.GetString("ChatStatusChecking");
        var (isHealthy, _) = await Task.Run(() => _chatService.CheckHealthAsync(CancellationToken.None));
        ConnectionStateText.Text = ResourceLoader.GetString(isHealthy ? "StatusConnected" : "StatusOffline");
    }

    // ── UI Helpers ──

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        SendButton.IsEnabled = !busy;
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

    private ChatMessageItem AddMessage(string author, string content, HorizontalAlignment align,
        Brush bg, Brush border, Brush label, ChatMessageType type = ChatMessageType.Text)
    {
        var item = new ChatMessageItem(author, content, align, bg, border, label, type);
        Messages.Add(item);
        MessagesList.ScrollIntoView(item);
        return item;
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
        MessagesList.ScrollIntoView(item);
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
        SkillsPanelView.Visibility = Visibility.Collapsed;
        MemoryPanelView.Visibility = Visibility.Collapsed;
        TaskPanelView.Visibility = Visibility.Collapsed;
        BuddyPanelView.Visibility = Visibility.Collapsed;
        ReplayPanelView.Visibility = Visibility.Collapsed;

        var accent = GetBrush("AppAccentTextBrush");
        var muted = GetBrush("AppTextSecondaryBrush");
        TabSessions.Foreground = muted;
        TabFiles.Foreground = muted;
        TabSkills.Foreground = muted;
        TabMemory.Foreground = muted;
        TabTasks.Foreground = muted;
        TabBuddy.Foreground = muted;
        TabReplay.Foreground = muted;

        switch (tag)
        {
            case "sessions": SessionPanelView.Visibility = Visibility.Visible; TabSessions.Foreground = accent; break;
            case "files": FileBrowserPanelView.Visibility = Visibility.Visible; TabFiles.Foreground = accent; break;
            case "skills": SkillsPanelView.Visibility = Visibility.Visible; TabSkills.Foreground = accent; break;
            case "memory": MemoryPanelView.Visibility = Visibility.Visible; TabMemory.Foreground = accent; break;
            case "tasks": TaskPanelView.Visibility = Visibility.Visible; TabTasks.Foreground = accent; break;
            case "buddy": BuddyPanelView.Visibility = Visibility.Visible; TabBuddy.Foreground = accent; break;
            case "replay": ReplayPanelView.Visibility = Visibility.Visible; TabReplay.Foreground = accent; break;
        }
    }

    private static Brush GetBrush(string key) => (Brush)Application.Current.Resources[key];
}
