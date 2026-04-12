namespace Hermes.Agent.Tools;

using System.Collections.Concurrent;

/// <summary>
/// Tracks file read timestamps to detect stale file content.
/// Matches the official Hermes Agent file_tools.py _read_tracker logic.
/// Thread-safe static tracker shared across all file tools.
/// </summary>
public static class FileReadTracker
{
    private static readonly ConcurrentDictionary<string, DateTime> _readTimestamps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Record the file's current modification time after a successful read.
    /// </summary>
    public static void RecordRead(string filePath)
    {
        try
        {
            var resolved = Path.GetFullPath(filePath);
            if (File.Exists(resolved))
            {
                _readTimestamps[resolved] = File.GetLastWriteTimeUtc(resolved);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FileReadTracker.RecordRead failed for {filePath}: {ex}");
        }
    }

    /// <summary>
    /// Check whether a file was modified since we last read it.
    /// Returns a warning string if the file is stale, or null if fresh / never read.
    /// </summary>
    public static string? CheckStaleness(string filePath)
    {
        try
        {
            var resolved = Path.GetFullPath(filePath);
            if (!_readTimestamps.TryGetValue(resolved, out var lastReadTime))
                return null; // Never read - nothing to compare

            if (!File.Exists(resolved))
                return null; // File deleted - let the write handle it

            var currentMtime = File.GetLastWriteTimeUtc(resolved);
            if (currentMtime != lastReadTime)
            {
                return $"Warning: {filePath} was modified since you last read it " +
                       "(external edit or concurrent agent). The content you read may be " +
                       "stale. Consider re-reading the file to verify before writing.";
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FileReadTracker.CheckStaleness failed for {filePath}: {ex}");
        }

        return null;
    }

    /// <summary>
    /// Update the stored timestamp after a successful write.
    /// Prevents false staleness warnings from our own writes.
    /// </summary>
    public static void UpdateAfterWrite(string filePath)
    {
        try
        {
            var resolved = Path.GetFullPath(filePath);
            if (File.Exists(resolved))
            {
                _readTimestamps[resolved] = File.GetLastWriteTimeUtc(resolved);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FileReadTracker.UpdateAfterWrite failed for {filePath}: {ex}");
        }
    }

    /// <summary>Clear all tracked timestamps (useful for testing).</summary>
    public static void Clear() => _readTimestamps.Clear();
}
