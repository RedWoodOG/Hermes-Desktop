namespace Hermes.Agent.LLM;

using System.Text.Json;

/// <summary>Parses OpenAI-compatible SSE JSON chunks into <see cref="StreamEvent"/> values.</summary>
internal static class OpenAiStreamChunkParser
{
    public static bool TryParseUsage(JsonElement root, out UsageStats usage)
    {
        usage = default!;
        if (!root.TryGetProperty("usage", out var usageEl) || usageEl.ValueKind != JsonValueKind.Object)
            return false;

        int input = usageEl.TryGetProperty("prompt_tokens", out var pt) &&
                      pt.ValueKind == JsonValueKind.Number
            ? pt.GetInt32()
            : 0;
        int output = usageEl.TryGetProperty("completion_tokens", out var ctok) &&
                     ctok.ValueKind == JsonValueKind.Number
            ? ctok.GetInt32()
            : 0;
        int? cacheRead = null;
        if (usageEl.TryGetProperty("prompt_tokens_details", out var ptd) &&
            ptd.ValueKind == JsonValueKind.Object &&
            ptd.TryGetProperty("cached_tokens", out var ct2) &&
            ct2.ValueKind == JsonValueKind.Number)
        {
            cacheRead = ct2.GetInt32();
        }

        usage = new UsageStats(input, output, CacheCreationTokens: null, CacheReadTokens: cacheRead);
        return true;
    }

    public static IEnumerable<StreamEvent> ProjectDelta(
        JsonElement delta,
        RedactedThinkingStreamSplitter contentSplitter)
    {
        if (delta.TryGetProperty("reasoning", out var reasoningEl) &&
            reasoningEl.ValueKind == JsonValueKind.String)
        {
            var reasoning = reasoningEl.GetString();
            if (!string.IsNullOrEmpty(reasoning))
                yield return new StreamEvent.ThinkingDelta(reasoning);
        }

        if (!delta.TryGetProperty("content", out var contentEl) ||
            contentEl.ValueKind != JsonValueKind.String)
        {
            yield break;
        }

        var token = contentEl.GetString();
        if (string.IsNullOrEmpty(token))
            yield break;

        foreach (var ev in contentSplitter.AppendToken(token))
            yield return ev;
    }

    public static void TryCaptureUsage(JsonElement root, ref UsageStats? finalUsage)
    {
        if (TryParseUsage(root, out var usage))
            finalUsage = usage;
    }

    public static IEnumerable<StreamEvent> ProjectChunk(
        JsonElement root,
        RedactedThinkingStreamSplitter contentSplitter)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            yield break;

        var delta = choices[0].GetProperty("delta");
        foreach (var ev in ProjectDelta(delta, contentSplitter))
            yield return ev;
    }
}
