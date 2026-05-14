namespace Hermes.Agent.Shell;

/// <summary>
/// Functional grouping of a <see cref="SlashCommand"/>. Used by the chat palette UI to
/// section the suggestion list (chat → agent → tools → info), in that order.
/// </summary>
public enum SlashCommandCategory
{
    /// <summary>Conversation-level controls: new, clear, retry…</summary>
    Chat,

    /// <summary>Agent control surface: approve, deny, reset, compact, fast…</summary>
    Agent,

    /// <summary>Tool-invocation shortcuts: web, browse, code, shell…</summary>
    Tools,

    /// <summary>Informational queries: help, status, model, version, usage…</summary>
    Info,
}

/// <summary>
/// Where a <see cref="SlashCommand"/> is handled.
/// </summary>
public enum SlashCommandKind
{
    /// <summary>The desktop shell handles the command locally; nothing is sent to the agent.</summary>
    Local,

    /// <summary>The command (including arguments) is forwarded to the agent as a regular message.</summary>
    AgentForwarded,
}

/// <summary>
/// Metadata for a single chat slash command. Pure data — no UI or service dependencies.
/// The desktop shell maps <see cref="Name"/> to its local handler; localizable copy lives
/// in <c>Resources.resw</c> under <c>SlashCommand.&lt;name without slash&gt;.Description</c>.
/// </summary>
public sealed record SlashCommand(
    string Name,
    SlashCommandCategory Category,
    SlashCommandKind Kind)
{
    /// <summary>Resw lookup key for a localized one-line description.</summary>
    public string DescriptionResourceKey => $"SlashCommand.{NameWithoutSlash}.Description";

    /// <summary>The command name without its leading <c>/</c> (for case-insensitive lookups).</summary>
    public string NameWithoutSlash =>
        Name.StartsWith('/') ? Name[1..] : Name;
}
