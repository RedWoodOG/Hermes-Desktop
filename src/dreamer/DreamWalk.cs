namespace Hermes.Agent.Dreamer;

using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Microsoft.Extensions.Logging;

/// <summary>One free-association walk: mode selection, prompt assembly, LLM call, journaling.</summary>
public sealed class DreamWalk
{
    private readonly DreamerRoom _room;
    private readonly IChatClient _walkClient;
    private readonly ILogger<DreamWalk> _logger;
    private readonly Random _rng = new();

    /// <summary>
    /// Initializes a new instance of DreamWalk using the specified room, chat client, and logger.
    /// </summary>
    public DreamWalk(DreamerRoom room, IChatClient walkClient, ILogger<DreamWalk> logger)
    {
        _room = room;
        _walkClient = walkClient;
        _logger = logger;
    }

    /// <summary>
    /// Performs a single "dream walk": selects a walk mode, builds a prompt from local room files and provided context, invokes the chat client to generate a journal entry, writes the entry to a new walk file, and returns the generated text.
    /// </summary>
    /// <param name="config">Configuration for the walk (accepted for future use; not used by the current implementation).</param>
    /// <param name="researchContext">Contextual material (e.g., transcripts or inbox excerpts); included in the prompt and truncated to 12,000 characters when used.</param>
    /// <param name="priorWalkExcerpt">Optional excerpt from a prior walk for continuity; treated as "(none)" when null and truncated to 2,000 characters when used.</param>
    /// <param name="ct">Cancellation token for the chat call and file I/O operations.</param>
    /// <returns>The generated journal entry text produced by the chat client.</returns>
    public async Task<string> RunAsync(
        DreamerConfig config,
        string researchContext,
        string? priorWalkExcerpt,
        CancellationToken ct)
    {
        var mode = PickMode();
        var soul = await ReadOptionalFileAsync(_room.SoulPath, ct);
        var fasc = await ReadOptionalFileAsync(_room.FascinationsPath, ct);

        var prompt = $"""
            ## Dreamer walk mode: {mode}
            {soul}

            ## Fascinations
            {fasc[..Math.Min(4000, fasc.Length)]}

            ## Research context (transcripts / inbox excerpts)
            {researchContext[..Math.Min(12000, researchContext.Length)]}

            ## Prior walk (for continuity)
            {(priorWalkExcerpt ?? "(none)")[..Math.Min(2000, (priorWalkExcerpt ?? "(none)").Length)]}

            Write this walk as a dated journal entry. End with `[BUILD: slug]` only if you have a concrete sandbox project idea; otherwise omit that line.
            """;

        var text = await _walkClient.CompleteAsync(
            new[] { new Message { Role = "user", Content = prompt } },
            ct);

        var path = _room.NewWalkPath();
        try
        {
            await File.WriteAllTextAsync(path,
                $"# Walk {DateTime.UtcNow:O}\n\n## Mode: {mode}\n\n{text}\n",
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableFileException(ex))
        {
            _logger.LogWarning(ex, "Dreamer walk completed but journal write failed for {Path}", path);
        }

        _logger.LogInformation("Dreamer walk completed -> {Path}", path);
        return text;
    }

    /// <summary>
    /// Chooses a walk mode string by sampling the private random generator.
    /// </summary>
    /// <returns>One of "drift" (40% probability), "continue" (30% probability), "tangent" (20% probability), or "tend" (10% probability).</returns>
    private string PickMode()
    {
        var r = _rng.NextDouble();
        if (r < 0.40) return "drift";
        if (r < 0.70) return "continue";
        if (r < 0.90) return "tangent";
        return "tend";
    }

    private async Task<string> ReadOptionalFileAsync(string path, CancellationToken ct)
    {
        try
        {
            return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableFileException(ex))
        {
            _logger.LogWarning(ex, "Dreamer walk skipped unreadable file {Path}", path);
            return "";
        }
    }

    private static bool IsRecoverableFileException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;
}
