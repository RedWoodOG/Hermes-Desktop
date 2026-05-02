namespace HermesDesktop.Tools;

using System.ComponentModel;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Patterns;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Hermes.Agent.Core;
using Hermes.Agent.Tools;

internal sealed class WindowsAutomationTool : ITool, IDisposable
{
    private const int DefaultMaxDepth = 4;
    private const int DefaultMaxNodes = 120;
    private readonly Dictionary<string, AutomationElement> _elementRefs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private UIA3Automation? _automation;
    private int _nextRef;
    private bool _disposed;

    public string Name => "windows_automation";

    public string Description =>
        "Drive native Windows apps through UI Automation. Actions: snapshot, click, double_click, focus, invoke, type, press.";

    public Type ParametersType => typeof(WindowsAutomationParameters);

    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (WindowsAutomationParameters)parameters;
        await _lock.WaitAsync(ct);
        try
        {
            return (p.Action ?? "snapshot").ToLowerInvariant() switch
            {
                "snapshot" => Snapshot(p),
                "click" => Click(p, doubleClick: false),
                "double_click" => Click(p, doubleClick: true),
                "focus" => Focus(p),
                "invoke" => Invoke(p),
                "type" or "fill" => TypeText(p),
                "press" => PressKey(p),
                _ => ToolResult.Fail(
                    "Unknown action. Use: snapshot, click, double_click, focus, invoke, type, fill, press.")
            };
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("Windows automation action was cancelled.");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Windows automation failed: {ex.Message}", ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _automation?.Dispose();
        _lock.Dispose();
        _disposed = true;
    }

    private ToolResult Snapshot(WindowsAutomationParameters p)
    {
        var roots = ResolveSnapshotRoots(p, out var rejection);
        if (rejection is not null)
        {
            return ToolResult.Fail(rejection);
        }

        _elementRefs.Clear();
        _nextRef = 0;

        var maxDepth = Math.Clamp(p.MaxDepth ?? DefaultMaxDepth, 0, 8);
        var maxNodes = Math.Clamp(p.MaxNodes ?? DefaultMaxNodes, 1, 500);
        var nodeCount = 0;

        var nodes = roots
            .Select(root => BuildNode(root, depth: 0, maxDepth, maxNodes, ref nodeCount))
            .Where(node => node is not null)
            .Cast<WindowsAutomationNode>()
            .ToArray();

        if (nodes.Length == 0)
        {
            return ToolResult.Fail("No UI Automation elements matched the snapshot request.");
        }

        var header = "Windows UI Automation snapshot. Use ref values with click, invoke, focus, type, or press.";
        var content = WindowsAutomationTreeFormatter.Format(nodes);
        return ToolResult.Ok($"{header}\n\n{content}");
    }

    private ToolResult Click(WindowsAutomationParameters p, bool doubleClick)
    {
        var element = RequireElement(p.Ref);
        if (doubleClick)
        {
            element.DoubleClick(moveMouse: false);
            return ToolResult.Ok($"Double-clicked {p.Ref}.");
        }

        element.Click(moveMouse: false);
        return ToolResult.Ok($"Clicked {p.Ref}.");
    }

    private ToolResult Focus(WindowsAutomationParameters p)
    {
        var element = RequireElement(p.Ref);
        element.Focus();
        return ToolResult.Ok($"Focused {p.Ref}.");
    }

    private ToolResult Invoke(WindowsAutomationParameters p)
    {
        var element = RequireElement(p.Ref);
        var invoke = element.Patterns.Invoke.PatternOrDefault;
        if (invoke is not null)
        {
            invoke.Invoke();
            return ToolResult.Ok($"Invoked {p.Ref}.");
        }

        return ToolResult.Fail($"Invoke pattern is unavailable for {p.Ref}. Use click explicitly if pointer activation is intended.");
    }

    private ToolResult TypeText(WindowsAutomationParameters p)
    {
        var element = RequireElement(p.Ref);
        var text = p.Text ?? "";
        element.Focus();

        IValuePattern? valuePattern = element.Patterns.Value.PatternOrDefault;
        if (valuePattern is not null && !valuePattern.IsReadOnly)
        {
            valuePattern.SetValue(text);
            return ToolResult.Ok($"Set text on {p.Ref}.");
        }

        return ToolResult.Fail($"Value pattern is unavailable for {p.Ref}; refusing to send raw keyboard text.");
    }

    private ToolResult PressKey(WindowsAutomationParameters p)
    {
        if (string.IsNullOrWhiteSpace(p.Key))
        {
            return ToolResult.Fail("Key is required for press.");
        }

        if (p.Ref is not null)
        {
            RequireElement(p.Ref).Focus();
        }

        var keyName = NormalizeKeyName(p.Key);
        if (!Enum.TryParse<VirtualKeyShort>(keyName, ignoreCase: true, out var key))
        {
            return ToolResult.Fail($"Unsupported key '{p.Key}'. Use names such as Enter, Tab, Escape, Up, Down, Left, Right.");
        }

        Keyboard.Press(key);
        return ToolResult.Ok($"Pressed {p.Key}.");
    }

    private IReadOnlyList<AutomationElement> ResolveSnapshotRoots(WindowsAutomationParameters p, out string? rejection)
    {
        rejection = null;
        var automation = EnsureAutomation();

        if (string.Equals(p.Target, "focused", StringComparison.OrdinalIgnoreCase))
        {
            var focused = automation.FocusedElement();
            return focused is null ? [] : [focused];
        }

        if (!string.IsNullOrWhiteSpace(p.Ref) && _elementRefs.TryGetValue(p.Ref, out var cached))
        {
            return [cached];
        }

        if (!string.IsNullOrWhiteSpace(p.Ref))
        {
            rejection = $"Unknown ref '{p.Ref}'. Run snapshot again to refresh UIA refs.";
            return [];
        }

        var desktop = automation.GetDesktop();
        var windows = desktop.FindAllChildren();
        if (string.IsNullOrWhiteSpace(p.WindowTitle))
        {
            return windows;
        }

        return windows
            .Where(window => (window.Name ?? "").Contains(p.WindowTitle, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private WindowsAutomationNode? BuildNode(
        AutomationElement element,
        int depth,
        int maxDepth,
        int maxNodes,
        ref int nodeCount)
    {
        if (nodeCount >= maxNodes)
        {
            return null;
        }

        var currentRef = NextRef(element);
        nodeCount++;

        var children = new List<WindowsAutomationNode>();
        if (depth < maxDepth)
        {
            foreach (var child in SafeFindChildren(element))
            {
                var childNode = BuildNode(child, depth + 1, maxDepth, maxNodes, ref nodeCount);
                if (childNode is not null)
                {
                    children.Add(childNode);
                }
            }
        }

        return new WindowsAutomationNode(
            currentRef,
            SafeRead(() => element.ControlType.ToString()),
            SafeRead(() => element.Name),
            SafeRead(() => element.AutomationId),
            SafeRead(() => element.ClassName),
            depth,
            children);
    }

    private AutomationElement[] SafeFindChildren(AutomationElement element)
    {
        try
        {
            return element.FindAllChildren();
        }
        catch
        {
            return [];
        }
    }

    private AutomationElement RequireElement(string? elementRef)
    {
        if (string.IsNullOrWhiteSpace(elementRef))
        {
            throw new InvalidOperationException("Ref is required. Run snapshot first and use one of its ref values.");
        }

        if (!_elementRefs.TryGetValue(elementRef, out var element))
        {
            throw new InvalidOperationException($"Unknown ref '{elementRef}'. Run snapshot again to refresh UIA refs.");
        }

        return element;
    }

    private string NextRef(AutomationElement element)
    {
        var elementRef = $"uia_{++_nextRef}";
        _elementRefs[elementRef] = element;
        return elementRef;
    }

    private UIA3Automation EnsureAutomation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _automation ??= new UIA3Automation();
        return _automation;
    }

    private static string SafeRead(Func<string?> read)
    {
        try
        {
            return read() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string NormalizeKeyName(string key)
    {
        return key.Trim().ToUpperInvariant() switch
        {
            "ENTER" => "RETURN",
            "ESC" => "ESCAPE",
            "CTRL" => "CONTROL",
            "PGUP" => "PRIOR",
            "PAGEUP" => "PRIOR",
            "PGDN" => "NEXT",
            "PAGEDOWN" => "NEXT",
            _ => key.Trim().ToUpperInvariant()
        };
    }
}

public sealed class WindowsAutomationParameters
{
    [Description("Action to perform: snapshot, click, double_click, focus, invoke, type, fill, or press.")]
    public string? Action { get; init; }

    [Description("Element ref from the latest snapshot, such as uia_12.")]
    public string? Ref { get; init; }

    [Description("Text to type or set for type/fill actions.")]
    public string? Text { get; init; }

    [Description("Keyboard key to press, such as Enter, Tab, Escape, Up, Down, Left, or Right.")]
    public string? Key { get; init; }

    [Description("Optional top-level window title substring to scope snapshot results.")]
    public string? WindowTitle { get; init; }

    [Description("Snapshot target. Use focused to snapshot the focused element; default snapshots top-level desktop windows.")]
    public string? Target { get; init; }

    [Description("Maximum snapshot depth. Defaults to 4; capped at 8.")]
    public int? MaxDepth { get; init; }

    [Description("Maximum number of elements to include. Defaults to 120; capped at 500.")]
    public int? MaxNodes { get; init; }
}
