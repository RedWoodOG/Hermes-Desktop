namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

/// <summary>
/// Tool for Language Server Protocol integration.
/// Provides code intelligence features like go-to-definition, references, etc.
/// </summary>
public sealed class LspTool : ITool
{
    private readonly Dictionary<string, LspClient> _clients = new();
    private readonly LspConfig _config;
    
    public string Name => "lsp";
    public string Description => "Query Language Server Protocol for code intelligence (definitions, references, completions, diagnostics)";
    public Type ParametersType => typeof(LspParameters);
    
    public LspTool(LspConfig? config = null)
    {
        _config = config ?? new LspConfig();
    }
    
    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (LspParameters)parameters;
        
        try
        {
            var client = await GetOrCreateClientAsync(p.Language, p.RootPath, ct);
            
            var result = p.Action.ToLowerInvariant() switch
            {
                "definition" => await GetDefinitionAsync(client, p, ct),
                "references" => await GetReferencesAsync(client, p, ct),
                "hover" => await GetHoverAsync(client, p, ct),
                "completion" => await GetCompletionsAsync(client, p, ct),
                "diagnostics" => await GetDiagnosticsAsync(client, p, ct),
                "symbols" => await GetDocumentSymbolsAsync(client, p, ct),
                "rename" => await GetRenameEditsAsync(client, p, ct),
                _ => throw new NotSupportedException($"Unknown LSP action: {p.Action}")
            };
            
            return ToolResult.Ok(result);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"LSP operation failed: {ex.Message}", ex);
        }
    }
    
    private async Task<LspClient> GetOrCreateClientAsync(string language, string rootPath, CancellationToken ct)
    {
        var key = $"{language}:{rootPath}";
        
        if (_clients.TryGetValue(key, out var client))
            return client;
        
        var serverConfig = _config.GetServerConfig(language);
        if (serverConfig is null)
            throw new InvalidOperationException($"No LSP server configured for language: {language}");
        
        client = new LspClient(serverConfig, rootPath);
        await client.InitializeAsync(ct);
        
        _clients[key] = client;
        return client;
    }
    
    private static async Task<string> GetDefinitionAsync(LspClient client, LspParameters p, CancellationToken ct)
    {
        var result = await client.GoToDefinitionAsync(p.FilePath, p.Line ?? 0, p.Column ?? 0, ct);
        return FormatLocations(result);
    }
    
    private static async Task<string> GetReferencesAsync(LspClient client, LspParameters p, CancellationToken ct)
    {
        var result = await client.FindReferencesAsync(p.FilePath, p.Line ?? 0, p.Column ?? 0, ct);
        return FormatLocations(result);
    }
    
    private static async Task<string> GetHoverAsync(LspClient client, LspParameters p, CancellationToken ct)
    {
        var result = await client.GetHoverAsync(p.FilePath, p.Line ?? 0, p.Column ?? 0, ct);
        return result ?? "No hover information available";
    }
    
    private static async Task<string> GetCompletionsAsync(LspClient client, LspParameters p, CancellationToken ct)
    {
        var result = await client.GetCompletionsAsync(p.FilePath, p.Line ?? 0, p.Column ?? 0, ct);
        return FormatCompletions(result);
    }
    
    private static async Task<string> GetDiagnosticsAsync(LspClient client, LspParameters p, CancellationToken ct)
    {
        var result = await client.GetDiagnosticsAsync(p.FilePath, ct);
        return FormatDiagnostics(result);
    }
    
    private static async Task<string> GetDocumentSymbolsAsync(LspClient client, LspParameters p, CancellationToken ct)
    {
        var result = await client.GetDocumentSymbolsAsync(p.FilePath, ct);
        return FormatSymbols(result);
    }
    
    private static async Task<string> GetRenameEditsAsync(LspClient client, LspParameters p, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(p.NewName))
            return "New name required for rename operation";
            
        var result = await client.GetRenameEditsAsync(p.FilePath, p.Line ?? 0, p.Column ?? 0, p.NewName, ct);
        return FormatWorkspaceEdit(result);
    }
    
    private static string FormatLocations(IReadOnlyList<LspLocation>? locations)
    {
        if (locations is null || locations.Count == 0)
            return "No locations found";
        
        var sb = new StringBuilder();
        foreach (var loc in locations)
        {
            sb.AppendLine($"{loc.FilePath}:{loc.Line}:{loc.Column}");
        }
        return sb.ToString();
    }
    
    private static string FormatCompletions(IReadOnlyList<LspCompletionItem>? items)
    {
        if (items is null || items.Count == 0)
            return "No completions available";
        
        var sb = new StringBuilder();
        foreach (var item in items.Take(20))
        {
            sb.AppendLine($"{item.Label} - {item.Kind} {(item.Detail is not null ? $"({item.Detail})" : "")}");
        }
        return sb.ToString();
    }
    
    private static string FormatDiagnostics(IReadOnlyList<LspDiagnostic>? diagnostics)
    {
        if (diagnostics is null || diagnostics.Count == 0)
            return "No diagnostics";
        
        var sb = new StringBuilder();
        foreach (var d in diagnostics)
        {
            var severity = d.Severity switch
            {
                LspDiagnosticSeverity.Error => "ERROR",
                LspDiagnosticSeverity.Warning => "WARN",
                LspDiagnosticSeverity.Information => "INFO",
                _ => "HINT"
            };
            sb.AppendLine($"[{severity}] {d.FilePath}:{d.Line}:{d.Column}: {d.Message}");
        }
        return sb.ToString();
    }
    
    private static string FormatSymbols(IReadOnlyList<LspSymbol>? symbols)
    {
        if (symbols is null || symbols.Count == 0)
            return "No symbols found";
        
        var sb = new StringBuilder();
        void WriteSymbol(LspSymbol symbol, int indent = 0)
        {
            sb.AppendLine($"{new string(' ', indent)}{symbol.Name} ({symbol.Kind}) - Line {symbol.Line}");
            foreach (var child in symbol.Children ?? Array.Empty<LspSymbol>())
            {
                WriteSymbol(child, indent + 2);
            }
        }
        
        foreach (var symbol in symbols)
        {
            WriteSymbol(symbol);
        }
        return sb.ToString();
    }
    
    private static string FormatWorkspaceEdit(LspWorkspaceEdit? edit)
    {
        if (edit is null)
            return "No rename edits";
        
        var sb = new StringBuilder();
        sb.AppendLine("Rename will affect:");
        foreach (var (file, edits) in edit.Changes)
        {
            sb.AppendLine($"  {file}: {edits.Count} changes");
        }
        return sb.ToString();
    }
}

/// <summary>
/// LSP client implementation.
/// </summary>
public sealed class LspClient : IAsyncDisposable
{
    private readonly LspServerConfig _config;
    private readonly string _rootPath;
    private readonly Process _process;
    private int _requestId = 0;
    
    public LspClient(LspServerConfig config, string rootPath)
    {
        _config = config;
        _rootPath = rootPath;
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = config.Command,
                Arguments = config.Args ?? "",
                WorkingDirectory = rootPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
    }
    
    public async Task InitializeAsync(CancellationToken ct)
    {
        _process.Start();
        
        var initParams = new
        {
            processId = Environment.ProcessId,
            rootUri = new Uri(_rootPath).AbsoluteUri,
            capabilities = new
            {
                textDocument = new
                {
                    definition = new { linkSupport = true },
                    references = new { },
                    hover = new { contentFormat = new[] { "markdown", "plaintext" } },
                    completion = new { completionItem = new { snippetSupport = true } },
                    rename = new { prepareSupport = true }
                }
            }
        };
        
        await SendRequestAsync("initialize", initParams, ct);
        await SendNotificationAsync("initialized", new { }, ct);
    }
    
    public async Task<IReadOnlyList<LspLocation>> GoToDefinitionAsync(string filePath, int line, int column, CancellationToken ct)
    {
        var result = await SendRequestAsync("textDocument/definition", new
        {
            textDocument = new { uri = new Uri(filePath).AbsoluteUri },
            position = new { line, character = column }
        }, ct);
        
        return ParseLocations(result);
    }
    
    public async Task<IReadOnlyList<LspLocation>> FindReferencesAsync(string filePath, int line, int column, CancellationToken ct)
    {
        var result = await SendRequestAsync("textDocument/references", new
        {
            textDocument = new { uri = new Uri(filePath).AbsoluteUri },
            position = new { line, character = column },
            context = new { includeDeclaration = true }
        }, ct);
        
        return ParseLocations(result);
    }
    
    public async Task<string?> GetHoverAsync(string filePath, int line, int column, CancellationToken ct)
    {
        var result = await SendRequestAsync("textDocument/hover", new
        {
            textDocument = new { uri = new Uri(filePath).AbsoluteUri },
            position = new { line, character = column }
        }, ct);
        
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("contents", out var contents))
        {
            if (contents.ValueKind == JsonValueKind.String)
                return contents.GetString();
            if (contents.TryGetProperty("value", out var value))
                return value.GetString();
        }
        return null;
    }
    
    public async Task<IReadOnlyList<LspCompletionItem>> GetCompletionsAsync(string filePath, int line, int column, CancellationToken ct)
    {
        var result = await SendRequestAsync("textDocument/completion", new
        {
            textDocument = new { uri = new Uri(filePath).AbsoluteUri },
            position = new { line, character = column }
        }, ct);
        
        var items = new List<LspCompletionItem>();
        if (result.TryGetProperty("items", out var itemsProp))
        {
            foreach (var item in itemsProp.EnumerateArray())
            {
                items.Add(new LspCompletionItem(
                    item.GetProperty("label").GetString() ?? "",
                    item.TryGetProperty("kind", out var kind) ? kind.GetInt32() : 0,
                    item.TryGetProperty("detail", out var detail) ? detail.GetString() : null
                ));
            }
        }
        return items;
    }
    
    public async Task<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(string filePath, CancellationToken ct)
    {
        // Diagnostics usually come via notification, but some servers support pull
        await Task.CompletedTask;
        return Array.Empty<LspDiagnostic>();
    }
    
    public async Task<IReadOnlyList<LspSymbol>> GetDocumentSymbolsAsync(string filePath, CancellationToken ct)
    {
        var result = await SendRequestAsync("textDocument/documentSymbol", new
        {
            textDocument = new { uri = new Uri(filePath).AbsoluteUri }
        }, ct);
        
        return ParseSymbols(result);
    }
    
    public async Task<LspWorkspaceEdit?> GetRenameEditsAsync(string filePath, int line, int column, string newName, CancellationToken ct)
    {
        var result = await SendRequestAsync("textDocument/rename", new
        {
            textDocument = new { uri = new Uri(filePath).AbsoluteUri },
            position = new { line, character = column },
            newName
        }, ct);
        
        if (result.ValueKind == JsonValueKind.Null)
            return null;
            
        var changes = new Dictionary<string, List<LspTextEdit>>();
        if (result.TryGetProperty("changes", out var changesProp))
        {
            foreach (var change in changesProp.EnumerateObject())
            {
                var edits = new List<LspTextEdit>();
                foreach (var edit in change.Value.EnumerateArray())
                {
                    edits.Add(new LspTextEdit(
                        edit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                        edit.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                        edit.GetProperty("newText").GetString() ?? ""
                    ));
                }
                changes[change.Name] = edits;
            }
        }
        
        return new LspWorkspaceEdit(changes);
    }
    
    private async Task<JsonElement> SendRequestAsync(string method, object parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _requestId);
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        
        var header = $"Content-Length: {Encoding.UTF8.GetByteCount(request)}\r\n\r\n";
        await _process.StandardInput.WriteAsync((header + request).AsMemory(), ct);
        await _process.StandardInput.FlushAsync(ct);
        
        // Read response (simplified - real impl needs proper LSP message parsing)
        var response = await ReadMessageAsync(ct);
        return response;
    }
    
    private async Task SendNotificationAsync(string method, object parameters, CancellationToken ct)
    {
        var notification = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters });
        var header = $"Content-Length: {Encoding.UTF8.GetByteCount(notification)}\r\n\r\n";
        await _process.StandardInput.WriteAsync((header + notification).AsMemory(), ct);
        await _process.StandardInput.FlushAsync(ct);
    }
    
    private async Task<JsonElement> ReadMessageAsync(CancellationToken ct)
    {
        // Read headers
        string? line;
        var contentLength = 0;
        
        while ((line = await _process.StandardOutput.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrEmpty(line)) break;
            if (line.StartsWith("Content-Length:"))
            {
                contentLength = int.Parse(line.Substring(15).Trim());
            }
        }
        
        // Read content
        var buffer = new char[contentLength];
        await _process.StandardOutput.ReadAsync(buffer.AsMemory(0, contentLength), ct);
        var content = new string(buffer);
        
        return JsonDocument.Parse(content).RootElement;
    }
    
    private static List<LspLocation> ParseLocations(JsonElement result)
    {
        var locations = new List<LspLocation>();
        
        if (result.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in result.EnumerateArray())
            {
                locations.Add(ParseLocation(item));
            }
        }
        else if (result.ValueKind == JsonValueKind.Object)
        {
            // Could be LocationLink
            if (result.TryGetProperty("targetUri", out var targetUri))
            {
                locations.Add(new LspLocation(
                    targetUri.GetString() ?? "",
                    result.GetProperty("targetRange").GetProperty("start").GetProperty("line").GetInt32(),
                    result.GetProperty("targetRange").GetProperty("start").GetProperty("character").GetInt32()
                ));
            }
            else
            {
                locations.Add(ParseLocation(result));
            }
        }
        
        return locations;
    }
    
    private static LspLocation ParseLocation(JsonElement element)
    {
        var uri = element.GetProperty("uri").GetString() ?? "";
        var range = element.GetProperty("range");
        return new LspLocation(
            uri,
            range.GetProperty("start").GetProperty("line").GetInt32(),
            range.GetProperty("start").GetProperty("character").GetInt32()
        );
    }
    
    private static List<LspSymbol> ParseSymbols(JsonElement result)
    {
        var symbols = new List<LspSymbol>();
        
        if (result.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in result.EnumerateArray())
            {
                symbols.Add(ParseSymbol(item));
            }
        }
        
        return symbols;
    }
    
    private static LspSymbol ParseSymbol(JsonElement element)
    {
        var name = element.GetProperty("name").GetString() ?? "";
        var kind = element.GetProperty("kind").GetInt32();
        var line = element.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32();
        
        var children = new List<LspSymbol>();
        if (element.TryGetProperty("children", out var childrenProp))
        {
            foreach (var child in childrenProp.EnumerateArray())
            {
                children.Add(ParseSymbol(child));
            }
        }
        
        return new LspSymbol(name, kind, line, children);
    }
    
    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.Kill();
        }
        catch { }
        _process.Dispose();
    }
}

// LSP types
public sealed record LspLocation(string FilePath, int Line, int Column);
public sealed record LspCompletionItem(string Label, int Kind, string? Detail);
public sealed record LspDiagnostic(string FilePath, int Line, int Column, string Message, LspDiagnosticSeverity Severity);
public sealed record LspSymbol(string Name, int Kind, int Line, IReadOnlyList<LspSymbol>? Children = null);
public sealed record LspTextEdit(int Line, int Column, string NewText);
public sealed record LspWorkspaceEdit(IReadOnlyDictionary<string, List<LspTextEdit>> Changes);

public enum LspDiagnosticSeverity { Error, Warning, Information, Hint }

public sealed class LspConfig
{
    public Dictionary<string, LspServerConfig> Servers { get; init; } = new();
    
    public LspServerConfig? GetServerConfig(string language)
    {
        return Servers.TryGetValue(language, out var config) ? config : null;
    }
}

public sealed record LspServerConfig(
    string Language,
    string Command,
    string? Args = null);

public sealed class LspParameters
{
    public required string Action { get; init; }
    public required string Language { get; init; }
    public required string RootPath { get; init; }
    public required string FilePath { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public string? NewName { get; init; }
}
