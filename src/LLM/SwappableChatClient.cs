namespace Hermes.Agent.LLM;

using Hermes.Agent.Core;
using System.Runtime.CompilerServices;

/// <summary>
/// IChatClient proxy that delegates to ChatClientFactory.Current.
/// Registered as the singleton IChatClient in DI. When the factory switches
/// provider, all subsequent calls automatically route to the new client.
/// No code changes needed in Agent, HermesChatService, or any other consumer.
/// </summary>
public sealed class SwappableChatClient : IChatClient
{
    private readonly ChatClientFactory _factory;

    public SwappableChatClient(ChatClientFactory factory)
    {
        _factory = factory;
    }

    public Task<string> CompleteAsync(IEnumerable<Message> messages, CancellationToken ct)
        => _factory.Current.CompleteAsync(messages, ct);

    public Task<ChatResponse> CompleteWithToolsAsync(
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition> tools,
        CancellationToken ct)
        => _factory.Current.CompleteWithToolsAsync(messages, tools, ct);

    public IAsyncEnumerable<string> StreamAsync(
        IEnumerable<Message> messages,
        CancellationToken ct)
        => _factory.Current.StreamAsync(messages, ct);

    public IAsyncEnumerable<StreamEvent> StreamAsync(
        string? systemPrompt,
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition>? tools = null,
        CancellationToken ct = default)
        => _factory.Current.StreamAsync(systemPrompt, messages, tools, ct);
}
