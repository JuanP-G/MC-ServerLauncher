using Avalonia.Media;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// One row of the tunnels table, as something a view can bind to.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="TunnelInventory.Row"/>, split from it the same way
/// <c>NotificationBrushes</c> is split from <c>NotificationPalette</c>: deciding whether a tunnel is
/// an orphan is a rule and belongs somewhere that knows nothing about a UI framework; turning that
/// verdict into an amber pill belongs somewhere a UI can reach.
/// </remarks>
public sealed class TunnelRowViewModel
{
    private readonly TunnelInventory.Row _row;

    public TunnelRowViewModel(TunnelInventory.Row row) => _row = row;

    public string Id => _row.Tunnel.Id;

    /// <summary>The public address, or the tunnel's own name when it has none yet.</summary>
    public string Address => _row.Tunnel.Address ?? _row.Tunnel.Name;

    public string Proto => _row.IsBedrock ? "UDP" : "TCP";

    public string LocalPort => _row.Tunnel.LocalPort.ToString();

    public int LocalPortNumber => _row.Tunnel.LocalPort;

    public bool IsBedrock => _row.IsBedrock;

    /// <summary>The server this points at, or "no server" when nothing does.</summary>
    public string OwnerText => _row.Owner is null
        ? Localizer.Get("Tun_NoOwner")
        : _row.Owner + (_row.IsBedrock ? " · Bedrock" : string.Empty);

    public bool HasOwner => _row.Owner is not null;

    public string StatusText => Localizer.Get(_row.Health switch
    {
        TunnelHealth.Orphan => "Tun_Orphan",
        TunnelHealth.PortClash => "Tun_Clash",
        _ => "Tun_InUse",
    });

    /// <summary>Why this row is flagged. Empty when it is not.</summary>
    /// <remarks>
    /// Written out under the row rather than hidden in a tooltip. "Orphan" alone is a label to stare
    /// at; "left over from a server that no longer exists" is something to act on, and the person
    /// reading it has usually never heard the word in this sense before.
    /// </remarks>
    public string HintText => _row.Health switch
    {
        TunnelHealth.Orphan => Localizer.Get("Tun_OrphanHint"),
        TunnelHealth.PortClash => Localizer.Get("Tun_ClashHint"),
        _ => string.Empty,
    };

    public bool HasHint => HintText.Length > 0;

    public bool NeedsAttention => _row.NeedsAttention;

    public IBrush StatusBrush => new SolidColorBrush(Color.Parse(_row.Health switch
    {
        TunnelHealth.Orphan => "#F0C468",
        TunnelHealth.PortClash => "#F08A93",
        _ => "#6FD97E",
    }));

    public IBrush RowBrush => new SolidColorBrush(Color.Parse(_row.Health switch
    {
        TunnelHealth.Orphan => "#14E3A82B",
        TunnelHealth.PortClash => "#14E05561",
        _ => "#00000000",
    }));
}
