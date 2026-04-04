namespace Hermes.Agent.Coordinator;

using Hermes.Agent.Agents;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Hermes.Agent.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Coordinator Mode - Multi-worker orchestration engine.
/// Breaks complex tasks into subtasks, spawns workers in parallel, monitors, synthesizes.
/// </summary>
public sealed class CoordinatorService
{
    private readonly AgentService _agentService;
    private readonly TaskManager _taskManager;
    private readonly ILogger<CoordinatorService> _logger;
    private readonly IChatClient _chatClient;
    private readonly string _stateDir;

    public CoordinatorService(
        AgentService agentService,
        TaskManager taskManager,
        ILogger<CoordinatorService> logger,
        IChatClient chatClient,
        string stateDir)
    {
        _agentService = agentService;
        _taskManager = taskManager;
        _logger = logger;
        _chatClient = chatClient;
        _stateDir = stateDir;
        Directory.CreateDirectory(stateDir);
    }

    public bool IsCoordinatorMode() =>
        Environment.GetEnvironmentVariable("HERMES_COORDINATOR_MODE") == "true";

    // ── Main Orchestration Entry Point ──

    public async Task<CoordinationResult> RunCoordinatedTaskAsync(string userTask, CancellationToken ct)
    {
        var coordinationId = $"coord_{Guid.NewGuid():N}"[..20];
        _logger.LogInformation("Starting coordinated task {Id}: {Task}", coordinationId, userTask[..Math.Min(80, userTask.Length)]);

        var state = new CoordinationState
        {
            CoordinationId = coordinationId,
            OriginalTask = userTask,
            Phase = TaskWorkflowPhase.Research,
            StartedAt = DateTime.UtcNow
        };

        try
        {
            // Phase 1: Decompose — Break task into subtasks via LLM
            state.Phase = TaskWorkflowPhase.Research;
            await SaveStateAsync(state, ct);

            var subtasks = await DecomposeTaskAsync(userTask, ct);
            _logger.LogInformation("Decomposed into {Count} subtasks", subtasks.Count);

            if (subtasks.Count == 0)
            {
                return new CoordinationResult
                {
                    CoordinationId = coordinationId,
                    Status = "completed",
                    Output = "Task is simple enough to handle directly without decomposition.",
                    SubtaskResults = []
                };
            }

            // Phase 2: Create tasks in TaskManager
            state.Phase = TaskWorkflowPhase.Synthesis;
            var taskIds = new Dictionary<string, string>(); // subtask index → taskId
            foreach (var subtask in subtasks)
            {
                var result = await _taskManager.CreateTaskAsync(new TaskCreateRequest
                {
                    Description = subtask.Description,
                    Priority = subtask.IsBlocking ? TaskPriority.High : TaskPriority.Medium,
                    Dependencies = subtask.DependsOn?.Select(i => taskIds.GetValueOrDefault($"subtask_{i}") ?? "").Where(s => s != "").ToList(),
                    SuccessCriteria = subtask.SuccessCriteria
                }, ct);
                taskIds[$"subtask_{subtask.Index}"] = result.TaskId;
                state.SubtaskMap[subtask.Index] = result.TaskId;
            }
            await SaveStateAsync(state, ct);

            // Phase 3: Spawn workers for independent subtasks
            state.Phase = TaskWorkflowPhase.Implementation;
            var workerResults = new Dictionary<int, string>();
            var pendingSubtasks = new List<Subtask>(subtasks);
            var maxRounds = 10; // Safety limit

            for (var round = 0; round < maxRounds && pendingSubtasks.Count > 0; round++)
            {
                // Find subtasks whose dependencies are all met
                var ready = pendingSubtasks.Where(s =>
                    s.DependsOn is null || s.DependsOn.Count == 0 ||
                    s.DependsOn.All(dep => workerResults.ContainsKey(dep))
                ).ToList();

                if (ready.Count == 0)
                {
                    _logger.LogWarning("No subtasks ready — possible circular dependency");
                    break;
                }

                // Spawn workers in parallel
                var workerTasks = ready.Select(subtask => SpawnWorkerAsync(subtask, workerResults, ct)).ToList();
                var results = await Task.WhenAll(workerTasks);

                // Collect results
                foreach (var (subtask, result) in ready.Zip(results))
                {
                    workerResults[subtask.Index] = result.Output ?? result.Error ?? "No output";
                    pendingSubtasks.Remove(subtask);

                    // Mark task as completed or failed
                    var taskId = state.SubtaskMap.GetValueOrDefault(subtask.Index);
                    if (taskId is not null)
                    {
                        if (result.Status == "completed")
                            await _taskManager.CompleteTaskAsync(taskId, ct);
                        else
                            await _taskManager.FailTaskAsync(taskId, result.Error ?? "Worker failed", ct);
                    }

                    state.WorkerResults[subtask.Index] = result;
                }

                await SaveStateAsync(state, ct);
            }

            // Phase 4: Synthesize results
            state.Phase = TaskWorkflowPhase.Verification;
            var synthesis = await SynthesizeResultsAsync(userTask, subtasks, workerResults, ct);

            state.CompletedAt = DateTime.UtcNow;
            await SaveStateAsync(state, ct);

            _logger.LogInformation("Coordination {Id} completed successfully", coordinationId);
            return new CoordinationResult
            {
                CoordinationId = coordinationId,
                Status = "completed",
                Output = synthesis,
                SubtaskResults = workerResults.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coordination {Id} failed", coordinationId);
            state.Error = ex.Message;
            await SaveStateAsync(state, ct);

            return new CoordinationResult
            {
                CoordinationId = coordinationId,
                Status = "failed",
                Output = $"Coordination failed: {ex.Message}",
                SubtaskResults = state.WorkerResults.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Output ?? "")
            };
        }
    }

    // ── Decompose Task via LLM ──

    private async Task<List<Subtask>> DecomposeTaskAsync(string task, CancellationToken ct)
    {
        var prompt = $@"Break this task into independent subtasks for parallel execution.

TASK: {task}

Return ONLY a JSON array of subtasks in this exact format:
[
  {{
    ""index"": 0,
    ""description"": ""What this subtask does"",
    ""successCriteria"": ""How to verify it's done"",
    ""dependsOn"": [],
    ""isBlocking"": false
  }},
  ...
]

Rules:
- Keep subtasks independent when possible (empty dependsOn)
- Use dependsOn to reference other subtask indexes when order matters
- Mark isBlocking=true for subtasks that other subtasks depend on
- 2-8 subtasks maximum
- Each subtask should be a focused, achievable unit of work";

        var response = await _chatClient.CompleteAsync(
            [new Message { Role = "system", Content = GetCoordinatorSystemPrompt() },
             new Message { Role = "user", Content = prompt }], ct);

        return ParseSubtasks(response);
    }

    // ── Spawn a Worker Agent ──

    private async Task<AgentResult> SpawnWorkerAsync(Subtask subtask, Dictionary<int, string> priorResults, CancellationToken ct)
    {
        var contextFromDeps = "";
        if (subtask.DependsOn is { Count: > 0 })
        {
            var depOutputs = subtask.DependsOn
                .Where(priorResults.ContainsKey)
                .Select(i => $"Result from subtask {i}:\n{priorResults[i]}")
                .ToList();
            if (depOutputs.Count > 0)
                contextFromDeps = "\n\nContext from prior subtasks:\n" + string.Join("\n---\n", depOutputs);
        }

        var workerPrompt = $@"Complete this subtask:

{subtask.Description}

Success criteria: {subtask.SuccessCriteria ?? "Complete the task as described"}
{contextFromDeps}

Provide a clear summary of what you accomplished when done.";

        _logger.LogInformation("Spawning worker for subtask {Index}: {Desc}", subtask.Index, subtask.Description[..Math.Min(60, subtask.Description.Length)]);

        return await _agentService.SpawnAgentAsync(new AgentRequest
        {
            Description = $"Subtask {subtask.Index}",
            Prompt = workerPrompt,
            RunInBackground = false
        }, ct);
    }

    // ── Synthesize Results ──

    private async Task<string> SynthesizeResultsAsync(
        string originalTask,
        List<Subtask> subtasks,
        Dictionary<int, string> results,
        CancellationToken ct)
    {
        var resultSummary = string.Join("\n\n", subtasks.Select(s =>
        {
            var output = results.GetValueOrDefault(s.Index, "(no result)");
            return $"## Subtask {s.Index}: {s.Description}\n{output}";
        }));

        var prompt = $@"You coordinated multiple workers to complete a task. Synthesize their outputs into a cohesive final result.

ORIGINAL TASK: {originalTask}

WORKER RESULTS:
{resultSummary}

Provide a unified, coherent summary of everything that was accomplished. Highlight any issues or incomplete items.";

        return await _chatClient.CompleteAsync(
            [new Message { Role = "user", Content = prompt }], ct);
    }

    // ── System Prompt ──

    public string GetCoordinatorSystemPrompt() => @"You are a COORDINATOR orchestrating multiple worker agents.

## Your Role
- Break complex tasks into focused subtasks
- Identify dependencies between subtasks
- Assign subtasks to parallel workers
- Monitor progress and handle failures
- Synthesize worker outputs into cohesive results

## Workflow
1. Research: Understand the full scope
2. Decompose: Break into 2-8 subtasks with clear success criteria
3. Execute: Spawn workers in parallel (respecting dependencies)
4. Synthesize: Combine outputs into final result

## Rules
- Maximize parallelism — only add dependencies when truly needed
- Each subtask should be self-contained and achievable
- Include success criteria so completion can be verified
- Keep subtask count reasonable (2-8)";

    public Dictionary<string, string> GetCoordinatorUserContext(
        List<string> workerTools, List<string> mcpServers, string? scratchpadDir)
    {
        var context = new Dictionary<string, string>
        {
            ["workerToolsContext"] = $"Available tools: {string.Join(", ", workerTools)}\nMCP Servers: {string.Join(", ", mcpServers)}"
        };
        if (scratchpadDir is not null) context["scratchpad"] = scratchpadDir;
        return context;
    }

    public string? MatchSessionMode(string? sessionMode)
    {
        var current = IsCoordinatorMode() ? "coordinator" : "normal";
        if (current == sessionMode) return null;

        Environment.SetEnvironmentVariable("HERMES_COORDINATOR_MODE", sessionMode == "coordinator" ? "true" : null);
        _logger.LogInformation("Switched to {Mode} mode", sessionMode);
        return $"Switched to {sessionMode} mode";
    }

    // ── State Persistence ──

    private async Task SaveStateAsync(CoordinationState state, CancellationToken ct)
    {
        var path = Path.Combine(_stateDir, $"{state.CoordinationId}.json");
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
        await File.WriteAllTextAsync(path, json, ct);
    }

    public async Task<CoordinationState?> LoadStateAsync(string coordinationId, CancellationToken ct)
    {
        var path = Path.Combine(_stateDir, $"{coordinationId}.json");
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<CoordinationState>(json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    // ── Parse Subtasks from LLM Response ──

    private static List<Subtask> ParseSubtasks(string response)
    {
        // Extract JSON array from response (may be wrapped in markdown code block)
        var jsonMatch = Regex.Match(response, @"\[[\s\S]*?\]");
        if (!jsonMatch.Success) return [];

        try
        {
            return JsonSerializer.Deserialize<List<Subtask>>(jsonMatch.Value,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? [];
        }
        catch
        {
            return [];
        }
    }
}

// ── Types ──

public sealed class Subtask
{
    public int Index { get; set; }
    public required string Description { get; set; }
    public string? SuccessCriteria { get; set; }
    public List<int>? DependsOn { get; set; }
    public bool IsBlocking { get; set; }
}

public sealed class CoordinationState
{
    public required string CoordinationId { get; init; }
    public required string OriginalTask { get; init; }
    public TaskWorkflowPhase Phase { get; set; }
    public Dictionary<int, string> SubtaskMap { get; init; } = new(); // subtask index → taskId
    public Dictionary<int, AgentResult> WorkerResults { get; init; } = new();
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class CoordinationResult
{
    public required string CoordinationId { get; init; }
    public required string Status { get; init; }
    public string? Output { get; init; }
    public Dictionary<string, string> SubtaskResults { get; init; } = new();
}

public enum TaskWorkflowPhase
{
    Research,
    Synthesis,
    Implementation,
    Verification
}
