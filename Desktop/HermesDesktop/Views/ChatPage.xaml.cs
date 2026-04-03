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
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
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
    private string _connectionState;
    private string _composerStatus;
    private string _permissionMode;

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

        _connectionState = ResourceLoader.GetString("ChatStatusChecking");
        _composerStatus = ResourceLoader.GetString("ChatComposerHint");
        _permissionMode = "Default";
    }

    public ObservableCollection<ChatMessageItem> Messages { get; } = new();

    public string ConnectionState => _connectionState;
    public string ComposerStatus => _composerStatus;
    public string PermissionMode => _permissionMode;
    public string SessionIdText => string.IsNullOrEmpty(_sessionId) ? "New Session" : $"Session: {_sessionId}";
    public string MessageCountSummary => $"{Messages.Count} messages";

    private string _sessionId => _chatService.CurrentSessionId ?? "";

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        AppendSystemMessage(
            string.Format(
                CultureInfo.CurrentCulture,
                ResourceLoader.GetString("ChatInitialAssistantMessage"),
                "Hermes.C# Workspace",
                "Qwen3.5"));
        
        await RefreshConnectionStatusAsync();
        await RefreshUiDataAsync();
    }

    private async void SendPrompt_Click(object sender, RoutedEventArgs e)
    {
        await SendPromptAsync();
    }

    private async void SendKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SendPromptAsync();
    }

    private void PermissionModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PermissionModeSelector.SelectedItem is ComboBoxItem item && item.Tag is string modeTag)
        {
            #region agent log
            DesktopDebugLog.Write("H2", "ChatPage.xaml.cs:PermissionModeSelector_SelectionChanged", "combo_changed",
                new { modeTag, invokedSetPermissionModeOnService = false });
            #endregion
            _permissionMode = modeTag;
            Bindings.Update();
        }
    }

    private async void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _chatService.ResetConversation();
        Messages.Clear();
        
        AppendSystemMessage(
            string.Format(
                CultureInfo.CurrentCulture,
                ResourceLoader.GetString("ChatInitialAssistantMessage"),
                "Hermes.C# Workspace",
                "Qwen3.5"));
        
        _composerStatus = ResourceLoader.GetString("ChatComposerHint");
        await RefreshConnectionStatusAsync();
        Bindings.Update();
    }

    private async Task SendPromptAsync()
    {
        if (_isBusy) return;

        string prompt = PromptTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt)) return;

        #region agent log
        DesktopDebugLog.Write("H1", "ChatPage.xaml.cs:SendPromptAsync", "ui_send_uses_stream_path",
            new { promptLength = prompt.Length });
        #endregion
        PromptTextBox.Text = string.Empty;
        AppendUserMessage(prompt);
        SetBusyState(true, ResourceLoader.GetString("ChatComposerWaiting"));

        try
        {
            // Create streaming placeholder
            var streamingItem = AddVisualMessage(
                ResourceLoader.GetString("ChatAssistantLabel"),
                "",
                HorizontalAlignment.Left,
                _assistantBackgroundBrush,
                _assistantBorderBrush,
                _secondaryLabelBrush);
            streamingItem.IsStreaming = true;

            // Stream tokens
            await foreach (var token in _chatService.StreamAsync(prompt, CancellationToken.None))
            {
                streamingItem.AppendToken(token);
                MessagesList.ScrollIntoView(streamingItem);
            }

            streamingItem.IsStreaming = false;
            _connectionState = ResourceLoader.GetString("StatusConnected");
            _composerStatus = ResourceLoader.GetString("ChatComposerReady");
        }
        catch (OperationCanceledException)
        {
            AppendSystemMessage("Generation cancelled.");
        }
        catch (Exception ex)
        {
            _connectionState = ResourceLoader.GetString("StatusOffline");
            _composerStatus = ResourceLoader.GetString("ChatComposerOffline");
            AppendSystemMessage(
                string.Format(
                    CultureInfo.CurrentCulture,
                    ResourceLoader.GetString("ChatErrorMessageFormat"),
                    ex.Message));
        }
        finally
        {
            SetBusyState(false, _composerStatus);
            Bindings.Update();
            PromptTextBox.Focus(FocusState.Programmatic);
        }
    }

    private async Task RefreshConnectionStatusAsync()
    {
        _connectionState = ResourceLoader.GetString("ChatStatusChecking");
        Bindings.Update();

        (bool isHealthy, string detail) = await _chatService.CheckHealthAsync(CancellationToken.None);
        _connectionState = ResourceLoader.GetString(isHealthy ? "StatusConnected" : "StatusOffline");
        
        if (!_isBusy)
        {
            _composerStatus = ResourceLoader.GetString(
                isHealthy ? "ChatComposerReady" : "ChatComposerOffline");
        }

        Bindings.Update();
    }

    private Task RefreshUiDataAsync()
    {
        // Placeholder for future pillar integration
        return Task.CompletedTask;
    }

    private void SetBusyState(bool isBusy, string statusText)
    {
        _isBusy = isBusy;
        _composerStatus = statusText;
        SendButton.IsEnabled = !isBusy;
        PromptTextBox.IsEnabled = !isBusy;
        Bindings.Update();
    }

    private void AppendUserMessage(string message)
    {
        AddVisualMessage(
            ResourceLoader.GetString("ChatUserLabel"),
            message,
            HorizontalAlignment.Right,
            _userBackgroundBrush,
            _userBorderBrush,
            _accentLabelBrush);
    }

    private void AppendAssistantMessage(string message)
    {
        AddVisualMessage(
            ResourceLoader.GetString("ChatAssistantLabel"),
            message,
            HorizontalAlignment.Left,
            _assistantBackgroundBrush,
            _assistantBorderBrush,
            _secondaryLabelBrush);
    }

    private void AppendSystemMessage(string message)
    {
        AddVisualMessage(
            ResourceLoader.GetString("ChatSystemLabel"),
            message,
            HorizontalAlignment.Left,
            _systemBackgroundBrush,
            _systemBorderBrush,
            _secondaryLabelBrush);
    }

    private ChatMessageItem AddVisualMessage(
        string authorLabel,
        string content,
        HorizontalAlignment alignment,
        Brush background,
        Brush borderBrush,
        Brush labelBrush,
        ChatMessageType messageType = ChatMessageType.Text,
        List<ToolCallInfo>? toolCalls = null)
    {
        var item = new ChatMessageItem(authorLabel, content, alignment, background, borderBrush, labelBrush, messageType, toolCalls);
        Messages.Add(item);
        MessagesList.ScrollIntoView(item);
        Bindings.Update();
        return item;
    }

    // ── Panel Tab Switching ──

    private string _activeTab = "sessions";

    private void PanelTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _activeTab = tag;

            // Hide all panels
            SessionPanelView.Visibility = Visibility.Collapsed;
            FileBrowserPanelView.Visibility = Visibility.Collapsed;
            SkillsPanelView.Visibility = Visibility.Collapsed;
            MemoryPanelView.Visibility = Visibility.Collapsed;
            TaskPanelView.Visibility = Visibility.Collapsed;
            BuddyPanelView.Visibility = Visibility.Collapsed;

            // Show selected panel
            var accentBrush = GetBrush("AppAccentTextBrush");
            var mutedBrush = GetBrush("AppTextSecondaryBrush");

            TabSessions.Foreground = mutedBrush;
            TabFiles.Foreground = mutedBrush;
            TabSkills.Foreground = mutedBrush;
            TabMemory.Foreground = mutedBrush;
            TabTasks.Foreground = mutedBrush;
            TabBuddy.Foreground = mutedBrush;

            switch (tag)
            {
                case "sessions":
                    SessionPanelView.Visibility = Visibility.Visible;
                    TabSessions.Foreground = accentBrush;
                    break;
                case "files":
                    FileBrowserPanelView.Visibility = Visibility.Visible;
                    TabFiles.Foreground = accentBrush;
                    break;
                case "skills":
                    SkillsPanelView.Visibility = Visibility.Visible;
                    TabSkills.Foreground = accentBrush;
                    break;
                case "memory":
                    MemoryPanelView.Visibility = Visibility.Visible;
                    TabMemory.Foreground = accentBrush;
                    break;
                case "tasks":
                    TaskPanelView.Visibility = Visibility.Visible;
                    TabTasks.Foreground = accentBrush;
                    break;
                case "buddy":
                    BuddyPanelView.Visibility = Visibility.Visible;
                    TabBuddy.Foreground = accentBrush;
                    break;
            }
        }
    }

    private static Brush GetBrush(string resourceKey)
    {
        return (Brush)Application.Current.Resources[resourceKey];
    }
}
