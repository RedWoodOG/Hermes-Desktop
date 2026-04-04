using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using HermesDesktop.Diagnostics;
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
        _userBorderBrush = GetBrush("AppAccentBrush");
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

        AppendSystemMessage(string.Format(CultureInfo.CurrentCulture,
            ResourceLoader.GetString("ChatInitialAssistantMessage"),
            "Hermes.C# Workspace", "Qwen3.5"));

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
        ShowThinking(true);

        try
        {
            // Task.Run prevents UI thread deadlock with HttpClient async
            var reply = await Task.Run(() => _chatService.SendAsync(prompt, CancellationToken.None));

            ShowThinking(false);

            if (string.IsNullOrWhiteSpace(reply.Response))
                AppendSystemMessage("LLM returned an empty response.");
            else
                AppendAssistantMessage(reply.Response);

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

    // ── New Chat ──

    private async void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _chatService.ResetConversation();
        Messages.Clear();
        SessionIdLabel.Text = "New Session";

        AppendSystemMessage(string.Format(CultureInfo.CurrentCulture,
            ResourceLoader.GetString("ChatInitialAssistantMessage"),
            "Hermes.C# Workspace", "Qwen3.5"));

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

    private void ShowThinking(bool show)
    {
        ThinkingIndicator.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) MessagesList.ScrollIntoView(Messages.Count > 0 ? Messages[^1] : null);
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
        e.Handled = true;
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

        var accent = GetBrush("AppAccentTextBrush");
        var muted = GetBrush("AppTextSecondaryBrush");
        TabSessions.Foreground = muted;
        TabFiles.Foreground = muted;
        TabSkills.Foreground = muted;
        TabMemory.Foreground = muted;
        TabTasks.Foreground = muted;
        TabBuddy.Foreground = muted;

        switch (tag)
        {
            case "sessions": SessionPanelView.Visibility = Visibility.Visible; TabSessions.Foreground = accent; break;
            case "files": FileBrowserPanelView.Visibility = Visibility.Visible; TabFiles.Foreground = accent; break;
            case "skills": SkillsPanelView.Visibility = Visibility.Visible; TabSkills.Foreground = accent; break;
            case "memory": MemoryPanelView.Visibility = Visibility.Visible; TabMemory.Foreground = accent; break;
            case "tasks": TaskPanelView.Visibility = Visibility.Visible; TabTasks.Foreground = accent; break;
            case "buddy": BuddyPanelView.Visibility = Visibility.Visible; TabBuddy.Foreground = accent; break;
        }
    }

    private static Brush GetBrush(string key) => (Brush)Application.Current.Resources[key];
}
