namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;

/// <summary>
/// Send a message to a messaging platform (stub).
/// Actual sending requires a gateway integration.
/// </summary>
public sealed class SendMessageTool : ITool
{
    private static readonly HashSet<string> SupportedPlatforms = new(StringComparer.OrdinalIgnoreCase)
    {
        "telegram", "discord", "slack", "matrix"
    };

    public string Name => "send_message";
    public string Description => "Send a message to a messaging platform (telegram, discord, slack, matrix). Currently queues for gateway delivery.";
    public Type ParametersType => typeof(SendMessageParameters);

    public Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (SendMessageParameters)parameters;

        if (string.IsNullOrWhiteSpace(p.Platform))
            return Task.FromResult(ToolResult.Fail("Platform is required."));

        if (!SupportedPlatforms.Contains(p.Platform))
            return Task.FromResult(ToolResult.Fail(
                $"Unsupported platform: {p.Platform}. Supported: {string.Join(", ", SupportedPlatforms)}"));

        if (string.IsNullOrWhiteSpace(p.Message))
            return Task.FromResult(ToolResult.Fail("Message is required."));

        // Stub: in a real implementation this would route through the messaging gateway
        var chatIdInfo = string.IsNullOrWhiteSpace(p.ChatId) ? "default chat" : $"chat {p.ChatId}";
        return Task.FromResult(ToolResult.Ok(
            $"Message queued for {p.Platform} ({chatIdInfo}): {p.Message.Length} chars. " +
            "Delivery pending gateway connection."));
    }
}

public sealed class SendMessageParameters
{
    public required string Platform { get; init; }
    public required string Message { get; init; }
    public string? ChatId { get; init; }
}
