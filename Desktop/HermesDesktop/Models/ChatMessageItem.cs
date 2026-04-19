using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace HermesDesktop.Models;

// ── Shared base for chat-list items (messages + event pills) ──

public abstract class ChatListItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected static Brush Res(string key) => (Brush)Application.Current.Resources[key];
}

// ── Message types ──

public enum ChatMessageType
{
    Text,
    ToolCall,
    System
}

public enum ChatRole
{
    User,
    Assistant,
    System
}

// ── Tool call info (legacy; retained for any non-chat consumers) ──

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

// ── Chat message item (bindable, supports streaming, role-derived visuals) ──

public sealed class ChatMessageItem : ChatListItem
{
    private string _content;
    private readonly System.Text.StringBuilder _thinkingBuilder = new();
    private string _thinkingContent = "";
    private bool _isStreaming;
    private ChatMessageType _messageType;

    public ChatMessageItem(ChatRole role, string content, ChatMessageType messageType = ChatMessageType.Text)
    {
        Role = role;
        _content = content;
        _messageType = messageType;
        Timestamp = DateTime.Now;
    }

    public ChatRole Role { get; }
    public DateTime Timestamp { get; }

    // ── Role-derived visuals (node-card grammar) ──

    public string RoleGlyph => Role switch
    {
        ChatRole.User      => "\u25C6",  // ◆
        ChatRole.Assistant => "\u25B2",  // ▲
        ChatRole.System    => "\u25C7",  // ◇
        _ => ""
    };

    public string RoleLabel => Role switch
    {
        ChatRole.User      => "YOU",
        ChatRole.Assistant => "HERMES",
        ChatRole.System    => "SYSTEM",
        _ => ""
    };

    public Brush RoleBrush => Role switch
    {
        ChatRole.User      => Res("AppAccentTextBrush"),
        ChatRole.Assistant => Res("SessionGreenBrush"),
        ChatRole.System    => Res("AppTextSecondaryBrush"),
        _ => Res("AppTextSecondaryBrush"),
    };

    public Brush EdgeBrush => BuildEdgeBrush(Role);

    private static Brush BuildEdgeBrush(ChatRole role)
    {
        var solid = role switch
        {
            ChatRole.User      => Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00), // #FFD700
            ChatRole.Assistant => Color.FromArgb(0xFF, 0x49, 0xC2, 0x7D), // #49C27D
            ChatRole.System    => Color.FromArgb(0xFF, 0x4A, 0x55, 0x65), // #4A5565
            _ => Color.FromArgb(0xFF, 0x4A, 0x55, 0x65),
        };
        var fade = Color.FromArgb(0x00, solid.R, solid.G, solid.B);
        var grad = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        grad.GradientStops.Add(new GradientStop { Color = solid, Offset = 0 });
        grad.GradientStops.Add(new GradientStop { Color = fade, Offset = 0.85 });
        return grad;
    }

    // ── Compat alias: used by ChatPage for "is this a user message?" checks ──
    public string AuthorLabel => RoleLabel;

    // ── Meta line: "opus-4-7 · 14:02" or "streaming" ──
    public string MetaLine => _isStreaming ? "streaming" : Timestamp.ToString("HH:mm");

    public string Content
    {
        get => _content;
        set { _content = value; OnPropertyChanged(); }
    }

    public string ThinkingContent
    {
        get => _thinkingContent;
        set { _thinkingContent = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasThinking)); OnPropertyChanged(nameof(ThinkingVisibility)); }
    }

    public bool HasThinking => !string.IsNullOrEmpty(_thinkingContent);
    public Visibility ThinkingVisibility => HasThinking ? Visibility.Visible : Visibility.Collapsed;

    public bool IsStreaming
    {
        get => _isStreaming;
        set { _isStreaming = value; OnPropertyChanged(); OnPropertyChanged(nameof(MetaLine)); OnPropertyChanged(nameof(StreamingVisibility)); }
    }

    public Visibility StreamingVisibility => _isStreaming ? Visibility.Visible : Visibility.Collapsed;

    public ChatMessageType MessageType
    {
        get => _messageType;
        set { _messageType = value; OnPropertyChanged(); }
    }

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
        OnPropertyChanged(nameof(ThinkingVisibility));
    }
}

// ── Inline event pill (tool / memory / skill / file invocation) ──

public enum ChatEventKind { Tool, Memory, Skill, File }

public sealed class ChatEventItem : ChatListItem
{
    private ChatEventKind _kind;
    private string? _tail;

    public ChatEventItem(string toolName, string detail, string? tail = null)
    {
        ToolName = toolName;
        Detail = detail;
        _tail = tail;
        Timestamp = DateTime.Now;
        _kind = Classify(toolName);
    }

    public string ToolName { get; }
    public string Detail { get; }
    public DateTime Timestamp { get; }

    public ChatEventKind Kind
    {
        get => _kind;
        private set { _kind = value; OnPropertyChanged(); OnPropertyChanged(nameof(KindLabel)); OnPropertyChanged(nameof(KindBrush)); OnPropertyChanged(nameof(Glyph)); }
    }

    public string? Tail
    {
        get => _tail;
        set { _tail = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasTail)); OnPropertyChanged(nameof(TailVisibility)); }
    }

    public bool HasTail => !string.IsNullOrEmpty(_tail);
    public Visibility TailVisibility => HasTail ? Visibility.Visible : Visibility.Collapsed;

    public string Glyph => _kind switch
    {
        ChatEventKind.Memory => "\u25C7",  // ◇
        ChatEventKind.Skill  => "\u2726",  // ✦
        ChatEventKind.File   => "\u229E",  // ⊞
        _                    => "\u2328",  // ⌨ (keyboard — stands in for tool)
    };

    public string KindLabel => _kind switch
    {
        ChatEventKind.Tool   => "TOOL",
        ChatEventKind.Memory => "MEM",
        ChatEventKind.Skill  => "SKILL",
        ChatEventKind.File   => "FILE",
        _ => ""
    };

    public Brush KindBrush => _kind switch
    {
        ChatEventKind.Tool   => Res("ToolOrangeBrush"),
        ChatEventKind.Memory => Res("MemoryPurpleBrush"),
        ChatEventKind.Skill  => Res("SkillRedBrush"),
        ChatEventKind.File   => Res("FileGreenBrush"),
        _ => Res("AppTextSecondaryBrush"),
    };

    // Classify tool name into a semantic category for node-type coloring.
    private static ChatEventKind Classify(string toolName)
    {
        var lower = toolName.ToLowerInvariant();
        if (lower.StartsWith("memory", StringComparison.Ordinal)) return ChatEventKind.Memory;
        if (lower.StartsWith("skill",  StringComparison.Ordinal)) return ChatEventKind.Skill;
        if (lower is "read_file" or "write_file" or "edit_file" or "glob" or "grep"
                  or "patch" or "readfile" or "writefile" or "editfile"
                  or "ls" or "list_directory")
            return ChatEventKind.File;
        return ChatEventKind.Tool;
    }
}

// ── Dream status (unchanged) ──

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
