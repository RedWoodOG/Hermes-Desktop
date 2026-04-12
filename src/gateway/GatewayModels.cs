namespace Hermes.Agent.Gateway;

using System.Text.Json.Serialization;

// ══════════════════════════════════════════════
// Gateway Models — platform abstraction layer
// ══════════════════════════════════════════════
//
// Upstream ref: gateway/run.py — GatewayRunner, BasePlatformAdapter,
// MessageEvent, SessionSource, DeliveryRouter

/// <summary>
/// Supported messaging platforms.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Platform
{
    Telegram,
    Discord,
    Slack,
    WhatsApp,
    Signal,
    Matrix,
    Mattermost,
    Email,
    Webhook,
    Api
}

/// <summary>
/// Message types from platforms.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageType
{
    Text,
    Command,
    Image,
    Audio,
    Video,
    Document,
    Sticker,
    Location
}

/// <summary>
/// Incoming message from a platform adapter.
/// Upstream ref: gateway/run.py MessageEvent
/// </summary>
public sealed class MessageEvent
{
    public required string Text { get; init; }
    public MessageType Type { get; init; } = MessageType.Text;
    public required SessionSource Source { get; init; }
    public List<string> MediaUrls { get; init; } = new List<string>();
    public List<string> MediaTypes { get; init; } = new List<string>();
    public string? MessageId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Extract slash command from text (e.g., "/model" → "model").</summary>
    public string? GetCommand()
    {
        if (!Text.StartsWith('/')) return null;
        var parts = Text.TrimStart('/').Split(' ', 2);
        return parts[0].ToLowerInvariant();
    }

    public string? GetCommandArgs()
    {
        if (!Text.StartsWith('/')) return null;
        var parts = Text.TrimStart('/').Split(' ', 2);
        return parts.Length > 1 ? parts[1] : null;
    }
}

/// <summary>
/// Identifies where a message came from — platform + chat + user.
/// </summary>
public sealed class SessionSource
{
    public required Platform Platform { get; init; }
    public required string ChatId { get; init; }
    public required string UserId { get; init; }
    public string? Username { get; init; }
    public string? ThreadId { get; init; }
    public bool IsGroup { get; init; }
    public bool IsDm { get; init; }
}

/// <summary>
/// Per-platform configuration.
/// </summary>
public sealed class PlatformConfig
{
    public bool Enabled { get; set; }
    public string? Token { get; set; }
    public string? ApiKey { get; set; }
    public Dictionary<string, string> Extra { get; set; } = new();
}

/// <summary>
/// Gateway configuration loaded from config.yaml.
/// </summary>
public sealed class GatewayConfig
{
    public Dictionary<Platform, PlatformConfig> Platforms { get; set; } = new();

    /// <summary>Global user allowlist (comma-separated IDs).</summary>
    public string? AllowedUsers { get; set; }

    /// <summary>Whether to create per-user sessions in group chats.</summary>
    public bool GroupSessionsPerUser { get; set; }

    /// <summary>Session inactivity timeout in seconds.</summary>
    public int SessionTimeoutSeconds { get; set; } = 1800; // 30 min

    /// <summary>Maximum retry attempts for failed platform connections.</summary>
    public int MaxRetryAttempts { get; set; } = 20;
}

/// <summary>
/// Outbound message for delivery to a platform.
/// </summary>
public sealed class OutboundMessage
{
    public required Platform Platform { get; init; }
    public required string ChatId { get; init; }
    public required string Text { get; init; }
    public string? ThreadId { get; init; }
    public string? ReplyToMessageId { get; init; }
    public List<string>? MediaPaths { get; init; }
}

/// <summary>
/// Result of sending a message.
/// </summary>
public sealed class DeliveryResult
{
    public bool Success { get; init; }
    public string? MessageId { get; init; }
    public string? Error { get; init; }

    public static DeliveryResult Ok(string? messageId = null) => new() { Success = true, MessageId = messageId };
    public static DeliveryResult Fail(string error) => new() { Success = false, Error = error };
}
