namespace Hermes.Agent.Tools;

using Hermes.Agent.Core;
using Hermes.Agent.Skills;

/// <summary>
/// Invoke a skill by name from within a conversation.
/// Returns the skill system prompt for the agent to act on.
/// </summary>
public sealed class SkillInvokeTool : ITool
{
    private readonly SkillManager _skillManager;

    public string Name => "skill_invoke";
    public string Description => "Invoke a skill by name. Returns the skill system prompt for the agent to act on.";
    public Type ParametersType => typeof(SkillInvokeParameters);

    public SkillInvokeTool(SkillManager skillManager)
    {
        _skillManager = skillManager;
    }

    public async Task<ToolResult> ExecuteAsync(object parameters, CancellationToken ct)
    {
        var p = (SkillInvokeParameters)parameters;

        if (string.IsNullOrWhiteSpace(p.SkillName))
            return ToolResult.Fail("skill_name is required.");

        try
        {
            var context = await _skillManager.InvokeSkillAsync(
                p.SkillName,
                p.Query ?? string.Empty,
                ct);

            return ToolResult.Ok(context);
        }
        catch (SkillNotFoundException)
        {
            return ToolResult.Fail($"Skill not found: {p.SkillName}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to invoke skill: {ex.Message}", ex);
        }
    }
}

public sealed class SkillInvokeParameters
{
    public required string SkillName { get; init; }
    public string? Query { get; init; }
}
