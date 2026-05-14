namespace Hermes.Agent.Tools;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hermes.Agent.Core;

/// <summary>
/// In-memory planning tool for tracking multi-step work within a tool instance.
/// </summary>
public sealed class PlanningTool : ITool, IToolSchemaProvider
{
    private static readonly JsonSerializerOptions SchemaJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "not_started",
        "in_progress",
        "completed",
        "blocked"
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, PlanState> _plans = new(StringComparer.OrdinalIgnoreCase);
    private string? _activePlanId;

    public string Name => "planning";

    public string Description =>
        "Create, inspect, list, update step status, and delete in-memory execution plans.";

    public Type ParametersType => typeof(PlanningParameters);

    public Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (parameters is not PlanningParameters p)
            return Task.FromResult(ToolResult.Fail("Invalid parameters for planning tool."));

        try
        {
            var command = (p.Command ?? p.Action)?.Trim().ToLowerInvariant();
            var result = command switch
            {
                "create" => CreatePlan(p.PlanId, p.Title, p.Steps),
                "get" => GetPlan(p.PlanId),
                "list" => ListPlans(),
                "mark_step" => MarkStep(p.PlanId, p.StepIndex, p.StepStatus, p.StepNotes),
                "delete" => DeletePlan(p.PlanId),
                _ => ToolResult.Fail(
                    $"Unknown command: {p.Command ?? p.Action}. Use create, get, list, mark_step, or delete.")
            };

            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Planning tool failed: {ex.Message}", ex));
        }
    }

    public JsonElement? GetParameterSchema()
    {
        var schema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["command"] = new
                {
                    type = "string",
                    description = "Operation to perform.",
                    @enum = new[] { "create", "get", "list", "mark_step", "delete" }
                },
                ["action"] = new
                {
                    type = "string",
                    description = "Alias for command.",
                    @enum = new[] { "create", "get", "list", "mark_step", "delete" }
                },
                ["plan_id"] = new
                {
                    type = "string",
                    description = "Plan identifier. Required for delete; required for get/mark_step when no active plan exists."
                },
                ["title"] = new
                {
                    type = "string",
                    description = "Plan title. Required for create."
                },
                ["steps"] = new
                {
                    type = "array",
                    description = "Non-empty list of step descriptions. Required for create.",
                    items = new { type = "string" }
                },
                ["step_index"] = new
                {
                    type = "integer",
                    description = "0-based step index. Required for mark_step."
                },
                ["step_status"] = new
                {
                    type = "string",
                    description = "Status to apply to a step.",
                    @enum = new[] { "not_started", "in_progress", "completed", "blocked" }
                },
                ["step_notes"] = new
                {
                    type = "string",
                    description = "Optional notes to attach to the step."
                }
            },
            additionalProperties = false
        };

        return JsonSerializer.SerializeToElement(schema, SchemaJsonOptions);
    }

    private ToolResult CreatePlan(string? planId, string? title, IReadOnlyList<string>? steps)
    {
        if (string.IsNullOrWhiteSpace(planId))
            return ToolResult.Fail("plan_id is required for create.");

        if (string.IsNullOrWhiteSpace(title))
            return ToolResult.Fail("title is required for create.");

        if (steps is null || steps.Count == 0 || steps.Any(string.IsNullOrWhiteSpace))
            return ToolResult.Fail("steps must be a non-empty list of step descriptions.");

        lock (_gate)
        {
            if (_plans.ContainsKey(planId))
                return ToolResult.Fail($"A plan with ID '{planId}' already exists.");

            var plan = new PlanState(
                planId,
                title.Trim(),
                steps.Select(step => new PlanStep(step.Trim())).ToList());

            _plans[planId] = plan;
            _activePlanId = planId;

            return ToolResult.Ok($"Plan created: {planId}\n\n{FormatPlan(plan)}");
        }
    }

    private ToolResult GetPlan(string? planId)
    {
        lock (_gate)
        {
            if (!TryGetPlan(planId, out var plan, out var error))
                return ToolResult.Fail(error);

            return ToolResult.Ok(FormatPlan(plan));
        }
    }

    private ToolResult ListPlans()
    {
        lock (_gate)
        {
            if (_plans.Count == 0)
                return ToolResult.Ok("No plans available.");

            var sb = new StringBuilder();
            sb.AppendLine("Available plans:");

            foreach (var plan in _plans.Values.OrderBy(p => p.PlanId, StringComparer.OrdinalIgnoreCase))
            {
                var active = string.Equals(plan.PlanId, _activePlanId, StringComparison.OrdinalIgnoreCase)
                    ? " (active)"
                    : "";
                var completed = plan.Steps.Count(s => s.Status == "completed");
                sb.AppendLine($"- {plan.PlanId}{active}: {plan.Title} - {completed}/{plan.Steps.Count} completed");
            }

            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
    }

    private ToolResult MarkStep(string? planId, int? stepIndex, string? stepStatus, string? stepNotes)
    {
        if (stepIndex is null)
            return ToolResult.Fail("step_index is required for mark_step.");

        if (string.IsNullOrWhiteSpace(stepStatus))
            return ToolResult.Fail("step_status is required for mark_step.");

        if (!ValidStatuses.Contains(stepStatus))
            return ToolResult.Fail("Invalid step_status. Use not_started, in_progress, completed, or blocked.");

        var index = stepIndex.Value;

        lock (_gate)
        {
            if (!TryGetPlan(planId, out var plan, out var error))
                return ToolResult.Fail(error);

            if (index < 0 || index >= plan.Steps.Count)
                return ToolResult.Fail($"Invalid step_index: {index}. Valid range is 0 to {plan.Steps.Count - 1}.");

            var step = plan.Steps[index];
            step.Status = stepStatus.ToLowerInvariant();
            if (stepNotes is not null)
                step.Notes = stepNotes;

            _activePlanId = plan.PlanId;

            return ToolResult.Ok($"Step {index} updated in plan '{plan.PlanId}'.\n\n{FormatPlan(plan)}");
        }
    }

    private ToolResult DeletePlan(string? planId)
    {
        if (string.IsNullOrWhiteSpace(planId))
            return ToolResult.Fail("plan_id is required for delete.");

        lock (_gate)
        {
            if (!_plans.Remove(planId))
                return ToolResult.Fail($"No plan found with ID: {planId}");

            if (string.Equals(_activePlanId, planId, StringComparison.OrdinalIgnoreCase))
                _activePlanId = null;

            return ToolResult.Ok($"Plan '{planId}' deleted.");
        }
    }

    private bool TryGetPlan(string? planId, out PlanState plan, out string error)
    {
        var resolvedPlanId = string.IsNullOrWhiteSpace(planId) ? _activePlanId : planId;
        if (string.IsNullOrWhiteSpace(resolvedPlanId))
        {
            plan = null!;
            error = "No active plan. Provide plan_id.";
            return false;
        }

        if (!_plans.TryGetValue(resolvedPlanId, out plan!))
        {
            error = $"No plan found with ID: {resolvedPlanId}";
            return false;
        }

        error = "";
        return true;
    }

    private static string FormatPlan(PlanState plan)
    {
        var total = plan.Steps.Count;
        var completed = plan.Steps.Count(step => step.Status == "completed");
        var inProgress = plan.Steps.Count(step => step.Status == "in_progress");
        var blocked = plan.Steps.Count(step => step.Status == "blocked");
        var notStarted = plan.Steps.Count(step => step.Status == "not_started");
        var percent = total == 0 ? 0 : completed * 100.0 / total;

        var sb = new StringBuilder();
        sb.AppendLine($"Plan: {plan.Title} (ID: {plan.PlanId})");
        sb.AppendLine($"Progress: {completed}/{total} completed ({percent:0.0}%)");
        sb.AppendLine($"Status: {completed} completed, {inProgress} in progress, {blocked} blocked, {notStarted} not started");
        sb.AppendLine();
        sb.AppendLine("Steps:");

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            sb.AppendLine($"{i}. [{StatusMarker(step.Status)}] {step.Description}");
            if (!string.IsNullOrWhiteSpace(step.Notes))
                sb.AppendLine($"   Notes: {step.Notes}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string StatusMarker(string status) => status switch
    {
        "completed" => "x",
        "in_progress" => "~",
        "blocked" => "!",
        _ => " "
    };

    private sealed record PlanState(string PlanId, string Title, List<PlanStep> Steps);

    private sealed class PlanStep
    {
        public PlanStep(string description)
        {
            Description = description;
        }

        public string Description { get; }
        public string Status { get; set; } = "not_started";
        public string? Notes { get; set; }
    }
}

public sealed class PlanningParameters
{
    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("plan_id")]
    public string? PlanId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("steps")]
    public IReadOnlyList<string>? Steps { get; init; }

    [JsonPropertyName("step_index")]
    public int? StepIndex { get; init; }

    [JsonPropertyName("step_status")]
    public string? StepStatus { get; init; }

    [JsonPropertyName("step_notes")]
    public string? StepNotes { get; init; }
}
