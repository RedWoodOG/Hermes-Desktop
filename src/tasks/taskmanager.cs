namespace Hermes.Agent.Tasks;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

// ── Enums ──

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Blocked
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskPriority
{
    Low,
    Medium,
    High,
    Critical
}

// ── Task Manager ──

public sealed class TaskManager
{
    private readonly string _tasksDir;
    private readonly ILogger<TaskManager> _logger;
    private readonly ConcurrentDictionary<string, HermesTask> _tasks = new();

    public TaskManager(string tasksDir, ILogger<TaskManager> logger)
    {
        _tasksDir = tasksDir;
        _logger = logger;
        Directory.CreateDirectory(tasksDir);
        LoadTasks();
    }

    // ── Create ──

    public async Task<TaskResult> CreateTaskAsync(TaskCreateRequest request, CancellationToken ct)
    {
        var taskId = $"task_{Guid.NewGuid():N}"[..20];
        var task = new HermesTask
        {
            TaskId = taskId,
            Description = request.Description,
            Status = request.Status ?? TaskStatus.Pending,
            Priority = request.Priority ?? TaskPriority.Medium,
            Assignee = request.Assignee,
            DueDate = request.DueDate,
            Dependencies = request.Dependencies ?? new List<string>(),
            SuccessCriteria = request.SuccessCriteria,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Auto-block if any dependency is not yet completed
        if (task.Dependencies.Count > 0 && !AllDependenciesMet(task.Dependencies))
            task.Status = TaskStatus.Blocked;

        _tasks[taskId] = task;
        await SaveTaskAsync(task, ct);

        _logger.LogInformation("Created task {TaskId}: {Description} [{Status}]", taskId, task.Description, task.Status);
        return new TaskResult { TaskId = taskId, Status = task.Status, Message = $"Task created: {task.Description}" };
    }

    // ── Read ──

    public HermesTask? GetTask(string taskId) => _tasks.TryGetValue(taskId, out var task) ? task : null;

    public HermesTask GetTaskOrThrow(string taskId) =>
        _tasks.TryGetValue(taskId, out var task) ? task : throw new TaskNotFoundException(taskId);

    public List<HermesTask> ListTasks() => _tasks.Values.OrderByDescending(t => t.CreatedAt).ToList();

    // ── Update ──

    public async Task<TaskResult> UpdateTaskAsync(string taskId, TaskUpdateRequest request, CancellationToken ct)
    {
        var task = GetTaskOrThrow(taskId);

        if (request.Description is not null) task.Description = request.Description;
        if (request.Priority is not null) task.Priority = request.Priority.Value;
        if (request.Assignee is not null) task.Assignee = request.Assignee;
        if (request.DueDate is not null) task.DueDate = request.DueDate;
        if (request.SuccessCriteria is not null) task.SuccessCriteria = request.SuccessCriteria;
        if (request.Dependencies is not null)
        {
            task.Dependencies = request.Dependencies;
            // Re-evaluate blocked status
            if (task.Status == TaskStatus.Blocked && AllDependenciesMet(task.Dependencies))
                task.Status = TaskStatus.Pending;
            else if (task.Status == TaskStatus.Pending && !AllDependenciesMet(task.Dependencies))
                task.Status = TaskStatus.Blocked;
        }

        task.UpdatedAt = DateTime.UtcNow;
        await SaveTaskAsync(task, ct);

        _logger.LogInformation("Updated task {TaskId}", taskId);
        return new TaskResult { TaskId = taskId, Status = task.Status, Message = "Task updated" };
    }

    // ── Delete ──

    public async Task DeleteTaskAsync(string taskId, CancellationToken ct)
    {
        if (!_tasks.TryRemove(taskId, out _))
            throw new TaskNotFoundException(taskId);

        var path = Path.Combine(_tasksDir, $"{taskId}.json");
        if (File.Exists(path))
            File.Delete(path);

        _logger.LogInformation("Deleted task {TaskId}", taskId);
        await Task.CompletedTask;
    }

    // ── Complete ──

    public async Task<TaskResult> CompleteTaskAsync(string taskId, CancellationToken ct)
    {
        var task = GetTaskOrThrow(taskId);
        task.Status = TaskStatus.Completed;
        task.CompletedAt = DateTime.UtcNow;
        task.UpdatedAt = DateTime.UtcNow;
        await SaveTaskAsync(task, ct);

        _logger.LogInformation("Completed task {TaskId}", taskId);

        // Unblock tasks that depended on this one
        var unblocked = await UnblockDependentsAsync(taskId, ct);
        if (unblocked > 0)
            _logger.LogInformation("Unblocked {Count} tasks after completing {TaskId}", unblocked, taskId);

        return new TaskResult { TaskId = taskId, Status = task.Status, Message = $"Task completed. {unblocked} dependent task(s) unblocked." };
    }

    // ── Fail ──

    public async Task<TaskResult> FailTaskAsync(string taskId, string reason, CancellationToken ct)
    {
        var task = GetTaskOrThrow(taskId);
        task.Status = TaskStatus.Failed;
        task.FailureReason = reason;
        task.UpdatedAt = DateTime.UtcNow;
        await SaveTaskAsync(task, ct);

        _logger.LogWarning("Failed task {TaskId}: {Reason}", taskId, reason);
        return new TaskResult { TaskId = taskId, Status = task.Status, Message = $"Task failed: {reason}" };
    }

    // ── Dependencies ──

    public TaskDependencyInfo GetDependencies(string taskId)
    {
        var task = GetTaskOrThrow(taskId);
        var blockedBy = task.Dependencies
            .Select(id => _tasks.TryGetValue(id, out var t) ? t : null)
            .Where(t => t is not null)
            .ToList()!;

        var blocking = _tasks.Values
            .Where(t => t.Dependencies.Contains(taskId))
            .ToList();

        return new TaskDependencyInfo { BlockedBy = blockedBy!, Blocking = blocking };
    }

    public bool CanStart(string taskId)
    {
        var task = GetTaskOrThrow(taskId);
        return AllDependenciesMet(task.Dependencies);
    }

    // ── Ordering ──

    public List<HermesTask> GetOrderedTasks()
    {
        return _tasks.Values
            .OrderBy(t => t.Status == TaskStatus.Completed ? 1 : 0) // Active first
            .ThenBy(t => CanStart(t.TaskId) ? 0 : 1)               // Startable first
            .ThenByDescending(t => t.Priority)                       // Critical > High > Medium > Low
            .ThenBy(t => t.DueDate ?? DateTime.MaxValue)             // Earliest due date first
            .ToList();
    }

    public List<HermesTask> GetOverdueTasks()
    {
        var now = DateTime.UtcNow;
        return _tasks.Values
            .Where(t => t.DueDate.HasValue && t.DueDate.Value < now && t.Status != TaskStatus.Completed)
            .OrderBy(t => t.DueDate)
            .ToList();
    }

    // ── Helpers ──

    private bool AllDependenciesMet(List<string> deps) =>
        deps.All(depId => _tasks.TryGetValue(depId, out var dep) && dep.Status == TaskStatus.Completed);

    private async Task<int> UnblockDependentsAsync(string completedTaskId, CancellationToken ct)
    {
        var unblocked = 0;
        foreach (var task in _tasks.Values.Where(t => t.Status == TaskStatus.Blocked && t.Dependencies.Contains(completedTaskId)))
        {
            if (AllDependenciesMet(task.Dependencies))
            {
                task.Status = TaskStatus.Pending;
                task.UpdatedAt = DateTime.UtcNow;
                await SaveTaskAsync(task, ct);
                unblocked++;
            }
        }
        return unblocked;
    }

    private void LoadTasks()
    {
        if (!Directory.Exists(_tasksDir)) return;
        foreach (var file in Directory.EnumerateFiles(_tasksDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var task = JsonSerializer.Deserialize<HermesTask>(json, JsonOpts);
                if (task is not null) _tasks[task.TaskId] = task;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to load task from {File}", file); }
        }
    }

    private Task SaveTaskAsync(HermesTask task, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(task, JsonOpts);
        return File.WriteAllTextAsync(Path.Combine(_tasksDir, $"{task.TaskId}.json"), json, ct);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

// ── Models ──

public sealed class HermesTask
{
    public required string TaskId { get; init; }
    public required string Description { get; set; }
    public TaskStatus Status { get; set; } = TaskStatus.Pending;
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public string? Assignee { get; set; }
    public DateTime? DueDate { get; set; }
    public List<string> Dependencies { get; set; } = new List<string>();
    public string? SuccessCriteria { get; set; }
    public string? FailureReason { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; set; }

    [JsonIgnore]
    public bool IsOverdue => DueDate.HasValue && DueDate.Value < DateTime.UtcNow && Status != TaskStatus.Completed;

    [JsonIgnore]
    public bool DueSoon => DueDate.HasValue && DueDate.Value < DateTime.UtcNow.AddHours(24) && !IsOverdue && Status != TaskStatus.Completed;
}

public sealed class TaskCreateRequest
{
    public required string Description { get; init; }
    public TaskStatus? Status { get; init; }
    public TaskPriority? Priority { get; init; }
    public string? Assignee { get; init; }
    public DateTime? DueDate { get; init; }
    public List<string>? Dependencies { get; init; }
    public string? SuccessCriteria { get; init; }
}

public sealed class TaskUpdateRequest
{
    public string? Description { get; init; }
    public TaskPriority? Priority { get; init; }
    public string? Assignee { get; init; }
    public DateTime? DueDate { get; init; }
    public List<string>? Dependencies { get; init; }
    public string? SuccessCriteria { get; init; }
}

public sealed class TaskResult
{
    public required string TaskId { get; init; }
    public required TaskStatus Status { get; init; }
    public required string Message { get; init; }
}

public sealed class TaskDependencyInfo
{
    public required List<HermesTask> BlockedBy { get; init; }
    public required List<HermesTask> Blocking { get; init; }
}

public sealed class TaskNotFoundException(string taskId) : Exception($"Task '{taskId}' not found");
