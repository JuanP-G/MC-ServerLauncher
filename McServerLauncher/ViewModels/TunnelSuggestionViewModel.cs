using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>One line of the "things I can fix" panel: what is wrong, and the button that fixes it.</summary>
public sealed partial class TunnelSuggestionViewModel
{
    private readonly TunnelsViewModel _owner;
    private readonly TunnelSuggestion _suggestion;

    public TunnelSuggestionViewModel(TunnelsViewModel owner, TunnelSuggestion suggestion, bool canFixHere = true)
    {
        _owner = owner;
        _suggestion = suggestion;
        CanFixHere = canFixHere;
    }

    /// <summary>False when the only key that can see the tunnel is read-only: the fix is then made on playit.gg.</summary>
    public bool CanFixHere { get; }

    public bool IsProblem => _suggestion.IsProblem;

    public string Text => _suggestion.Kind switch
    {
        TunnelSuggestionKind.DeleteOrphan =>
            string.Format(Localizer.Get("Tun_Fix_OrphanFmt"), _suggestion.Subject),
        TunnelSuggestionKind.DeleteDuplicate =>
            string.Format(Localizer.Get("Tun_Fix_DuplicateFmt"), _suggestion.Subject, _suggestion.Other),
        TunnelSuggestionKind.SharedPort =>
            string.Format(Localizer.Get("Tun_Fix_SharedFmt"), _suggestion.Other, _suggestion.Subject),
        TunnelSuggestionKind.CreateJava =>
            string.Format(Localizer.Get("Tun_Fix_CreateJavaFmt"), _suggestion.Subject),
        TunnelSuggestionKind.CreateBedrock =>
            string.Format(Localizer.Get("Tun_Fix_CreateBedrockFmt"), _suggestion.Subject),
        _ => string.Format(Localizer.Get("Tun_Fix_RenameFmt"), _suggestion.Count),
    };

    public string ButtonText => !CanFixHere ? Localizer.Get("Tun_Fix_OnWeb") : _suggestion.Kind switch
    {
        TunnelSuggestionKind.DeleteOrphan or TunnelSuggestionKind.DeleteDuplicate => Localizer.Get("Tun_Fix_Delete"),
        TunnelSuggestionKind.SharedPort => string.Format(Localizer.Get("Tun_Fix_ChangePortFmt"), _suggestion.Subject),
        TunnelSuggestionKind.CreateJava => Localizer.Get("Tun_Fix_Create"),
        TunnelSuggestionKind.CreateBedrock => Localizer.Get("Tun_Fix_CreateBedrock"),
        _ => Localizer.Get("Tun_Fix_RenameAll"),
    };

    [RelayCommand]
    private Task Apply()
    {
        if (CanFixHere) return _owner.ApplyAsync(_suggestion);
        _owner.OpenWebForChange();
        return Task.CompletedTask;
    }
}
