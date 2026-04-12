namespace Hermes.Agent.Dreamer;

using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Microsoft.Extensions.Logging;

/// <summary>Low-temperature self-review to estimate repetition (1 = fresh, 5 = echo chamber).</summary>
public sealed class EchoDetector
{
    private readonly IChatClient _client;
    private readonly ILogger<EchoDetector> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="EchoDetector"/> using the provided chat client and logger.
    /// </summary>
    public EchoDetector(IChatClient client, ILogger<EchoDetector> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Estimates how much the new walk repeats content from a prior excerpt and produces a repetition score.
    /// </summary>
    /// <param name="walkText">The new walk text to evaluate; will be truncated to at most 6000 characters.</param>
    /// <param name="priorWalkExcerpt">An optional prior excerpt; if null or whitespace it is treated as "(none)" and otherwise truncated to at most 2000 characters.</param>
    /// <returns>An integer score 1–5 where 1 = fresh/new and 5 = heavy repetition; returns 3 if scoring fails or no valid digit is found.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled via the provided cancellation token.</exception>
    /// <exception cref="TaskCanceledException">The operation was canceled via the provided cancellation token.</exception>
    public async Task<int> ScoreEchoAsync(string walkText, string? priorWalkExcerpt, CancellationToken ct)
    {
        try
        {
            var prior = string.IsNullOrWhiteSpace(priorWalkExcerpt) ? "(none)" : priorWalkExcerpt[..Math.Min(2000, priorWalkExcerpt.Length)];
            var prompt =
                "Rate how repetitive the NEW walk is versus the PRIOR excerpt. Reply with ONE digit 1-5 only.\n" +
                "1 = fresh / new angles, 5 = heavy repetition.\n\n" +
                "## PRIOR\n" + prior + "\n\n## NEW WALK\n" +
                walkText[..Math.Min(6000, walkText.Length)];

            var reply = await _client.CompleteAsync(
                new[] { new Message { Role = "user", Content = prompt } },
                ct);

            foreach (var ch in reply.Trim())
            {
                if (ch >= '1' && ch <= '5')
                    return ch - '0';
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Echo detection failed; using neutral score");
        }

        return 3;
    }
}