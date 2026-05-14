namespace Hermes.Agent.UI;

using System.Text;

/// <summary>
/// Batches streaming text deltas before they are committed to a UI-bound string.
/// Adapted from DeepSeek-TUI's streaming chunk/commit-tick model for WinUI.
/// </summary>
public sealed class StreamingTextAccumulator
{
    private readonly StringBuilder _pending = new();
    private readonly TimeProvider _clock;
    private DateTimeOffset _lastFlush;

    public StreamingTextAccumulator(
        TimeProvider? clock = null,
        TimeSpan? minFlushInterval = null,
        int maxBufferedChars = 256)
    {
        _clock = clock ?? TimeProvider.System;
        MinFlushInterval = minFlushInterval ?? TimeSpan.FromMilliseconds(33);
        MaxBufferedChars = Math.Max(1, maxBufferedChars);
        _lastFlush = _clock.GetUtcNow();
    }

    public TimeSpan MinFlushInterval { get; }
    public int MaxBufferedChars { get; }
    public bool HasPending => _pending.Length > 0;

    public void Append(string? delta)
    {
        if (!string.IsNullOrEmpty(delta))
            _pending.Append(delta);
    }

    public string FlushIfDue(bool force = false)
    {
        if (_pending.Length == 0)
            return string.Empty;

        var now = _clock.GetUtcNow();
        if (!force &&
            _pending.Length < MaxBufferedChars &&
            now - _lastFlush < MinFlushInterval)
        {
            return string.Empty;
        }

        var flushLength = SafeFlushLength(force);
        if (flushLength == 0)
            return string.Empty;

        var text = _pending.ToString(0, flushLength);
        _pending.Remove(0, flushLength);
        _lastFlush = now;
        return text;
    }

    public string Flush() => FlushIfDue(force: true);

    public void Clear()
    {
        _pending.Clear();
        _lastFlush = _clock.GetUtcNow();
    }

    private int SafeFlushLength(bool force)
    {
        var length = _pending.Length;

        // Do not split a UTF-16 surrogate pair during ordinary timed flushes.
        // The final forced flush is allowed to release whatever the provider sent.
        if (!force && length > 0 && char.IsHighSurrogate(_pending[length - 1]))
            length--;

        return length;
    }
}
