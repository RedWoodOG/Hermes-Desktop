namespace Hermes.Agent.Commands;

using System.Text;

/// <summary>
/// Metadata-driven command registry for slash commands and future command palettes.
/// Inspired by DeepSeek-TUI's shared slash-command / command-palette catalog.
/// </summary>
public sealed class CommandRegistry<TContext>
{
    private readonly Dictionary<string, RegisteredCommand<TContext>> _commands = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<RegisteredCommand<TContext>> Commands =>
        _commands.Values.DistinctBy(c => c.Name).OrderBy(c => c.Name).ToList();

    public CommandRegistry<TContext> Register(RegisteredCommand<TContext> command)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ArgumentException("Command name is required.", nameof(command));

        AddAlias(command.Name, command);
        foreach (var alias in command.Aliases)
            AddAlias(alias, command);
        return this;
    }

    public bool TryGet(string name, out RegisteredCommand<TContext> command) =>
        _commands.TryGetValue(Normalize(name), out command!);

    public static ParsedCommand Parse(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.StartsWith("/", StringComparison.Ordinal))
            trimmed = trimmed[1..];

        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0
            ? new ParsedCommand("", "")
            : new ParsedCommand(parts[0], parts.Length > 1 ? parts[1] : "");
    }

    public async Task<bool> TryExecuteAsync(string input, TContext context, CancellationToken ct)
    {
        var parsed = Parse(input);
        if (!TryGet(parsed.Name, out var command))
            return false;

        await command.ExecuteAsync(context, parsed.Arguments, ct);
        return true;
    }

    public string FormatHelp(string header = "Available slash commands:")
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);
        foreach (var command in Commands)
        {
            var aliases = command.Aliases.Count == 0
                ? ""
                : $" ({string.Join(", ", command.Aliases.Select(a => "/" + a))})";
            var usage = string.IsNullOrWhiteSpace(command.Usage) ? "/" + command.Name : command.Usage;
            sb.AppendLine($"  {usage}{aliases} - {command.Description}");
        }
        return sb.ToString().TrimEnd();
    }

    private void AddAlias(string alias, RegisteredCommand<TContext> command)
    {
        var normalized = Normalize(alias);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Command alias cannot be empty.", nameof(command));
        _commands[normalized] = command;
    }

    private static string Normalize(string value) =>
        value.Trim().TrimStart('/').ToLowerInvariant();
}

public sealed class RegisteredCommand<TContext>
{
    public RegisteredCommand(
        string name,
        string description,
        Func<TContext, string, CancellationToken, Task> executeAsync,
        string? usage = null,
        IReadOnlyList<string>? aliases = null,
        string category = "general")
    {
        Name = name;
        Description = description;
        ExecuteAsync = executeAsync;
        Usage = usage;
        Aliases = aliases ?? [];
        Category = category;
    }

    public string Name { get; }
    public string Description { get; }
    public Func<TContext, string, CancellationToken, Task> ExecuteAsync { get; }
    public string? Usage { get; }
    public IReadOnlyList<string> Aliases { get; }
    public string Category { get; }
}

public sealed record ParsedCommand(string Name, string Arguments);
