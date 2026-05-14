namespace Hermes.Agent.Shell;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Static catalog of chat slash commands plus filter / parse helpers.
/// Modeled after the Electron <c>SLASH_COMMANDS</c> array in
/// <c>_inbox/hermes-desktop-main/src/renderer/src/screens/Chat/Chat.tsx</c>, scoped to
/// the subset that is actually wired to handlers in the WinUI shell today.
/// </summary>
public static class SlashCommandRegistry
{
    /// <summary>
    /// Catalog ordered the way commands appear in the palette: chat → agent → tools → info.
    /// </summary>
    public static IReadOnlyList<SlashCommand> All { get; } = new SlashCommand[]
    {
        // Chat (local)
        new("/new",      SlashCommandCategory.Chat, SlashCommandKind.Local),
        new("/clear",    SlashCommandCategory.Chat, SlashCommandKind.Local),
        new("/retry",    SlashCommandCategory.Chat, SlashCommandKind.Local),

        // Agent control (forwarded to agent runtime)
        new("/btw",      SlashCommandCategory.Agent, SlashCommandKind.AgentForwarded),
        new("/approve",  SlashCommandCategory.Agent, SlashCommandKind.AgentForwarded),
        new("/deny",     SlashCommandCategory.Agent, SlashCommandKind.AgentForwarded),
        new("/reset",    SlashCommandCategory.Agent, SlashCommandKind.AgentForwarded),
        new("/compact",  SlashCommandCategory.Agent, SlashCommandKind.AgentForwarded),

        // Info (local — answered from process state, no LLM round-trip)
        new("/help",     SlashCommandCategory.Info, SlashCommandKind.Local),
        new("/tools",    SlashCommandCategory.Info, SlashCommandKind.Local),
        new("/skills",   SlashCommandCategory.Info, SlashCommandKind.Local),
        new("/model",    SlashCommandCategory.Info, SlashCommandKind.Local),
        new("/usage",    SlashCommandCategory.Info, SlashCommandKind.Local),
        new("/version",  SlashCommandCategory.Info, SlashCommandKind.Local),
        new("/status",   SlashCommandCategory.Info, SlashCommandKind.Local),
        new("/debug",    SlashCommandCategory.Info, SlashCommandKind.Local),
    };

    /// <summary>
    /// Return all commands whose name starts with <paramref name="prefix"/> (case-insensitive).
    /// <paramref name="prefix"/> may include or omit the leading <c>/</c>. An empty prefix
    /// returns the full catalog.
    /// </summary>
    public static IReadOnlyList<SlashCommand> FindByPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return All;

        var normalized = prefix.StartsWith('/') ? prefix : "/" + prefix;
        return All
            .Where(c => c.Name.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>
    /// Look up a single command by exact name (with or without the leading <c>/</c>).
    /// </summary>
    public static SlashCommand? Find(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        var normalized = name.StartsWith('/') ? name : "/" + name;
        return All.FirstOrDefault(c =>
            string.Equals(c.Name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parse a chat input line as a slash command invocation.
    /// Returns <c>null</c> when the input does not start with <c>/</c> or has no characters
    /// after the slash. On success the returned tuple has the matched <see cref="SlashCommand"/>
    /// (or <c>null</c> if the command name is unknown) plus the trimmed argument string.
    /// </summary>
    public static SlashCommandInvocation? TryParse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var trimmed = input.TrimStart();
        if (!trimmed.StartsWith('/') || trimmed.Length < 2)
            return null;

        var parts = trimmed[1..].Split(' ', 2);
        var name = parts[0];
        if (string.IsNullOrEmpty(name))
            return null;

        var args = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        return new SlashCommandInvocation(Find(name), name, args);
    }
}

/// <summary>
/// Result of <see cref="SlashCommandRegistry.TryParse"/>.
/// <c>Command</c> is null when the typed name is unknown — callers should fall back to
/// dynamic skill dispatch in that case.
/// </summary>
public sealed record SlashCommandInvocation(
    SlashCommand? Command,
    string TypedName,
    string Arguments);
