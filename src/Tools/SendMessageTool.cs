namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using Hermes.Agent.Gateway;

/// <summary>
/// Send a message to a messaging platform via the gateway.
/// Routes through GatewayService when available, otherwise queues.
/// </summary>
public sealed class SendMessageTool : ITool
{
    private static readonly Dictionary<string, Platform> PlatformMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["telegram"] = Platform.Telegram,
        ["discord"] = Platform.Discord,
        ["slack"] = Platform.Slack,
        ["matrix"] = Platform.Matrix,
        ["whatsapp"] = Platform.WhatsApp,
        ["webhook"] = Platform.Webhook
    };

    private GatewayService? _gateway;

    public SendMessageTool(GatewayService? gateway = null) => _gateway = gateway;

    /// <summary>Set or update the gateway reference (for late binding).</summary>
    public void SetGateway(GatewayService gateway) => _gateway = gateway;

    public string Name => "send_message";
    public string Description => "Send a message to a messaging platform (telegram, discord, slack, matrix, whatsapp, webhook).";
    public Type ParametersType => typeof(SendMessageParameters);

    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (SendMessageParameters)parameters;

        if (string.IsNullOrWhiteSpace(p.Platform))
            return ToolResult.Fail("Platform is required.");

        if (!PlatformMap.TryGetValue(p.Platform, out var platform))
            return ToolResult.Fail(
                $"Unsupported platform: {p.Platform}. Supported: {string.Join(", ", PlatformMap.Keys)}");

        if (string.IsNullOrWhiteSpace(p.Message))
            return ToolResult.Fail("Message is required.");

        if (string.IsNullOrWhiteSpace(p.ChatId))
            return ToolResult.Fail("ChatId is required.");

        // Route through gateway if available
        if (_gateway is not null && _gateway.IsRunning)
        {
            var result = await _gateway.SendTextAsync(platform, p.ChatId, p.Message, ct);
            return result.Success
                ? ToolResult.Ok($"Message sent to {p.Platform} (chat {p.ChatId}): {p.Message.Length} chars. MessageId: {result.MessageId}")
                : ToolResult.Fail($"Delivery failed: {result.Error}");
        }

        return ToolResult.Fail(
            "Gateway is not running. Start the gateway from Integrations settings to send messages.");
    }
}

public sealed class SendMessageParameters
{
    public required string Platform { get; init; }
    public required string Message { get; init; }
    public string? ChatId { get; init; }
}
