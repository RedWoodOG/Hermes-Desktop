namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Microsoft.Extensions.Logging;

// ══════════════════════════════════════════════
// Mixture of Agents Tool
// ══════════════════════════════════════════════
//
// Upstream ref: tools/mixture_of_agents_tool.py
// Routes complex queries to multiple LLM providers in parallel,
// then synthesizes via a final aggregation model.
// Fault-tolerant: minimum 1 response needed.

/// <summary>
/// Send a query to multiple LLM models in parallel and synthesize results.
/// Useful for complex analysis, second opinions, and diverse perspectives.
/// </summary>
public sealed class MixtureOfAgentsTool : ITool
{
    private readonly IChatClient _primaryClient;
    private readonly List<MixtureModel> _models;
    private readonly ILogger<MixtureOfAgentsTool> _logger;

    public MixtureOfAgentsTool(
        IChatClient primaryClient,
        List<MixtureModel>? models = null,
        ILogger<MixtureOfAgentsTool>? logger = null)
    {
        _primaryClient = primaryClient;
        _models = models ?? DefaultModels();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MixtureOfAgentsTool>.Instance;
    }

    public string Name => "mixture_of_agents";
    public string Description => "Send a complex query to multiple AI models in parallel and synthesize their responses for higher quality answers.";
    public Type ParametersType => typeof(MixtureParameters);

    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (MixtureParameters)parameters;

        if (string.IsNullOrWhiteSpace(p.Query))
            return ToolResult.Fail("Query is required.");

        var messages = new[] { new Message { Role = "user", Content = p.Query } };

        // Phase 1: Query all models in parallel
        var tasks = _models.Select(async model =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(120));

                if (model.Client is not null)
                {
                    var response = await model.Client.CompleteAsync(messages, cts.Token);
                    return (model.Name, Response: response, Error: (string?)null);
                }
                else
                {
                    // Use primary client (different models on same provider)
                    var response = await _primaryClient.CompleteAsync(messages, cts.Token);
                    return (model.Name, Response: response, Error: (string?)null);
                }
            }
            catch (Exception ex)
            {
                return (model.Name, Response: (string?)null, Error: ex.Message);
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);

        // Filter successful responses (minimum 1 needed)
        var successful = results.Where(r => r.Response is not null).ToList();
        if (successful.Count == 0)
        {
            var errors = string.Join("\n", results.Select(r => $"  {r.Name}: {r.Error}"));
            return ToolResult.Fail($"All models failed:\n{errors}");
        }

        // Phase 2: Synthesize via primary model
        var synthesis = new System.Text.StringBuilder();
        synthesis.AppendLine("You received responses from multiple AI models. Synthesize them into a single, comprehensive answer.");
        synthesis.AppendLine($"Original query: {p.Query}");
        synthesis.AppendLine();

        foreach (var (name, response, _) in successful)
        {
            synthesis.AppendLine($"=== {name} ===");
            synthesis.AppendLine(response);
            synthesis.AppendLine();
        }

        synthesis.AppendLine("Provide a unified answer that combines the best insights from all responses. Resolve any contradictions.");

        try
        {
            var synthesisMessages = new[]
            {
                new Message { Role = "user", Content = synthesis.ToString() }
            };
            var finalAnswer = await _primaryClient.CompleteAsync(synthesisMessages, ct);

            var meta = $"\n\n---\n*Synthesized from {successful.Count}/{results.Length} models: {string.Join(", ", successful.Select(s => s.Name))}*";
            return ToolResult.Ok(finalAnswer + meta);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mixture-of-agents synthesis failed; returning best single-model response.");
            var best = successful.First();
            return ToolResult.Ok(
                $"(Synthesis failed; returning {best.Name} response.)\n\n{best.Response}");
        }
    }

    private static List<MixtureModel> DefaultModels() =>
    [
        new() { Name = "primary" }
    ];
}

public sealed class MixtureModel
{
    public required string Name { get; init; }
    public IChatClient? Client { get; init; }
}

public sealed class MixtureParameters
{
    public required string Query { get; init; }
}
