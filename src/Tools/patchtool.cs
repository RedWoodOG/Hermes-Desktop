namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;

/// <summary>
/// Apply unified diff patches to files.
/// Parses unified diff format and applies line-based add/remove hunks.
/// </summary>
public sealed class PatchTool : ITool
{
    public string Name => "patch";
    public string Description => "Apply a unified diff patch to a file. Parses hunks and applies line-based additions and removals.";
    public Type ParametersType => typeof(PatchParameters);

    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (PatchParameters)parameters;

        if (string.IsNullOrWhiteSpace(p.FilePath))
            return ToolResult.Fail("file_path is required.");

        if (string.IsNullOrWhiteSpace(p.Patch))
            return ToolResult.Fail("patch is required.");

        if (!File.Exists(p.FilePath))
            return ToolResult.Fail($"File not found: {p.FilePath}");

        try
        {
            var originalLines = (await File.ReadAllLinesAsync(p.FilePath, ct)).ToList();
            var hunks = ParseHunks(p.Patch);

            if (hunks.Count == 0)
                return ToolResult.Fail("No valid hunks found in patch.");

            // Apply hunks in reverse order so line numbers remain valid
            var sortedHunks = hunks.OrderByDescending(h => h.OriginalStart).ToList();
            var appliedCount = 0;

            foreach (var hunk in sortedHunks)
            {
                var startIndex = hunk.OriginalStart - 1; // Convert 1-based to 0-based

                if (startIndex < 0 || startIndex > originalLines.Count)
                {
                    return ToolResult.Fail(
                        $"Hunk at line {hunk.OriginalStart} is out of range (file has {originalLines.Count} lines).");
                }

                // Verify context lines match
                var contextOk = VerifyContext(originalLines, startIndex, hunk.Lines);
                if (!contextOk)
                {
                    return ToolResult.Fail(
                        $"Context mismatch at line {hunk.OriginalStart}. The file may have changed.");
                }

                // Apply the hunk
                ApplyHunk(originalLines, startIndex, hunk.Lines);
                appliedCount++;
            }

            await File.WriteAllLinesAsync(p.FilePath, originalLines, ct);
            return ToolResult.Ok($"Patch applied successfully: {appliedCount} hunk(s) applied to {p.FilePath}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to apply patch: {ex.Message}", ex);
        }
    }

    private static List<Hunk> ParseHunks(string patch)
    {
        var hunks = new List<Hunk>();
        var lines = patch.Split('\n');
        Hunk? currentHunk = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("@@"))
            {
                // Parse hunk header: @@ -start,count +start,count @@
                currentHunk = ParseHunkHeader(line);
                if (currentHunk != null)
                    hunks.Add(currentHunk);
            }
            else if (currentHunk != null)
            {
                if (line.StartsWith('+') || line.StartsWith('-') || line.StartsWith(' '))
                {
                    currentHunk.Lines.Add(line);
                }
                // Skip lines that don't match diff format (like "\ No newline at end of file")
            }
        }

        return hunks;
    }

    private static Hunk? ParseHunkHeader(string header)
    {
        // Format: @@ -originalStart,originalCount +newStart,newCount @@
        var match = System.Text.RegularExpressions.Regex.Match(
            header, @"@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@");

        if (!match.Success) return null;

        return new Hunk
        {
            OriginalStart = int.Parse(match.Groups[1].Value),
            OriginalCount = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1,
            NewStart = int.Parse(match.Groups[3].Value),
            NewCount = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1,
        };
    }

    private static bool VerifyContext(List<string> fileLines, int startIndex, List<string> hunkLines)
    {
        var fileIdx = startIndex;

        foreach (var line in hunkLines)
        {
            if (line.StartsWith(' ') || line.StartsWith('-'))
            {
                // Context or removal line — must match file content
                if (fileIdx >= fileLines.Count)
                    return false;

                var expected = line[1..]; // Strip the prefix
                if (fileLines[fileIdx] != expected)
                    return false;

                fileIdx++;
            }
            // Addition lines don't consume file lines during verification
        }

        return true;
    }

    private static void ApplyHunk(List<string> fileLines, int startIndex, List<string> hunkLines)
    {
        var fileIdx = startIndex;

        foreach (var line in hunkLines)
        {
            if (line.StartsWith(' '))
            {
                // Context line — skip
                fileIdx++;
            }
            else if (line.StartsWith('-'))
            {
                // Remove line
                fileLines.RemoveAt(fileIdx);
            }
            else if (line.StartsWith('+'))
            {
                // Add line
                fileLines.Insert(fileIdx, line[1..]);
                fileIdx++;
            }
        }
    }

    private sealed class Hunk
    {
        public int OriginalStart { get; init; }
        public int OriginalCount { get; init; }
        public int NewStart { get; init; }
        public int NewCount { get; init; }
        public List<string> Lines { get; } = new();
    }
}

public sealed class PatchParameters
{
    public required string FilePath { get; init; }
    public required string Patch { get; init; }
}
