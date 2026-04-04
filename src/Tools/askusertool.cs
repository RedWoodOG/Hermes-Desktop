namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using System.Text.Json;

/// <summary>
/// Tool for asking the user questions with predefined options.
/// </summary>
public sealed class AskUserTool : ITool
{
    private readonly IUserInteraction _interaction;
    
    public string Name => "ask_user";
    public string Description => "Ask the user a question with predefined options";
    public Type ParametersType => typeof(AskUserParameters);
    
    public AskUserTool(IUserInteraction interaction)
    {
        _interaction = interaction;
    }
    
    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (AskUserParameters)parameters;
        
        try
        {
            var response = await _interaction.AskQuestionAsync(
                p.Question,
                p.Options.Select(o => new QuestionOption(o.Label, o.Description)).ToList(),
                p.AllowMultiple,
                ct);
            
            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                response = response.SelectedOption,
                customResponse = response.CustomResponse,
                selectedOptions = response.SelectedOptions
            }));
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("User cancelled the question");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to ask user: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// User interaction interface.
/// </summary>
public interface IUserInteraction
{
    Task<UserResponse> AskQuestionAsync(
        string question,
        IReadOnlyList<QuestionOption> options,
        bool allowMultiple,
        CancellationToken ct);
}

/// <summary>
/// Option for a question.
/// </summary>
public sealed record QuestionOption(string Label, string? Description = null);

/// <summary>
/// User response to a question.
/// </summary>
public sealed class UserResponse
{
    public string? SelectedOption { get; init; }
    public string? CustomResponse { get; init; }
    public IReadOnlyList<string>? SelectedOptions { get; init; }
}

public sealed class AskUserParameters
{
    public required string Question { get; init; }
    public IReadOnlyList<AskUserOption> Options { get; init; } = Array.Empty<AskUserOption>();
    public bool AllowMultiple { get; init; }
}

public sealed class AskUserOption
{
    public required string Label { get; init; }
    public string? Description { get; init; }
}
