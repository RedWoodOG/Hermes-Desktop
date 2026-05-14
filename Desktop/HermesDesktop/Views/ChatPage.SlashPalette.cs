using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hermes.Agent.Core;
using Hermes.Agent.LLM;
using Hermes.Agent.Shell;
using Hermes.Agent.Skills;
using HermesDesktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.System;

namespace HermesDesktop.Views;

/// <summary>
/// Slash command palette + dispatcher for <see cref="ChatPage"/>.
///
/// Bundle E.2:
/// <list type="bullet">
///   <item>Drives <c>SlashPalette</c> visibility off <c>PromptTextBox</c> text.</item>
///   <item>Forwards keyboard navigation (Up/Down/Tab/Enter/Esc) when palette is open.</item>
///   <item>Dispatches typed commands through <see cref="SlashCommandRegistry"/>.</item>
/// </list>
/// </summary>
public partial class ChatPage
{
    // The static ResourceLoader is declared once in ChatPage.xaml.cs and shared across all
    // partial files of this class (Bugbot 2026-05-14: no redundant per-file loaders).

    public ObservableCollection<SlashCommandRow> SlashCommandSuggestions { get; } = new();

    // Last-seen usage stats from <see cref="HermesChatService"/> stream — populated by
    // <c>OnUsageReceived</c> (implemented in ChatPage.Usage.cs in Bundle E.3).
#pragma warning disable CS0649 // assigned via OnUsageReceived in Bundle E.3
    private UsageStats? _lastUsageStats;
#pragma warning restore CS0649

    private bool _slashPaletteOpen;

    /// <summary>
    /// Drive the slash palette open/closed from text-change events.
    /// Called from <c>PromptTextBox_TextChanged</c> in the main partial class.
    /// </summary>
    private void UpdateSlashPalette()
    {
        var text = PromptTextBox.Text ?? string.Empty;
        var shouldShow = text.StartsWith('/');

        if (!shouldShow)
        {
            CloseSlashPalette();
            return;
        }

        // First whitespace separator ends the "command name" region. If the user has
        // already typed an argument, we still surface the matching command but stop
        // re-filtering on it.
        var firstSpace = text.IndexOf(' ');
        var prefix = firstSpace >= 0 ? text[..firstSpace] : text;

        var matches = SlashCommandRegistry.FindByPrefix(prefix);
        RefreshSuggestions(matches);
        OpenSlashPalette();
    }

    private void RefreshSuggestions(System.Collections.Generic.IReadOnlyList<SlashCommand> matches)
    {
        SlashCommandSuggestions.Clear();
        foreach (var c in matches)
            SlashCommandSuggestions.Add(SlashCommandRow.From(c));

        SlashPaletteList.ItemsSource = SlashCommandSuggestions;
        SlashPaletteEmptyLabel.Visibility = SlashCommandSuggestions.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (SlashCommandSuggestions.Count > 0)
            SlashPaletteList.SelectedIndex = 0;
    }

    private void OpenSlashPalette()
    {
        if (_slashPaletteOpen) return;
        _slashPaletteOpen = true;
        SlashPalette.Visibility = Visibility.Visible;
    }

    private void CloseSlashPalette()
    {
        if (!_slashPaletteOpen) return;
        _slashPaletteOpen = false;
        SlashPalette.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Returns <c>true</c> if the key event was consumed by the palette (Up/Down/Tab/Enter/Esc).
    /// </summary>
    internal bool TryHandlePaletteKey(KeyRoutedEventArgs e)
    {
        if (!_slashPaletteOpen) return false;

        switch (e.Key)
        {
            case VirtualKey.Escape:
                CloseSlashPalette();
                e.Handled = true;
                return true;

            case VirtualKey.Down:
                MovePaletteSelection(+1);
                e.Handled = true;
                return true;

            case VirtualKey.Up:
                MovePaletteSelection(-1);
                e.Handled = true;
                return true;

            case VirtualKey.Tab:
                CompleteCurrentSelection();
                e.Handled = true;
                return true;

            case VirtualKey.Enter:
                // Enter only commits when the user has typed only the command name
                // (no argument yet) AND a suggestion is highlighted. Otherwise let
                // the normal Enter handler send the message.
                if (SlashCommandSuggestions.Count > 0 &&
                    !PromptTextBox.Text.Contains(' '))
                {
                    CompleteCurrentSelection();
                    e.Handled = true;
                    return true;
                }
                return false;
        }

        return false;
    }

    private void MovePaletteSelection(int delta)
    {
        if (SlashCommandSuggestions.Count == 0) return;
        var current = SlashPaletteList.SelectedIndex;
        if (current < 0) current = 0;
        var next = (current + delta + SlashCommandSuggestions.Count) % SlashCommandSuggestions.Count;
        SlashPaletteList.SelectedIndex = next;
        SlashPaletteList.ScrollIntoView(SlashPaletteList.SelectedItem);
    }

    private void CompleteCurrentSelection()
    {
        if (SlashPaletteList.SelectedItem is not SlashCommandRow row) return;
        PromptTextBox.Text = row.Name + " ";
        PromptTextBox.SelectionStart = PromptTextBox.Text.Length;
        CloseSlashPalette();
    }

    private async void SlashPaletteList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SlashCommandRow row) return;

        // Click commits immediately: complete and send as if the user pressed Enter.
        PromptTextBox.Text = row.Name;
        CloseSlashPalette();
        await SendPromptAsync();
    }

    // ── Dispatcher (replaces the old HandleSlashCommandAsync body) ──

    private async Task DispatchSlashCommandAsync(string input)
    {
        CloseSlashPalette();

        var invocation = SlashCommandRegistry.TryParse(input);
        if (invocation is null)
        {
            // Bare or malformed slash (e.g. the user submitted just "/"). The
            // SlashOutputUnknownCommand resource expects a command name, so
            // route the user to the help affordance instead of formatting a
            // sentence with a dangling bare slash. Bugbot finding: the old
            // path used .Replace("{0}", "") and produced
            // "Unknown command: /. Type /help for the full list." which is
            // technically true but reads like a parser error.
            AppendSystemMessage(ResourceLoader.GetString("SlashOutputEmptySlash"));
            return;
        }

        if (invocation.Command is null)
        {
            // Unknown name: fall through to legacy dynamic skill dispatch so
            // user-installed skills still work as virtual slash commands.
            await TryInvokeAsSkillAsync(invocation.TypedName, invocation.Arguments);
            return;
        }

        if (invocation.Command.Kind == SlashCommandKind.AgentForwarded)
        {
            await ForwardToAgentAsync(invocation.Command, invocation.Arguments);
            return;
        }

        await DispatchLocalAsync(invocation.Command, invocation.Arguments);
    }

    private async Task DispatchLocalAsync(SlashCommand command, string args)
    {
        switch (command.Name)
        {
            case "/new":
                NewChat_Click(this, new RoutedEventArgs());
                return;

            case "/clear":
                Messages.Clear();
                AppendSystemMessage(ResourceLoader.GetString("SlashOutputClearConfirmed"));
                return;

            case "/retry":
                if (!string.IsNullOrWhiteSpace(_lastPromptForRetry))
                {
                    PromptTextBox.Text = _lastPromptForRetry!;
                    await SendPromptAsync();
                }
                return;

            case "/help":
                AppendSystemMessage(BuildHelpText());
                return;

            case "/tools":
                AppendSystemMessage(BuildToolsText());
                return;

            case "/skills":
                AppendSystemMessage(BuildSkillsText());
                return;

            case "/model":
                AppendSystemMessage(BuildModelText());
                return;

            case "/usage":
                AppendSystemMessage(BuildUsageText());
                return;

            case "/version":
                AppendSystemMessage(BuildVersionText());
                return;

            case "/status":
                AppendSystemMessage(BuildStatusText());
                return;

            case "/debug":
                NavigateTo("diagnostics");
                return;

            default:
                AppendSystemMessage(string.Format(
                    CultureInfo.CurrentCulture,
                    ResourceLoader.GetString("SlashOutputUnknownCommand"),
                    command.Name.TrimStart('/')));
                return;
        }
    }

    private async Task ForwardToAgentAsync(SlashCommand command, string args)
    {
        // For agent-bound commands, we send the original "/cmd args" string back into
        // the regular chat pipeline so the agent sees a normal user message it can act on.
        var text = string.IsNullOrEmpty(args) ? command.Name : $"{command.Name} {args}";
        PromptTextBox.Text = text;
        await SendPromptAsync();
    }

    private async Task TryInvokeAsSkillAsync(string commandName, string args)
    {
        try
        {
            var invoker = App.Services.GetRequiredService<SkillInvoker>();
            SetBusy(true);
            ShowThinking(true, ResourceLoader.GetString("ChatThinkingRunningSkill"));

            var response = await invoker.InvokeAsync(commandName, args, CancellationToken.None);
            ShowThinking(false);

            if (!string.IsNullOrWhiteSpace(response))
                AppendAssistantMessage(response);
            else
                AppendSystemMessage("Skill returned an empty response.");
        }
        catch (SkillNotFoundException)
        {
            // Bugbot: thinking indicator was getting stuck on for unknown
            // commands because this catch never called ShowThinking(false)
            // (only the SkillDisabledException + generic catches did).
            ShowThinking(false);
            AppendSystemMessage(string.Format(
                CultureInfo.CurrentCulture,
                ResourceLoader.GetString("SlashOutputUnknownCommand"),
                commandName));
        }
        catch (SkillDisabledException ex)
        {
            ShowThinking(false);
            AppendSystemMessage(string.Format(
                CultureInfo.CurrentCulture,
                ResourceLoader.GetString("SlashOutputSkillDisabled"),
                ex.SkillName));
        }
        catch (Exception ex)
        {
            ShowThinking(false);
            AppendSystemMessage($"Skill error: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            PromptTextBox.Focus(FocusState.Programmatic);
        }
    }

    // ── Text builders for the local /info commands ──

    private string BuildHelpText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(ResourceLoader.GetString("SlashOutputHelpHeader"));
        sb.AppendLine();

        var groups = SlashCommandRegistry.All
            .GroupBy(c => c.Category)
            .OrderBy(g => (int)g.Key);

        foreach (var group in groups)
        {
            sb.AppendLine($"— {ResourceLoader.GetString($"SlashCategory.{group.Key}")}");
            foreach (var c in group)
            {
                var desc = ResourceLoader.GetString(c.DescriptionResourceKey);
                sb.AppendLine($"  {c.Name,-10}  {desc}");
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private string BuildToolsText()
    {
        var names = _agent.Tools.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var formatted = names.Count == 0 ? "(none)" : string.Join(", ", names);
        return string.Format(
            CultureInfo.CurrentCulture,
            ResourceLoader.GetString("SlashOutputToolsFormat"),
            names.Count, formatted);
    }

    private string BuildSkillsText()
    {
        var skillManager = App.Services.GetRequiredService<SkillManager>();
        var skills = skillManager.ListSkills();
        if (skills.Count == 0)
            return ResourceLoader.GetString("SlashOutputSkillsNone");

        var sb = new StringBuilder();
        sb.AppendLine(ResourceLoader.GetString("SlashOutputSkillsHeader"));
        foreach (var s in skills)
            sb.AppendLine($"  /{s.Name}  - {s.Description}");
        return sb.ToString().TrimEnd();
    }

    private string BuildModelText()
    {
        var snap = _runtimeStatusService.GetConfiguredSnapshot();
        return string.Format(
            CultureInfo.CurrentCulture,
            ResourceLoader.GetString("SlashOutputModelFormat"),
            snap.DisplayProvider, snap.DisplayModel);
    }

    private string BuildUsageText()
    {
        // Bugbot finding: `/usage` was reading the LAST-turn snapshot
        // (_lastUsageStats), so after multiple turns the slash command
        // disagreed with the on-screen footer (which already aggregates).
        // Source of truth for both surfaces is the per-session running
        // totals maintained by OnUsageReceived in ChatPage.Usage.cs.
        if (_lastUsageStats is null && _sessionInputTokens == 0 && _sessionOutputTokens == 0)
            return ResourceLoader.GetString("SlashOutputUsageNone");

        var total = _sessionInputTokens + _sessionOutputTokens;
        return string.Format(
            CultureInfo.CurrentCulture,
            ResourceLoader.GetString("SlashOutputUsageFormat"),
            _sessionInputTokens,
            _sessionOutputTokens,
            total);
    }

    private string BuildVersionText()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "dev";
        return string.Format(
            CultureInfo.CurrentCulture,
            ResourceLoader.GetString("SlashOutputVersionFormat"),
            version);
    }

    private string BuildStatusText()
    {
        var snap = _runtimeStatusService.GetConfiguredSnapshot();
        var state = snap.ConnectionState switch
        {
            RuntimeConnectionState.Connected => ResourceLoader.GetString("StatusConnected"),
            RuntimeConnectionState.Checking => ResourceLoader.GetString("ChatStatusChecking"),
            _ => ResourceLoader.GetString("StatusOffline"),
        };
        return $"{state} | {snap.DisplayProvider} | {snap.DisplayModel} | mode: {_chatService.CurrentPermissionMode}";
    }

    private void NavigateTo(string tag)
    {
        if (App.Current is App app && app.MainWindow is { } window)
            window.NavigateToTag(tag);
    }
}

/// <summary>
/// View-model row for the slash palette ListView. Bound through the standard <c>Binding</c>
/// markup extension (not x:Bind) so the data template can be reused across collections.
/// </summary>
public sealed class SlashCommandRow
{
    private static readonly ResourceLoader Resources = new();

    // Public settable properties (not init-only): the WinUI XAML type-info generator
    // emits direct assignments via setters, which CS8852s init-only properties.
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public static SlashCommandRow From(SlashCommand command) => new()
    {
        Name = command.Name,
        Category = Resources.GetString($"SlashCategory.{command.Category}"),
        Description = Resources.GetString(command.DescriptionResourceKey),
    };
}
