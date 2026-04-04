namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Text.Json;

/// <summary>
/// Tool for managing a todo list during task execution.
/// </summary>
public sealed class TodoWriteTool : ITool
{
    private readonly ITodoStore _store;
    
    public string Name => "todo_write";
    public string Description => "Create, update, or manage a todo list for tracking task progress";
    public Type ParametersType => typeof(TodoWriteParameters);
    
    public TodoWriteTool(ITodoStore? store = null)
    {
        _store = store ?? new InMemoryTodoStore();
    }
    
    public Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (TodoWriteParameters)parameters;
        
        try
        {
            var todos = p.Todos.Select(t => new TodoItem(
                t.Id ?? Guid.NewGuid().ToString(),
                t.Content,
                Enum.Parse<TodoStatus>(t.Status ?? "pending", true),
                Enum.Parse<TodoPriority>(t.Priority ?? "medium", true)
            )).ToList();
            
            _store.SetTodos(todos);
            
            var summary = FormatTodoList(todos);
            return Task.FromResult(ToolResult.Ok(summary));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Failed to update todos: {ex.Message}", ex));
        }
    }
    
    private static string FormatTodoList(IReadOnlyList<TodoItem> todos)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Todo list updated:");
        
        foreach (var todo in todos)
        {
            var status = todo.Status switch
            {
                TodoStatus.Completed => "✓",
                TodoStatus.InProgress => "►",
                _ => "○"
            };
            
            var priority = todo.Priority switch
            {
                TodoPriority.High => " [HIGH]",
                TodoPriority.Low => " [low]",
                _ => ""
            };
            
            sb.AppendLine($"  {status} {todo.Content}{priority}");
        }
        
        var completed = todos.Count(t => t.Status == TodoStatus.Completed);
        var total = todos.Count;
        sb.AppendLine($"\nProgress: {completed}/{total} completed");
        
        return sb.ToString();
    }
}

/// <summary>
/// Todo item storage interface.
/// </summary>
public interface ITodoStore
{
    IReadOnlyList<TodoItem> GetTodos();
    void SetTodos(IReadOnlyList<TodoItem> todos);
    void AddTodo(TodoItem item);
    void UpdateTodo(string id, TodoStatus status);
}

/// <summary>
/// In-memory todo store implementation.
/// </summary>
public sealed class InMemoryTodoStore : ITodoStore
{
    private List<TodoItem> _todos = new();
    
    public IReadOnlyList<TodoItem> GetTodos() => _todos;
    
    public void SetTodos(IReadOnlyList<TodoItem> todos) => _todos = todos.ToList();
    
    public void AddTodo(TodoItem item) => _todos.Add(item);
    
    public void UpdateTodo(string id, TodoStatus status)
    {
        var index = _todos.FindIndex(t => t.Id == id);
        if (index >= 0)
        {
            _todos[index] = _todos[index] with { Status = status };
        }
    }
}

/// <summary>
/// Todo item.
/// </summary>
public sealed record TodoItem(
    string Id,
    string Content,
    TodoStatus Status = TodoStatus.Pending,
    TodoPriority Priority = TodoPriority.Medium);

public enum TodoStatus
{
    Pending,
    InProgress,
    Completed
}

public enum TodoPriority
{
    High,
    Medium,
    Low
}

public sealed class TodoWriteParameters
{
    public IReadOnlyList<TodoItemInput> Todos { get; init; } = Array.Empty<TodoItemInput>();
}

public sealed class TodoItemInput
{
    public string? Id { get; init; }
    public required string Content { get; init; }
    public string? Status { get; init; }
    public string? Priority { get; init; }
}
