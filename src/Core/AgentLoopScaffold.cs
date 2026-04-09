namespace Hermes.Agent.Core;

using Hermes.Agent.Context;
using Hermes.Agent.Memory;
using Hermes.Agent.Soul;
using Hermes.Agent.Transcript;
using Microsoft.Extensions.Logging;

/// <summary>
/// Extraction scaffold for the agent loop.
/// Keeps behavior stable while creating explicit seams for future decomposition.
/// </summary>
internal static class AgentContextAssembler
{
    internal static async Task InjectMemoriesAsync(
        Session session,
        string userMessage,
        IEnumerable<string> registeredToolNames,
        MemoryManager? memories,
        ILogger logger,
        CancellationToken ct)
    {
        if (memories is null)
            return;

        try
        {
            var recentTools = registeredToolNames.Take(10).ToList();
            var relevantMemories = await memories.LoadRelevantMemoriesAsync(userMessage, recentTools, ct);
            if (relevantMemories.Count == 0)
                return;

            var memoryBlock = string.Join("\n---\n",
                relevantMemories.Select(m => $"[{m.Type}] {m.Filename}:\n{m.Content}"));

            session.Messages.Insert(0, new Message
            {
                Role = "system",
                Content = $"[Relevant Memories]\n{memoryBlock}"
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load memories, continuing without them");
        }
    }

    internal static async Task InjectSoulFallbackAsync(
        Session session,
        ContextManager? contextManager,
        SoulService? soulService,
        ILogger logger)
    {
        // If ContextManager is available, it handles soul injection via PromptBuilder.
        if (contextManager is not null || soulService is null)
            return;

        try
        {
            var soulContext = await soulService.AssembleSoulContextAsync();
            if (string.IsNullOrWhiteSpace(soulContext))
                return;

            // Insert soul as first system message (before memory blocks).
            session.Messages.Insert(0, new Message
            {
                Role = "system",
                Content = soulContext
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load soul context, continuing without it");
        }
    }

    internal static async Task<List<Message>?> PrepareOptimizedContextAsync(
        string sessionId,
        string userMessage,
        ContextManager? contextManager,
        ILogger logger,
        CancellationToken ct)
    {
        if (contextManager is null)
            return null;

        try
        {
            return await contextManager.PrepareContextAsync(
                sessionId,
                userMessage,
                retrievedContext: null,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ContextManager failed, falling back to raw session messages");
            return null;
        }
    }
}

/// <summary>
/// Single place for session/transcript write semantics used by Agent.
/// </summary>
internal static class AgentSessionWriter
{
    internal static async Task<Message> AppendUserMessageAsync(
        Session session,
        string content,
        TranscriptStore? transcripts,
        CancellationToken ct)
    {
        var message = new Message { Role = "user", Content = content };
        session.AddMessage(message);

        if (transcripts is not null)
            await transcripts.SaveMessageAsync(session.Id, message, ct);

        return message;
    }

    internal static async Task<Message> AppendAssistantMessageAsync(
        Session session,
        string content,
        TranscriptStore? transcripts,
        CancellationToken ct)
    {
        var message = new Message { Role = "assistant", Content = content };
        session.AddMessage(message);

        if (transcripts is not null)
            await transcripts.SaveMessageAsync(session.Id, message, ct);

        return message;
    }

    internal static async Task<Message> AppendAssistantToolRequestMessageAsync(
        Session session,
        string content,
        List<ToolCall> toolCalls,
        TranscriptStore? transcripts,
        CancellationToken ct)
    {
        var message = new Message
        {
            Role = "assistant",
            Content = content,
            ToolCalls = toolCalls
        };
        session.AddMessage(message);

        if (transcripts is not null)
            await transcripts.SaveMessageAsync(session.Id, message, ct);

        return message;
    }

    internal static async Task<Message> AppendToolMessageAsync(
        Session session,
        string content,
        string toolCallId,
        string toolName,
        TranscriptStore? transcripts,
        CancellationToken ct)
    {
        var message = new Message
        {
            Role = "tool",
            Content = content,
            ToolCallId = toolCallId,
            ToolName = toolName
        };
        session.AddMessage(message);

        if (transcripts is not null)
            await transcripts.SaveMessageAsync(session.Id, message, ct);

        return message;
    }
}
