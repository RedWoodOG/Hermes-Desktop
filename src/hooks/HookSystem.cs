namespace Hermes.Agent.Hooks;

using Microsoft.Extensions.Logging;
using System.Text.Json;

/// <summary>
/// Hook event types.
/// </summary>
public enum HookEventType
{
    PreToolUse,
    PostToolUse,
    Stop,
    SubagentStop,
    UserPromptSubmit,
    PermissionRequest,
    Notification,
    SessionStart,
    SessionEnd
}

/// <summary>
/// Base class for hook events.
/// </summary>
public abstract record HookEvent(HookEventType Type, DateTimeOffset Timestamp)
{
    protected HookEvent(HookEventType type) : this(type, DateTimeOffset.UtcNow) { }
}

/// <summary>
/// Hook event for tool use.
/// </summary>
public sealed record ToolUseHookEvent(
    string ToolName,
    object? Input,
    string? ToolCallId,
    HookEventType Type) : HookEvent(Type)
{
    public ToolUseHookEvent(string toolName, object? input, string? toolCallId)
        : this(toolName, input, toolCallId, HookEventType.PreToolUse) { }
}

/// <summary>
/// Hook event for post tool use with result.
/// </summary>
public sealed record PostToolUseHookEvent(
    string ToolName,
    object? Input,
    string? ToolCallId,
    bool Success,
    string? Output,
    string? Error) : HookEvent(HookEventType.PostToolUse);

/// <summary>
/// Hook event for stop conditions.
/// </summary>
public sealed record StopHookEvent(
    string? Reason,
    IReadOnlyList<string>? ToolResults) : HookEvent(HookEventType.Stop);

/// <summary>
/// Hook event for user prompt submission.
/// </summary>
public sealed record UserPromptHookEvent(
    string Prompt,
    IReadOnlyDictionary<string, string>? Metadata) : HookEvent(HookEventType.UserPromptSubmit);

/// <summary>
/// Hook event for permission requests.
/// </summary>
public sealed record PermissionHookEvent(
    string ToolName,
    object? Input,
    string? Reason) : HookEvent(HookEventType.PermissionRequest);

/// <summary>
/// Hook definition.
/// </summary>
public abstract record HookDefinition(
    string Name,
    IReadOnlySet<HookEventType> Events,
    bool Enabled = true)
{
    public abstract Task<HookResult> ExecuteAsync(HookEvent evt, CancellationToken ct);
}

/// <summary>
/// Hook that executes a shell command.
/// </summary>
public sealed record CommandHook(
    string Name,
    string Command,
    IReadOnlySet<HookEventType> Events,
    bool Enabled = true) : HookDefinition(Name, Events, Enabled)
{
    public override async Task<HookResult> ExecuteAsync(HookEvent evt, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {Command}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            // Set environment variables from event
            psi.Environment["HOOK_EVENT_TYPE"] = evt.Type.ToString();
            psi.Environment["HOOK_TIMESTAMP"] = evt.Timestamp.ToString("O");
            
            if (evt is ToolUseHookEvent toolEvt)
            {
                psi.Environment["HOOK_TOOL_NAME"] = toolEvt.ToolName;
                if (toolEvt.Input is not null)
                    psi.Environment["HOOK_TOOL_INPUT"] = JsonSerializer.Serialize(toolEvt.Input);
            }
            
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return HookResult.Fail("Failed to start process");
            
            await process.WaitForExitAsync(ct);
            
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            
            if (process.ExitCode == 0)
                return HookResult.Ok(output);
            else
                return HookResult.Fail($"Command failed: {error}");
        }
        catch (Exception ex)
        {
            return HookResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Hook that sends an HTTP POST request.
/// </summary>
public sealed record HttpHook(
    string Name,
    Uri Url,
    IReadOnlySet<HookEventType> Events,
    IReadOnlyDictionary<string, string>? Headers = null,
    bool Enabled = true) : HookDefinition(Name, Events, Enabled)
{
    private static readonly HttpClient HttpClient = new();
    
    public override async Task<HookResult> ExecuteAsync(HookEvent evt, CancellationToken ct)
    {
        try
        {
            // SSRF check
            var ssrfGuard = new SsrfGuard();
            var validation = await ssrfGuard.ValidateAsync(Url.ToString(), ct);
            if (!validation.IsValid)
                return HookResult.Fail($"SSRF protection: {validation.Reason}");
            
            var payload = new
            {
                eventType = evt.Type.ToString(),
                timestamp = evt.Timestamp,
                data = evt
            };
            
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            
            var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = content };
            
            if (Headers is not null)
            {
                foreach (var (key, value) in Headers)
                {
                    request.Headers.Add(key, value);
                }
            }
            
            var response = await HttpClient.SendAsync(request, ct);
            
            if (response.IsSuccessStatusCode)
                return HookResult.Ok(await response.Content.ReadAsStringAsync(ct));
            else
                return HookResult.Fail($"HTTP {response.StatusCode}");
        }
        catch (Exception ex)
        {
            return HookResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Hook that executes an in-process delegate.
/// </summary>
public sealed record InProcessHook(
    string Name,
    Func<HookEvent, CancellationToken, Task<HookResult>> Handler,
    IReadOnlySet<HookEventType> Events,
    bool Enabled = true) : HookDefinition(Name, Events, Enabled)
{
    public override Task<HookResult> ExecuteAsync(HookEvent evt, CancellationToken ct)
        => Handler(evt, ct);
}

/// <summary>
/// Result of hook execution.
/// </summary>
public sealed record HookResult(bool Success, string? Output = null, string? Error = null)
{
    public static HookResult Ok(string? output = null) => new(true, output);
    public static HookResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// Hook executor that runs hooks for events.
/// </summary>
public sealed class HookExecutor
{
    private readonly List<HookDefinition> _hooks = new();
    private readonly ILogger<HookExecutor> _logger;
    private readonly SsrfGuard _ssrfGuard = new();
    
    public HookExecutor(ILogger<HookExecutor>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HookExecutor>.Instance;
    }
    
    public void RegisterHook(HookDefinition hook)
    {
        _hooks.Add(hook);
        _logger.LogDebug("Registered hook: {Name} for events: {Events}", hook.Name, hook.Events);
    }
    
    public void RemoveHook(string name)
    {
        _hooks.RemoveAll(h => h.Name == name);
    }
    
    public async Task<IReadOnlyList<HookExecutionResult>> ExecuteHooksAsync(
        HookEventType eventType,
        HookEvent evt,
        CancellationToken ct = default)
    {
        var results = new List<HookExecutionResult>();
        
        var matchingHooks = _hooks
            .Where(h => h.Enabled && h.Events.Contains(eventType))
            .ToList();
        
        if (matchingHooks.Count == 0)
            return results;
        
        _logger.LogDebug("Executing {Count} hooks for event {EventType}", matchingHooks.Count, eventType);
        
        foreach (var hook in matchingHooks)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await hook.ExecuteAsync(evt, ct);
                sw.Stop();
                
                results.Add(new HookExecutionResult(
                    hook.Name,
                    result.Success,
                    result.Output,
                    result.Error,
                    sw.ElapsedMilliseconds
                ));
                
                _logger.LogDebug("Hook {Name} completed in {Ms}ms: {Success}",
                    hook.Name, sw.ElapsedMilliseconds, result.Success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Hook {Name} threw exception", hook.Name);
                results.Add(new HookExecutionResult(hook.Name, false, null, ex.Message, 0));
            }
        }
        
        return results;
    }
    
    /// <summary>
    /// Check if any hook wants to block the operation.
    /// </summary>
    public async Task<bool> ShouldBlockAsync(HookEventType eventType, HookEvent evt, CancellationToken ct = default)
    {
        var results = await ExecuteHooksAsync(eventType, evt, ct);
        return results.Any(r => !r.Success);
    }
}

/// <summary>
/// Result of a single hook execution.
/// </summary>
public sealed record HookExecutionResult(
    string HookName,
    bool Success,
    string? Output,
    string? Error,
    long DurationMs);

/// <summary>
/// SSRF guard for HTTP hooks (shared with WebFetchTool).
/// </summary>
public sealed class SsrfGuard
{
    private static readonly string[] PrivateIpRanges =
    {
        "10.", "172.16.", "172.17.", "172.18.", "172.19.", "172.20.", "172.21.",
        "172.22.", "172.23.", "172.24.", "172.25.", "172.26.", "172.27.", "172.28.",
        "172.29.", "172.30.", "172.31.", "192.168.", "127.", "169.254.",
        "::1", "fc00:", "fd00:", "fe80:"
    };
    
    private static readonly string[] BlockedHosts =
    {
        "localhost", "localhost.localdomain", "ip6-localhost", "ip6-loopback",
        "metadata.google.internal", "169.254.169.254"
    };
    
    public async Task<SsrfValidationResult> ValidateAsync(string url, CancellationToken ct)
    {
        Uri uri;
        try
        {
            uri = new Uri(url);
        }
        catch (UriFormatException)
        {
            return SsrfValidationResult.Invalid("Invalid URL format");
        }
        
        if (uri.Scheme != "http" && uri.Scheme != "https")
            return SsrfValidationResult.Invalid("Only HTTP/HTTPS allowed");
        
        var host = uri.Host.ToLowerInvariant();
        
        if (BlockedHosts.Contains(host))
            return SsrfValidationResult.Invalid("Blocked host");
        
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct);
            foreach (var addr in addresses)
            {
                var ipStr = addr.ToString();
                foreach (var range in PrivateIpRanges)
                {
                    if (ipStr.StartsWith(range))
                        return SsrfValidationResult.Invalid($"Private IP blocked: {ipStr}");
                }
            }
        }
        catch (Exception ex)
        {
            return SsrfValidationResult.Invalid($"DNS failed: {ex.Message}");
        }
        
        return SsrfValidationResult.Valid();
    }
}

public sealed record SsrfValidationResult(bool IsValid, string? Reason = null)
{
    public static SsrfValidationResult Valid() => new(true);
    public static SsrfValidationResult Invalid(string reason) => new(false, reason);
}
