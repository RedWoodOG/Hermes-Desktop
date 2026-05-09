namespace Hermes.Agent.Core;

using System.Text;
using System.Text.Json;

public sealed record PostEditDiagnostic(
    string FilePath,
    int Line,
    int Column,
    string Message,
    PostEditDiagnosticSeverity Severity);

public enum PostEditDiagnosticSeverity
{
    Error,
    Warning,
    Information,
    Hint
}

public interface IPostEditDiagnosticsProvider
{
    Task<IReadOnlyList<PostEditDiagnostic>> GetDiagnosticsAsync(
        IReadOnlyList<string> changedFilePaths,
        CancellationToken ct);
}

public sealed class NoOpPostEditDiagnosticsProvider : IPostEditDiagnosticsProvider
{
    public Task<IReadOnlyList<PostEditDiagnostic>> GetDiagnosticsAsync(
        IReadOnlyList<string> changedFilePaths,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PostEditDiagnostic>>(Array.Empty<PostEditDiagnostic>());
}

public sealed class PostEditDiagnosticsOptions
{
    public bool Enabled { get; init; }
    public bool IncludeNoDiagnosticsMessage { get; init; }
    public bool IncludeDisabledMessage { get; init; }
}

public sealed class PostEditDiagnosticsHook
{
    private static readonly HashSet<string> FileMutatingToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "edit_file",
        "write_file",
        "patch"
    };

    private readonly IPostEditDiagnosticsProvider _provider;
    private readonly PostEditDiagnosticsOptions _options;

    public PostEditDiagnosticsHook(
        IPostEditDiagnosticsProvider? provider = null,
        PostEditDiagnosticsOptions? options = null)
    {
        _provider = provider ?? new NoOpPostEditDiagnosticsProvider();
        _options = options ?? new PostEditDiagnosticsOptions();
    }

    public async Task<string?> BuildReportAsync(ToolCall toolCall, ToolResult result, CancellationToken ct)
    {
        if (!result.Success)
            return null;

        var changedFilePaths = DetectChangedFilePaths(toolCall.Name, toolCall.Arguments);
        if (changedFilePaths.Count == 0)
            return null;

        if (!_options.Enabled)
        {
            return _options.IncludeDisabledMessage
                ? FormatDisabled(changedFilePaths)
                : null;
        }

        var diagnostics = await _provider.GetDiagnosticsAsync(changedFilePaths, ct);
        if (diagnostics.Count == 0)
        {
            return _options.IncludeNoDiagnosticsMessage
                ? FormatNoDiagnostics(changedFilePaths)
                : null;
        }

        return FormatDiagnostics(diagnostics);
    }

    public static IReadOnlyList<string> DetectChangedFilePaths(string toolName, string arguments)
    {
        if (!FileMutatingToolNames.Contains(toolName))
            return Array.Empty<string>();

        try
        {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Array.Empty<string>();

            var paths = new List<string>();
            AddPathIfPresent(doc.RootElement, "filePath", paths);
            AddPathIfPresent(doc.RootElement, "file_path", paths);

            return paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    public static string FormatDiagnostics(IReadOnlyList<PostEditDiagnostic> diagnostics)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Post-edit diagnostics:");
        foreach (var diagnostic in diagnostics)
        {
            sb.AppendLine(
                $"[{FormatSeverity(diagnostic.Severity)}] {diagnostic.FilePath}:{diagnostic.Line}:{diagnostic.Column}: {diagnostic.Message}");
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatNoDiagnostics(IReadOnlyList<string> changedFilePaths) =>
        FormatPathList("Post-edit diagnostics: no diagnostics for changed file(s):", changedFilePaths);

    public static string FormatDisabled(IReadOnlyList<string> changedFilePaths) =>
        FormatPathList("Post-edit diagnostics disabled for changed file(s):", changedFilePaths);

    private static void AddPathIfPresent(JsonElement root, string propertyName, List<string> paths)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var path = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
            }
        }
    }

    private static string FormatPathList(string header, IReadOnlyList<string> changedFilePaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);
        foreach (var path in changedFilePaths)
            sb.AppendLine($"- {path}");
        return sb.ToString().TrimEnd();
    }

    private static string FormatSeverity(PostEditDiagnosticSeverity severity) =>
        severity switch
        {
            PostEditDiagnosticSeverity.Error => "ERROR",
            PostEditDiagnosticSeverity.Warning => "WARN",
            PostEditDiagnosticSeverity.Information => "INFO",
            _ => "HINT"
        };
}
