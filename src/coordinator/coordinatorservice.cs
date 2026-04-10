namespace Hermes.Agent.Coordinator;

using Hermes.Agent.Agents;
using Hermes.Agent.Briefs;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Hermes.Agent.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;

/// <summary>
/// Coordinator Mode - Multi-worker orchestration engine.
/// Breaks complex tasks into subtasks, spawns workers in parallel, monitors, synthesizes.
/// When a TaskBrief is provided, uses its explicit structure instead of LLM decomposition.
/// </summary>
public sealed class CoordinatorService
{
    private readonly AgentService _agentService;
    private readonly TaskManager _taskManager;
    private readonly ILogger<CoordinatorService> _logger;
    private readonly IChatClient _chatClient;
    private readonly string _stateDir;
    private BriefService? _briefService;

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

    /// <summary>
    /// Attach a BriefService for brief-driven orchestration.
    /// </summary>
    public void SetBriefService(BriefService briefService) => _briefService = briefService;

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
            await SaveStateAsync(state, CancellationToken.None); // Don't use ct — it may be cancelled

            return new CoordinationResult
            {
                CoordinationId = coordinationId,
                Status = "failed",
                Output = $"Coordination failed: {ex.Message}",
                SubtaskResults = state.WorkerResults.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Output ?? "")
            };
        }
    }

    // ── Brief-Driven Orchestration ──

    /// <summary>
    /// Execute a task using an approved TaskBrief. Instead of LLM decomposition,
    /// the brief's explicit agent configuration drives spawning.
    /// This is the preferred path for local/open-source models.
    /// </summary>
    public async Task<CoordinationResult> RunBriefAsync(TaskBrief brief, CancellationToken ct)
    {
        if (_briefService is null)
            throw new InvalidOperationException("BriefService not configured. Call SetBriefService first.");

        if (brief.Status != BriefStatus.Approved)
            throw new InvalidOperationException($"Brief {brief.Id} is not approved (status: {brief.Status})");

        var coordinationId = $"coord_{Guid.NewGuid():N}"[..20];
        _logger.LogInformation("Starting brief-driven task {CoordId} for brief {BriefId}: {Title}",
            coordinationId, brief.Id, brief.Title);

        TaskResult? taskResult = null;
        CoordinationState? state = null;

        try
        {
            // Mark brief as in-progress
            await _briefService.UpdateBriefAsync(brief.Id, b => b.Status = BriefStatus.InProgress, ct);

            // Create linked task in TaskManager
            taskResult = await _taskManager.CreateTaskAsync(new TaskCreateRequest
            {
                Description = $"[{brief.Id}] {brief.Title}",
                Priority = TaskPriority.High,
                SuccessCriteria = brief.VerifyChecklist.Count > 0
                    ? string.Join("; ", brief.VerifyChecklist)
                    : brief.Objective
            }, ct);
            await _briefService.UpdateBriefAsync(brief.Id, b => b.LinkedTaskId = taskResult.TaskId, ct);

            state = new CoordinationState
            {
                CoordinationId = coordinationId,
                OriginalTask = _briefService.BuildCoordinatorPrompt(brief),
                Phase = TaskWorkflowPhase.Implementation,
                StartedAt = DateTime.UtcNow
            };
            await SaveStateAsync(state, ct);
            // Spawn agents per brief configuration (no LLM decomposition needed)
            var workerResults = new Dictionary<int, string>();

            for (var i = 0; i < brief.AgentCount; i++)
            {
                var role = i < brief.AgentRoles.Count ? brief.AgentRoles[i] : $"worker-{i + 1}";
                var agentPrompt = _briefService.BuildAgentPrompt(brief, role, i);

                // Pass prior agent results as context for sequential dependencies
                var contextFromPrior = "";
                if (workerResults.Count > 0)
                {
                    contextFromPrior = "\n\n## CONTEXT FROM PRIOR AGENTS:\n" +
                        string.Join("\n---\n", workerResults.Select(kv =>
                            $"Agent {kv.Key + 1} output:\n{kv.Value}"));
                }

                _logger.LogInformation("Spawning agent {Index}/{Total} role={Role} for brief {BriefId}",
                    i + 1, brief.AgentCount, role, brief.Id);

                var result = await _agentService.SpawnAgentAsync(new AgentRequest
                {
                    Description = $"[{brief.Id}] {role}",
                    Prompt = agentPrompt + contextFromPrior,
                    RunInBackground = false
                }, ct);

                workerResults[i] = result.Output ?? result.Error ?? "No output";
                state.WorkerResults[i] = result;
                await SaveStateAsync(state, ct);
            }

            // Synthesize results — use AgentCount (not AgentRoles) to include fallback workers
            var finalOutput = workerResults.Count == 1
                ? workerResults[0]
                : await SynthesizeResultsAsync(brief.Objective,
                    Enumerable.Range(0, brief.AgentCount).Select(i =>
                    {
                        var role = i < brief.AgentRoles.Count ? brief.AgentRoles[i] : $"worker-{i + 1}";
                        return new Subtask
                        {
                            Index = i,
                            Description = role,
                            SuccessCriteria = brief.Objective
                        };
                    }).ToList(),
                    workerResults, ct);

            // Verify output against brief's criteria
            state.Phase = TaskWorkflowPhase.Verification;
            var verification = await _briefService.VerifyOutputAsync(brief, finalOutput, ct);

            if (verification.RequiresManualReview)
            {
                // Manual review — mark brief as blocked (pending review), don't fail the task
                await _briefService.UpdateBriefAsync(brief.Id, b => b.Status = BriefStatus.Blocked, ct);
                _logger.LogInformation("Brief {BriefId} awaiting manual review by {EscalateTo}",
                    brief.Id, brief.EscalateTo ?? "user");
            }
            else if (verification.Passed)
            {
                await _briefService.UpdateBriefAsync(brief.Id, b => b.Status = BriefStatus.Completed, ct);
                await _taskManager.CompleteTaskAsync(taskResult.TaskId, ct);
                _logger.LogInformation("Brief {BriefId} completed and verified: {Details}", brief.Id, verification.Details);
            }
            else
            {
                await _briefService.UpdateBriefAsync(brief.Id, b => b.Status = BriefStatus.Failed, ct);
                await _taskManager.FailTaskAsync(taskResult.TaskId, $"Verification failed: {verification.Details}", ct);
                _logger.LogWarning("Brief {BriefId} verification failed: {Details}", brief.Id, verification.Details);
            }

            state.CompletedAt = DateTime.UtcNow;
            await SaveStateAsync(state, ct);

            return new CoordinationResult
            {
                CoordinationId = coordinationId,
                Status = verification.RequiresManualReview ? "awaiting_review"
                    : verification.Passed ? "completed" : "verification_failed",
                Output = finalOutput,
                SubtaskResults = workerResults.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Brief {BriefId} execution failed", brief.Id);
            // Use CancellationToken.None — ct may be cancelled, and cleanup must persist
            await _briefService.UpdateBriefAsync(brief.Id, b => b.Status = BriefStatus.Failed, CancellationToken.None);
            if (taskResult is not null)
                await _taskManager.FailTaskAsync(taskResult.TaskId, ex.Message, CancellationToken.None);

            // Escalate if configured
            if (!string.IsNullOrWhiteSpace(brief.EscalateTo))
                _logger.LogWarning("ESCALATION: Brief {BriefId} failed — notify {EscalateTo}: {Error}",
                    brief.Id, brief.EscalateTo, ex.Message);

            if (state is not null)
            {
                state.Error = ex.Message;
                await SaveStateAsync(state, CancellationToken.None);
            }

            return new CoordinationResult
            {
                CoordinationId = coordinationId,
                Status = "failed",
                Output = $"Brief execution failed: {ex.Message}",
                SubtaskResults = state?.WorkerResults.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Output ?? "") ?? new()
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
        var path = GetSafeStatePath(state.CoordinationId);
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
        await File.WriteAllTextAsync(path, json, ct);
    }

    public async Task<CoordinationState?> LoadStateAsync(string coordinationId, CancellationToken ct)
    {
        var path = GetSafeStatePath(coordinationId);
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<CoordinationState>(json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    /// <summary>
    /// Returns a safe file path for a coordination ID, preventing path traversal attacks.
    /// </summary>
    private string GetSafeStatePath(string coordinationId)
    {
        // Strip any path separators or invalid chars to prevent traversal
        var safeId = Path.GetFileName(coordinationId);
        if (string.IsNullOrWhiteSpace(safeId) || safeId != coordinationId)
            throw new ArgumentException($"Invalid coordination ID: '{coordinationId}'");

        var path = Path.GetFullPath(Path.Combine(_stateDir, $"{safeId}.json"));
        var stateRoot = Path.GetFullPath(_stateDir);
        if (!path.StartsWith(stateRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Coordination ID resolves outside state directory: '{coordinationId}'");

        return path;
    }

    // ── Parse Subtasks from LLM Response ──

    private static List<Subtask> ParseSubtasks(string response)
    {
        for (var start = response.IndexOf('['); start >= 0; start = response.IndexOf('[', start + 1))
        {
            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var i = start; i < response.Length; i++)
            {
                var ch = response[i];

                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (ch == '\\') { escaped = true; continue; }
                    if (ch == '"') inString = false;
                    continue;
                }

                if (ch == '"') { inString = true; continue; }
                if (ch == '[') depth++;
                else if (ch == ']' && --depth == 0)
                {
                    var candidate = response[start..(i + 1)];
                    try
                    {
                        return JsonSerializer.Deserialize<List<Subtask>>(candidate,
                            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? [];
                    }
                    catch (JsonException)
                    {
                        break; // try next '[' candidate
                    }
                }
            }
        }

        return [];
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
