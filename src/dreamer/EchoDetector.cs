namespace Hermes.Agent.Dreamer;

using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Microsoft.Extensions.Logging;

/// <summary>Low-temperature self-review to estimate repetition (1 = fresh, 5 = echo chamber).</summary>
public sealed class EchoDetector
{
    private readonly IChatClient _client;
    private readonly ILogger<EchoDetector> _logger;

    public EchoDetector(IChatClient client, ILogger<EchoDetector> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>Returns 1–5; on failure returns 3 (neutral).</summary>
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