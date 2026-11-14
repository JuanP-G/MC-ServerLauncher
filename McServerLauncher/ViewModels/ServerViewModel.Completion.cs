using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>One line of the suggestions list.</summary>
/// <param name="Text">What goes in the box.</param>
/// <param name="Detail">What it is, in the user's language, or its usage line.</param>
public sealed partial class SuggestionItem(string text, string detail) : ObservableObject
{
    public string Text { get; } = text;
    public string Detail { get; } = detail;
    public bool HasDetail => Detail.Length > 0;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Completing commands in the console box as they are typed: a list of what fits, the likeliest
/// one shown greyed in the box, and the command's usage under it. Tab takes it.
/// </summary>
/// <remarks>
/// <para>
/// The commands come from <see cref="CommandCatalog"/>: the vanilla list that ships with the app
/// until the server has answered its own <c>help</c>, which is asked the first time something is
/// typed in a run (not at every start, so a server nobody types into is never asked). A command's
/// branches (<c>help time</c>) are asked when they are first needed. Both questions go through
/// <see cref="CommandHelpProbe"/>, which keeps the answer out of the console.
/// </para>
/// <para>
/// The keys are the view's (it routes Tab, the arrows and Escape here); what they do is here.
/// </para>
/// </remarks>
public partial class ServerViewModel
{
    private readonly CommandCatalog _commands = CommandCatalog.BuiltIn();
    private readonly CommandHelpProbe _helpProbe = new();
    private readonly HashSet<string> _askedThisRun = new(StringComparer.OrdinalIgnoreCase);
    private Completion _completion = Completion.None;
    private string? _dismissedFor;

    /// <summary>What fits what is being typed, the likeliest first.</summary>
    public ObservableCollection<SuggestionItem> CommandSuggestions { get; } = new();

    /// <summary>The suggestion Tab would take.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommandGhost), nameof(HasCommandGhost))]
    private int _selectedSuggestion;

    /// <summary>Whether the list is showing.</summary>
    public bool HasSuggestions => CommandSuggestions.Count > 0;

    /// <summary>The rest of the selected suggestion, drawn greyed after what is typed.</summary>
    public string CommandGhost
    {
        get
        {
            if (!HasSuggestions || SelectedSuggestion < 0 || SelectedSuggestion >= CommandSuggestions.Count) return "";
            var pick = CommandSuggestions[SelectedSuggestion].Text;
            var typed = _completion.Typed;
            return pick.StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? pick[typed.Length..] : "";
        }
    }

    public bool HasCommandGhost => CommandGhost.Length > 0;

    // The usage line under the box, in three pieces so the word being typed can stand out.
    public string UsageBefore { get; private set; } = "";
    public string UsageCurrent { get; private set; } = "";
    public string UsageAfter { get; private set; } = "";
    public bool HasUsage => UsageBefore.Length + UsageCurrent.Length + UsageAfter.Length > 0;

    /// <summary>Raised after a suggestion is taken, so the view puts the caret at the end.</summary>
    public event Action? CommandCompleted;

    /// <summary>Recomputes what fits; called whenever the box changes.</summary>
    private void UpdateCompletion()
    {
        var text = CommandText ?? "";
        AskForCommandsIfNeeded(text);

        _completion = text == _dismissedFor ? Completion.None
            : CommandCompleter.Complete(text, _commands, CompletionContextNow());
        if (_completion.DetailWanted is { } command) AskForDetail(command);

        CommandSuggestions.Clear();
        foreach (var s in _completion.Suggestions)
            CommandSuggestions.Add(new SuggestionItem(s.Text, DescribeSuggestion(s)));
        SelectedSuggestion = HasSuggestions ? 0 : -1;
        MarkSelected();

        SetUsage(_completion.Usage, _completion.UsageWord);
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(CommandGhost));
        OnPropertyChanged(nameof(HasCommandGhost));
    }

    /// <summary>Tab: puts the selected suggestion in the box. False when there was none to take.</summary>
    public bool AcceptSuggestion()
    {
        if (!HasSuggestions || SelectedSuggestion < 0) return false;
        var pick = CommandSuggestions[SelectedSuggestion].Text;
        var text = CommandText ?? "";
        var from = Math.Clamp(_completion.ReplaceFrom, 0, text.Length);
        CommandText = text[..from] + pick + " ";
        CommandCompleted?.Invoke();
        return true;
    }

    /// <summary>The arrows: moves the selection, round the ends. False when the list is not showing.</summary>
    public bool MoveSuggestion(int by)
    {
        if (!HasSuggestions) return false;
        var count = CommandSuggestions.Count;
        SelectedSuggestion = ((SelectedSuggestion + by) % count + count) % count;
        MarkSelected();
        return true;
    }

    /// <summary>Escape: hides the list until the text changes. False when it was not showing.</summary>
    public bool DismissSuggestions()
    {
        if (!HasSuggestions && !HasUsage) return false;
        _dismissedFor = CommandText;
        UpdateCompletion();
        return true;
    }

    /// <summary>A click on a suggestion: selects it and takes it.</summary>
    public void PickSuggestion(SuggestionItem item)
    {
        var index = CommandSuggestions.IndexOf(item);
        if (index < 0) return;
        SelectedSuggestion = index;
        AcceptSuggestion();
    }

    private void MarkSelected()
    {
        for (var i = 0; i < CommandSuggestions.Count; i++)
            CommandSuggestions[i].IsSelected = i == SelectedSuggestion;
    }

    private void SetUsage(string? usage, int word)
    {
        UsageBefore = UsageCurrent = UsageAfter = "";
        if (usage is not null)
        {
            var words = CommandUsage.SplitTopLevel(usage);
            var current = word + 1;   // word 0 of the usage is the command's name
            if (word < 0 || current >= words.Count) UsageBefore = usage;
            else
            {
                UsageBefore = string.Join(' ', words.Take(current)) + " ";
                UsageCurrent = words[current];
                UsageAfter = current + 1 < words.Count ? " " + string.Join(' ', words.Skip(current + 1)) : "";
            }
        }
        OnPropertyChanged(nameof(UsageBefore));
        OnPropertyChanged(nameof(UsageCurrent));
        OnPropertyChanged(nameof(UsageAfter));
        OnPropertyChanged(nameof(HasUsage));
    }

    private CompletionContext CompletionContextNow() => new(
        ConnectedPlayers.ToList(),
        KnownPlayers.Concat(WhitelistPlayers).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        OpPlayers.ToList());

    // Built on first use, not in a static initialiser: SharedCommandHelp lives in another file of this
    // partial class, and the order in which two files' static fields start is not defined.
    private static Dictionary<string, string>? _helpDescriptions;
    private static Dictionary<string, string> HelpDescriptions => _helpDescriptions ??= BuildHelpDescriptions();

    private static Dictionary<string, string> BuildHelpDescriptions()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var help in SharedCommandHelp)
        {
            var name = help.Insert.Trim().Split(' ')[0];
            map.TryAdd(name, help.Description);
        }
        return map;
    }

    private static string DescribeSuggestion(Suggestion s) => s.Kind switch
    {
        SuggestionKind.Command => HelpDescriptions.TryGetValue(s.Text, out var d) ? d : s.Detail ?? "",
        SuggestionKind.OnlinePlayer => Localizer.Get("Sug_Online"),
        SuggestionKind.OnlineOperator => Localizer.Get("Sug_OnlineOp"),
        SuggestionKind.KnownPlayer => Localizer.Get("Sug_Known"),
        SuggestionKind.Selector => s.Text switch
        {
            "@a" => Localizer.Get("Sug_SelA"),
            "@p" => Localizer.Get("Sug_SelP"),
            "@r" => Localizer.Get("Sug_SelR"),
            "@s" => Localizer.Get("Sug_SelS"),
            "@e" => Localizer.Get("Sug_SelE"),
            _ => "",
        },
        _ => "",
    };

    // ---------------------------------------------------------------- asking the server

    /// <summary>The first keystroke of a run asks the server for its commands; never more than once a run.</summary>
    private void AskForCommandsIfNeeded(string text)
    {
        if (text.Length == 0 || IsNative || State != ServerState.Running) return;
        if (_helpProbe.Expire(DateTime.UtcNow) is { } stale) ApplyHelpAnswer(stale);
        if (!_askedThisRun.Add("")) return;
        SendProbe(null);
    }

    /// <summary>Asks for a command's branches, once a run, after the list itself has come back.</summary>
    private void AskForDetail(string command)
    {
        if (IsNative || State != ServerState.Running || !_commands.FromServer) return;
        if (!_askedThisRun.Add(command)) return;
        SendProbe(command);
    }

    private void SendProbe(string? topic)
    {
        if (_helpProbe.Ask(topic, DateTime.UtcNow) is not { } lines)
        {
            _askedThisRun.Remove(topic ?? "");   // one is still open: ask again on a later keystroke
            return;
        }
        foreach (var line in lines) _process.SendCommand(line);
    }

    /// <summary>Offers a console line to the open question. True when it was part of the answer.</summary>
    private bool SwallowHelpAnswer(string line)
    {
        if (!_helpProbe.IsAsking) return false;
        var swallowed = _helpProbe.TryConsume(line, DateTime.UtcNow, out var answer);
        if (answer is { } a) RunOnUi(() => ApplyHelpAnswer(a));
        return swallowed;
    }

    private void ApplyHelpAnswer(CommandHelpProbe.Answer answer)
    {
        if (answer.Topic is null) _commands.ReplaceWithServer(answer.Lines);
        else _commands.AddDetail(answer.Topic, answer.Lines);
        UpdateCompletion();
    }

    /// <summary>A new run asks again: mods or plugins may have changed while it was stopped.</summary>
    private void ResetCompletionForNewRun() => _askedThisRun.Clear();
}
