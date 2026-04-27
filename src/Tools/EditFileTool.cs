namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Text.RegularExpressions;

/// <summary>
/// File editing tool with precise string replacement.
/// Minimizes risk by only changing specified text.
/// </summary>
public sealed class EditFileTool : ITool
{
    public string Name => "edit_file";
    public string Description => "Perform precise string replacement within files with uniqueness validation";
    public Type ParametersType => typeof(EditFileParameters);
    
    public Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (EditFileParameters)parameters;
        return EditFileAsync(p.FilePath, p.OldString, p.NewString, p.ReplaceAll, ct);
    }
    
    private async Task<ToolResult> EditFileAsync(string filePath, string oldString, string newString, bool replaceAll, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return ToolResult.Fail($"File not found: {filePath}");
            }

            // Check for stale file content before editing
            var staleWarning = FileReadTracker.CheckStaleness(filePath);

            var content = await File.ReadAllTextAsync(filePath, ct);
            
            // Find occurrences
            var occurrences = Regex.Matches(content, Regex.Escape(oldString)).Count;
            
            if (occurrences == 0)
            {
                return ToolResult.Fail($"Text not found in file. The old_string must match exactly.");
            }
            
            if (occurrences > 1 && !replaceAll)
            {
                return ToolResult.Fail(
                    $"Found {occurrences} occurrences of the text. " +
                    $"Use replace_all=true to replace all, or make old_string more specific.");
            }
            
            // Perform replacement
            var newContent = replaceAll 
                ? content.Replace(oldString, newString)
                : Regex.Replace(content, Regex.Escape(oldString), newString, RegexOptions.None, TimeSpan.FromSeconds(5));
            
            // Write back
            await File.WriteAllTextAsync(filePath, newContent, ct);

            // Update tracker so consecutive edits don't trigger false warnings
            FileReadTracker.UpdateAfterWrite(filePath);

            // Generate inline unified diff
            var inlineDiff = DiffHelper.UnifiedDiff(content, newContent, Path.GetFileName(filePath));

            // Generate summary diff
            var diff = GenerateDiff(filePath, oldString, newString, occurrences);

            // Prepend stale warning if detected
            if (staleWarning is not null)
                diff = staleWarning + "\n\n" + diff;

            // Append inline diff
            diff += "\n\n" + inlineDiff;

            return ToolResult.Ok(diff);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to edit file: {ex.Message}", ex);
        }
    }
    
    private string GenerateDiff(string filePath, string oldStr, string newStr, int count)
    {
        return $"""
            Edit applied to {filePath}
            Replaced {count} occurrence(s)
            
            --- Old
            {oldStr}
            
            +++ New
            {newStr}
            """;
    }
}

public sealed class EditFileParameters
{
    public required string FilePath { get; init; }
    public required string OldString { get; init; }
    public required string NewString { get; init; }
    public bool ReplaceAll { get; init; }
}
