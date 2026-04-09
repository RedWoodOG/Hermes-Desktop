using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace HermesDesktop.Models;

// ── Message types ──

public enum ChatMessageType
{
    Text,
    ToolCall,
    System
}

// ── Tool call info ──

public sealed class ToolCallInfo : INotifyPropertyChanged
{
    private string _status = "pending";
    private string? _result;

    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string? CallId { get; set; }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public string? Result
    {
        get => _result;
        set { _result = value; OnPropertyChanged(); }
    }

    public TimeSpan? Duration { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ── Chat message item (bindable, supports streaming) ──

public sealed class ChatMessageItem : INotifyPropertyChanged
{
    private string _content;
    private readonly System.Text.StringBuilder _thinkingBuilder = new();
    private string _thinkingContent = "";
    private bool _isStreaming;
    private ChatMessageType _messageType;

    public ChatMessageItem(
        string authorLabel,
        string content,
        HorizontalAlignment bubbleAlignment,
        Brush bubbleBackground,
        Brush bubbleBorderBrush,
        Brush labelBrush,
        ChatMessageType messageType = ChatMessageType.Text,
        List<ToolCallInfo>? toolCalls = null)
    {
        AuthorLabel = authorLabel;
        _content = content;
        _messageType = messageType;
        BubbleAlignment = bubbleAlignment;
        BubbleBackground = bubbleBackground;
        BubbleBorderBrush = bubbleBorderBrush;
        LabelBrush = labelBrush;
        ToolCalls = toolCalls;
    }

    public string AuthorLabel { get; }
    public HorizontalAlignment BubbleAlignment { get; }
    public Brush BubbleBackground { get; }
    public Brush BubbleBorderBrush { get; }
    public Brush LabelBrush { get; }

    public string Content
    {
        get => _content;
        set { _content = value; OnPropertyChanged(); }
    }

    /// <summary>Reasoning/thinking content from reasoning models (collapsible in UI).</summary>
    public string ThinkingContent
    {
        get => _thinkingContent;
        set { _thinkingContent = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasThinking)); }
    }

    /// <summary>Whether this message has any thinking content to display.</summary>
    public bool HasThinking => !string.IsNullOrEmpty(_thinkingContent);

    public bool IsStreaming
    {
        get => _isStreaming;
        set { _isStreaming = value; OnPropertyChanged(); }
    }

    public ChatMessageType MessageType
    {
        get => _messageType;
        set { _messageType = value; OnPropertyChanged(); }
    }

    public List<ToolCallInfo>? ToolCalls { get; }

    // ── Streaming helpers ──

    public void AppendToken(string token)
    {
        _content += token;
        OnPropertyChanged(nameof(Content));
    }

    public void AppendThinking(string token)
    {
        _thinkingBuilder.Append(token);
        _thinkingContent = _thinkingBuilder.ToString();
        OnPropertyChanged(nameof(ThinkingContent));
        OnPropertyChanged(nameof(HasThinking));
    }

    // ── INotifyPropertyChanged ──

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DreamStatusViewModel
{
    public DreamStatusViewModel()
    {
        IsConsolidating = false;
        Status = "Idle";
        LastConsolidation = "Never";
    }

    public DreamStatusViewModel(DateTimeOffset? lastRun, bool isRunning)
    {
        IsConsolidating = isRunning;
        Status = isRunning ? "Consolidating..." : "Ready";
        LastConsolidation = lastRun?.ToLocalTime().ToString("g") ?? "Never";
    }

    public bool IsConsolidating { get; }
    public string Status { get; }
    public string LastConsolidation { get; }
}
