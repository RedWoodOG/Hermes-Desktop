namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Text.Json;

/// <summary>
/// Tool for scheduling cron-based tasks.
/// </summary>
public sealed class ScheduleCronTool : ITool
{
    private readonly ICronScheduler _scheduler;
    
    public string Name => "schedule_cron";
    public string Description => "Schedule a task to run periodically using cron expressions";
    public Type ParametersType => typeof(ScheduleCronParameters);
    
    public ScheduleCronTool(ICronScheduler? scheduler = null)
    {
        _scheduler = scheduler ?? new InMemoryCronScheduler();
    }
    
    public Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (ScheduleCronParameters)parameters;
        
        try
        {
            // Validate cron expression
            try
            {
                Cronos.CronExpression.Parse(p.CronExpression);
            }
            catch (Cronos.CronFormatException)
            {
                return Task.FromResult(ToolResult.Fail($"Invalid cron expression: {p.CronExpression}"));
            }
            
            var task = new CronTask(
                Guid.NewGuid().ToString(),
                p.Name ?? $"task_{DateTime.UtcNow:yyyyMMddHHmmss}",
                p.CronExpression,
                p.Prompt,
                DateTimeOffset.UtcNow,
                p.Recurring,
                p.Durable
            );
            
            _scheduler.Schedule(task);
            
            var nextRun = _scheduler.GetNextRun(task.Id);
            var summary = $"Scheduled task '{task.Name}'\n" +
                         $"Cron: {task.CronExpression}\n" +
                         $"Next run: {nextRun?.ToString("o") ?? "calculating..."}\n" +
                         $"Task ID: {task.Id}";
            
            return Task.FromResult(ToolResult.Ok(summary));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Failed to schedule task: {ex.Message}", ex));
        }
    }
}

/// <summary>
/// Cron scheduler interface.
/// </summary>
public interface ICronScheduler
{
    void Schedule(CronTask task);
    void Cancel(string taskId);
    CronTask? GetTask(string taskId);
    IReadOnlyList<CronTask> GetAllTasks();
    DateTimeOffset? GetNextRun(string taskId);
}

/// <summary>
/// Cron task definition.
/// </summary>
public sealed record CronTask(
    string Id,
    string Name,
    string CronExpression,
    string Prompt,
    DateTimeOffset CreatedAt,
    bool Recurring = true,
    bool Durable = false);

/// <summary>
/// In-memory cron scheduler using Cronos library.
/// </summary>
public sealed class InMemoryCronScheduler : ICronScheduler
{
    private readonly Dictionary<string, CronTask> _tasks = new();
    private readonly Dictionary<string, System.Timers.Timer> _timers = new();
    
    public void Schedule(CronTask task)
    {
        _tasks[task.Id] = task;
        
        // Calculate next run
        var cron = Cronos.CronExpression.Parse(task.CronExpression);
        var nextRun = cron.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        
        if (nextRun.HasValue)
        {
            var delay = (nextRun.Value - DateTimeOffset.UtcNow).TotalMilliseconds;
            if (delay > 0)
            {
                var timer = new System.Timers.Timer(delay);
                timer.Elapsed += async (s, e) =>
                {
                    timer.Dispose();
                    _timers.Remove(task.Id);
                    
                    // Execute task (would integrate with agent system)
                    Console.WriteLine($"[CRON] Executing task: {task.Name}");
                    
                    // Reschedule if recurring
                    if (task.Recurring)
                    {
                        Schedule(task);
                    }
                    else
                    {
                        _tasks.Remove(task.Id);
                    }
                };
                timer.AutoReset = false;
                timer.Start();
                _timers[task.Id] = timer;
            }
        }
    }
    
    public void Cancel(string taskId)
    {
        if (_timers.TryGetValue(taskId, out var timer))
        {
            timer.Stop();
            timer.Dispose();
            _timers.Remove(taskId);
        }
        _tasks.Remove(taskId);
    }
    
    public CronTask? GetTask(string taskId) => _tasks.TryGetValue(taskId, out var task) ? task : null;
    
    public IReadOnlyList<CronTask> GetAllTasks() => _tasks.Values.ToList();
    
    public DateTimeOffset? GetNextRun(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return null;
            
        var cron = Cronos.CronExpression.Parse(task.CronExpression);
        return cron.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
    }
}

public sealed class ScheduleCronParameters
{
    public string? Name { get; init; }
    public required string CronExpression { get; init; }
    public required string Prompt { get; init; }
    public bool Recurring { get; init; } = true;
    public bool Durable { get; init; } = false;
}
