using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>One line of the tunnels table.</summary>
/// <remarks>
/// Holds nothing but how to show a <see cref="TunnelRow"/> and the editing state of its name; every
/// action goes back to <see cref="TunnelsViewModel"/>, which is the only place that talks to the
/// account.
/// </remarks>
public partial class TunnelRowViewModel : ObservableObject
{
    private static readonly IBrush Ok = new ImmutableSolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush Warn = new ImmutableSolidColorBrush(Color.Parse("#E3A82B"));
    private static readonly IBrush NoTint = Brushes.Transparent;
    private static readonly IBrush WarnTint = new ImmutableSolidColorBrush(Color.Parse("#22E3A82B"));

    private readonly TunnelsViewModel _owner;
    private readonly TunnelRow _row;

    public TunnelRowViewModel(TunnelsViewModel owner, TunnelRow row, bool canEdit = true)
    {
        _owner = owner;
        _row = row;
        _editName = row.Tunnel.Name;
        CanEdit = canEdit;
    }

    /// <summary>
    /// Whether the key this tunnel was read with may change it. The key of an agent the user installed
    /// can only read, so its tunnels are renamed and deleted on playit.gg, and the buttons say so.
    /// </summary>
    public bool CanEdit { get; }

    public string RenameTip => Localizer.Get(CanEdit ? "Tun_RenameTip" : "Tun_ReadOnlyTip");
    public string DeleteTip => Localizer.Get(CanEdit ? "Title_DeleteTunnel" : "Tun_ReadOnlyTip");

    public string Id => _row.Tunnel.Id;
    public string Name => string.IsNullOrWhiteSpace(_row.Tunnel.Name) ? "—" : _row.Tunnel.Name;
    public string ProtoText => _row.Tunnel.IsUdp ? "UDP" : "TCP";
    public int LocalPort => _row.Tunnel.LocalPort;

    /// <summary>Where players connect. Bedrock has no SRV record, so its port is part of what they type.</summary>
    public string Address => _row.Tunnel.Address is not { Length: > 0 } host
        ? "—"
        : _row.Tunnel.IsUdp && _row.Tunnel.PublicPort > 0 ? $"{host}:{_row.Tunnel.PublicPort}" : host;

    public bool HasAddress => _row.Tunnel.Address is { Length: > 0 };

    public string ServerText => _row.Owners.Count == 0
        ? Localizer.Get("Tun_NoServer")
        : string.Join(", ", _row.Owners.Select(o => o.Name));

    public bool IsProblem => _row.Health != TunnelHealth.Ok;
    public IBrush DotBrush => IsProblem ? Warn : Ok;
    public IBrush RowBackground => IsProblem ? WarnTint : NoTint;

    public string? HintText => _row.Health switch
    {
        TunnelHealth.Orphan => Localizer.Get("Tun_Hint_Orphan"),
        TunnelHealth.Duplicate => Localizer.Get("Tun_Hint_Duplicate"),
        TunnelHealth.Shared => Localizer.Get("Tun_Hint_Shared"),
        _ => null,
    };

    public bool HasHint => HintText is not null;

    /// <summary>The name it would carry if named after its server, when that is not what it has.</summary>
    public string? SuggestedName => _row.NameDiffers ? _row.SuggestedName : null;

    public bool ShowSuggestion => SuggestedName is not null && !IsEditing && CanEdit;
    public string SuggestionText => string.Format(Localizer.Get("Tun_UseNameFmt"), SuggestedName);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuggestion), nameof(IsNotEditing))]
    private bool _isEditing;

    public bool IsNotEditing => !IsEditing;

    [ObservableProperty]
    private string _editName;

    [RelayCommand]
    private void BeginRename()
    {
        if (!CanEdit) { _owner.OpenWebForChange(); return; }
        EditName = _row.Tunnel.Name;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelRename() => IsEditing = false;

    [RelayCommand]
    private async Task ConfirmRename()
    {
        var name = EditName?.Trim();
        IsEditing = false;
        if (string.IsNullOrEmpty(name) || name == _row.Tunnel.Name) return;
        await _owner.RenameAsync(Id, name);
    }

    [RelayCommand]
    private Task UseSuggested() =>
        SuggestedName is { } name ? _owner.RenameAsync(Id, name) : Task.CompletedTask;

    [RelayCommand]
    private Task Copy() => _owner.CopyAsync(Address);

    [RelayCommand]
    private Task Delete()
    {
        if (CanEdit) return _owner.DeleteAsync(Id, Name);
        _owner.OpenWebForChange();
        return Task.CompletedTask;
    }
}
