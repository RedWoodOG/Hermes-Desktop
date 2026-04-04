using System.Text.Json;
using System.Text.Json.Serialization;
using Hermes.Agent.Core;

namespace Hermes.Agent.Context;

/// <summary>
/// Assembles cache-safe prompts with a stable system prefix.
/// The system prefix is byte-identical every turn — this is the cache anchor.
/// OpenAI/Anthropic can reuse cached computation when the prefix doesn't change.
/// </summary>
public sealed class PromptBuilder
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _systemPrompt;

    /// <summary>The stable system prompt used as the cache anchor.</summary>
    public string SystemPrompt => _systemPrompt;

    /// <summary>
    /// Creates a prompt builder with a fixed system prompt that serves as the cache anchor.
    /// </summary>
    public PromptBuilder(string systemPrompt)
    {
        _systemPrompt = systemPrompt;
    }

    /// <summary>
    /// Builds a cache-optimized prompt packet from session state, recent turns, and optional retrieved context.
    /// The system prompt is stable (cache anchor), session state changes slowly, and recent turns change each turn.
    /// </summary>
    public PromptPacket Build(BuildRequest request)
    {
        var stateJson = JsonSerializer.Serialize(new
        {
            activeGoal = NullIfEmpty(request.State.ActiveGoal),
            constraints = NullIfEmptyCollection(request.State.Constraints),
            decisions = NullIfEmptyCollection(request.State.Decisions?.Select(d => new { d.What, d.Why })),
            openQuestions = NullIfEmptyCollection(request.State.OpenQuestions),
            importantEntities = NullIfEmptyCollection(request.State.ImportantEntities),
            summary = NullIfEmpty(request.State.Summary?.Content)
        }, JsonOpts);

        return new PromptPacket
        {
            SystemPrompt = _systemPrompt,
            SessionStateJson = stateJson,
            RetrievedContext = request.RetrievedContext,
            RecentTurns = request.RecentTurns,
            CurrentUserMessage = request.CurrentUserMessage
        };
    }

    /// <summary>
    /// Converts a PromptPacket into the OpenAI-compatible message list format.
    /// Layout:
    ///   [0] system: stable instructions (cache anchor)
    ///   [1] system: session state JSON (slow-changing, second cache layer)
    ///   [2..N] system: retrieved context (if any)
    ///   [N+1..M] user/assistant: recent turns
    ///   [M+1] user: current message
    /// </summary>
    public List<Message> ToOpenAiMessages(PromptPacket packet)
    {
        var messages = new List<Message>();

        // Layer 1: Stable system prompt (cache anchor — never changes)
        messages.Add(new Message
        {
            Role = "system",
            Content = packet.SystemPrompt
        });

        // Layer 2: Session state (changes slowly — good for incremental caching)
        if (!string.IsNullOrEmpty(packet.SessionStateJson) && packet.SessionStateJson != "{}")
        {
            messages.Add(new Message
            {
                Role = "system",
                Content = $"[Session State]\n{packet.SessionStateJson}"
            });
        }

        // Layer 3: Retrieved context (only present when relevant)
        if (packet.RetrievedContext is { Count: > 0 })
        {
            var contextBlock = string.Join("\n---\n", packet.RetrievedContext);
            messages.Add(new Message
            {
                Role = "system",
                Content = $"[Retrieved Context]\n{contextBlock}"
            });
        }

        // Layer 4: Recent conversation turns (sliding window)
        if (packet.RecentTurns is { Count: > 0 })
        {
            messages.AddRange(packet.RecentTurns);
        }

        // Layer 5: Current user message
        messages.Add(new Message
        {
            Role = "user",
            Content = packet.CurrentUserMessage
        });

        return messages;
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static object? NullIfEmptyCollection<T>(IEnumerable<T>? collection)
    {
        if (collection is null) return null;
        var list = collection as IList<T> ?? collection.ToList();
        return list.Count == 0 ? null : list;
    }
}

/// <summary>Input parameters for building a prompt packet.</summary>
public sealed class BuildRequest
{
    /// <summary>Current session state to serialize into the prompt.</summary>
    public required SessionState State { get; init; }

    /// <summary>The user's message for the current turn.</summary>
    public required string CurrentUserMessage { get; init; }

    /// <summary>Recent conversation turns from the sliding window.</summary>
    public List<Message> RecentTurns { get; init; } = new();

    /// <summary>Optional context chunks retrieved on demand (e.g. from memory or search).</summary>
    public List<string>? RetrievedContext { get; init; }
}

/// <summary>Assembled prompt ready for conversion to provider message format.</summary>
public sealed class PromptPacket
{
    /// <summary>Stable system instructions (cache anchor layer).</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>Serialized session state JSON (slow-changing second cache layer).</summary>
    public string SessionStateJson { get; init; } = "{}";

    /// <summary>Optional retrieved context chunks.</summary>
    public List<string>? RetrievedContext { get; init; }

    /// <summary>Recent conversation turns from the sliding window.</summary>
    public List<Message> RecentTurns { get; init; } = new();

    /// <summary>The user's message for the current turn.</summary>
    public required string CurrentUserMessage { get; init; }
}
