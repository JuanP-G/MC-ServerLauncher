using System;
using System.Collections.Generic;
using System.Linq;
using McServerLauncher.Models;

namespace McServerLauncher.ViewModels;

/// <summary>Work that has to happen when a field of a <see cref="ServerConfig"/> changes.</summary>
/// <remarks>
/// Only things a property notification cannot express on its own: reading a file again, closing a
/// page, going back to the store. Anything that is just "this getter now returns something else" is
/// a name in <see cref="ServerConfigEffects.Row.ServerProperties"/> instead.
/// </remarks>
[Flags]
public enum ConfigEffect
{
    None = 0,

    /// <summary>Copy the config's name into the view model's own bindable one.</summary>
    MirrorName = 1 << 0,

    /// <summary>Copy the config's tunnel address into the view model's own bindable one.</summary>
    MirrorTunnelAddress = 1 << 1,

    /// <summary>Re-read the port from server.properties.</summary>
    RereadPort = 1 << 2,

    /// <summary>Re-read the MOTD, the player count and the icon.</summary>
    RereadInfo = 1 << 3,

    /// <summary>Recompute the signal bars and the tunnel warning.</summary>
    RefreshSignal = 1 << 4,

    /// <summary>Open or close the wake-on-demand listener to match the config.</summary>
    RestartWakeListener = 1 << 5,

    /// <summary>Re-read the mods or plugins folder.</summary>
    RescanContent = 1 << 6,

    /// <summary>Rebuild the store's category chips (only the family changes them).</summary>
    RebuildTags = 1 << 7,

    /// <summary>Close the open store details page: it resolved versions against the old answer.</summary>
    CloseDetails = 1 << 8,

    /// <summary>Run the store search again, for the new type and version.</summary>
    ReSearchStore = 1 << 9,

    /// <summary>Re-read the backup list, if the tab has ever been opened.</summary>
    ReloadBackups = 1 << 10
}

/// <summary>
/// One row per <see cref="ServerConfig"/> field: what it feeds, and what has to be redone when it
/// changes.
/// </summary>
/// <remarks>
/// <para>
/// The same instinct as <see cref="Services.ServerTypeCatalog"/> — one table, and everything reads
/// from it. It exists because the bug this repository kept shipping was never a hard one: a field
/// changed, and some getter somewhere went on returning the old answer because nobody remembered it
/// was derived. Spreading the knowledge across a method per view model meant every new field was a
/// fresh chance to forget.
/// </para>
/// <para>
/// <c>ServerConfigEffectsTests</c> makes forgetting impossible rather than merely discouraged: every
/// writable property of <see cref="ServerConfig"/> must appear here exactly once, and every property
/// name a row mentions must still exist on the view model it names. A field added without a row
/// fails the build's tests on the day it is written.
/// </para>
/// <para>
/// Persisting is deliberately <em>not</em> one of the effects. The edit dialog writes into the live
/// config as the user types, so saving on every change would write servers.json on every keystroke.
/// Saving stays where it already is: once, when a dialog is accepted.
/// </para>
/// </remarks>
public static class ServerConfigEffects
{
    /// <summary>What one field of the config feeds.</summary>
    /// <param name="ConfigProperty">The <see cref="ServerConfig"/> property this row is about.</param>
    /// <param name="ServerProperties">Properties of <see cref="ServerViewModel"/> to announce.</param>
    /// <param name="ModsProperties">Properties of <see cref="ServerModsViewModel"/> to announce.</param>
    /// <param name="Effects">Work beyond announcing.</param>
    /// <param name="NothingShowsIt">
    /// Why this field feeds nothing on screen. Present exactly when the other three are empty, so
    /// that "nobody displays it" is a written decision rather than an omission.
    /// </param>
    public sealed record Row(
        string ConfigProperty,
        string[] ServerProperties,
        string[] ModsProperties,
        ConfigEffect Effects,
        string? NothingShowsIt = null);

    private static readonly string[] None = Array.Empty<string>();

    /// <summary>A row for a field nothing on screen derives from, with the reason it does not.</summary>
    private static Row Unshown(string property, string because) =>
        new(property, None, None, ConfigEffect.None, because);

    /// <summary>Every field of the config, and what depends on it.</summary>
    public static readonly Row[] All =
    {
        Unshown(nameof(ServerConfig.Id),
            "Identity for servers.json; the app shows the name instead, which can change."),

        // The card's title. Mirrored rather than announced: the view model's own Name is what the
        // list binds, and it writes back here, so the two have to be kept the same value.
        new(nameof(ServerConfig.Name), None, None, ConfigEffect.MirrorName),

        // The one field that moves everything: it is the server's whole identity on disk.
        new(nameof(ServerConfig.FolderPath), None, None,
            ConfigEffect.RereadPort | ConfigEffect.RereadInfo | ConfigEffect.RescanContent
            | ConfigEffect.ReloadBackups | ConfigEffect.RestartWakeListener),

        Unshown(nameof(ServerConfig.JarFile),
            "Read when the server is launched, and never shown."),

        new(nameof(ServerConfig.Type),
            new[]
            {
                nameof(ServerViewModel.IsModded),
                nameof(ServerViewModel.ServerTypeText),
                nameof(ServerViewModel.ServerTypeBrush)
            },
            new[]
            {
                nameof(ServerModsViewModel.IsPluginBased),
                nameof(ServerModsViewModel.ContentTabTitle),
                nameof(ServerModsViewModel.BrowseTitle),
                nameof(ServerModsViewModel.InstalledTitle),
                nameof(ServerModsViewModel.SearchPlaceholder),
                nameof(ServerModsViewModel.NoInstalledText),
                nameof(ServerModsViewModel.FilterTypeText),
                nameof(ServerModsViewModel.FilterTypeBrush),
                nameof(ServerModsViewModel.HowToPlaySteps)
            },
            // The content folder is named after the family, and converting between families
            // archives the old one, so the installed list is about a directory that has moved.
            ConfigEffect.RescanContent | ConfigEffect.RebuildTags | ConfigEffect.CloseDetails
            | ConfigEffect.ReSearchStore),

        new(nameof(ServerConfig.GameVersion),
            new[] { nameof(ServerViewModel.GameVersionText) },
            new[]
            {
                nameof(ServerModsViewModel.FilterVersionText),
                nameof(ServerModsViewModel.HowToPlaySteps)
            },
            // Results on screen were chosen for the old version; leaving them is what made an
            // install fail minutes later, far from the change that caused it.
            ConfigEffect.CloseDetails | ConfigEffect.ReSearchStore),

        Unshown(nameof(ServerConfig.ModLoaderVersion),
            "Recorded for the installer; the badge shows the type and the Minecraft version."),
        Unshown(nameof(ServerConfig.ForgeArgs),
            "The launch method for Forge and NeoForge, read when starting."),
        Unshown(nameof(ServerConfig.JavaPath),
            "Resolved again on every start, and shown only inside the edit dialog."),
        Unshown(nameof(ServerConfig.MinRamGb), "Becomes -Xms when the server starts."),
        Unshown(nameof(ServerConfig.MaxRamGb), "Becomes -Xmx when the server starts."),
        Unshown(nameof(ServerConfig.ExtraJvmArgs), "Appended to the JVM arguments on start."),

        // The signal bars warn about a server published without a tunnel.
        new(nameof(ServerConfig.PlayitEnabled), None, None, ConfigEffect.RefreshSignal),

        new(nameof(ServerConfig.TunnelAddress), None, None, ConfigEffect.MirrorTunnelAddress),

        Unshown(nameof(ServerConfig.BackupsEnabled), "Read when a backup would be made."),
        Unshown(nameof(ServerConfig.BackupRetention), "Read when pruning after a new backup."),
        Unshown(nameof(ServerConfig.IdleShutdownMinutes),
            "The countdown timer re-reads it every second while the server is running."),

        // The listener holds a real socket, so turning it on has to open one now rather than at the
        // next stop — which is what used to happen, i.e. not until the server had been run once.
        new(nameof(ServerConfig.WakeOnDemand), None, None, ConfigEffect.RestartWakeListener),

        new(nameof(ServerConfig.CrossplayEnabled),
            new[] { nameof(ServerViewModel.IsCrossplayOn) }, None, ConfigEffect.None),

        Unshown(nameof(ServerConfig.BedrockModContentEnabled),
            "A record of an install that already happened; the Mods tab shows the jars themselves."),
        Unshown(nameof(ServerConfig.MultiVersionEnabled),
            "Same: the ViaVersion jars are visible in the Mods tab on their own."),

        new(nameof(ServerConfig.BedrockPort),
            new[] { nameof(ServerViewModel.BedrockLocalPortText) }, None, ConfigEffect.None),

        Unshown(nameof(ServerConfig.UseCustomNotifications),
            "Read at the moment a notification would be raised."),
        Unshown(nameof(ServerConfig.Notifications),
            "Read at the moment a notification would be raised."),
        Unshown(nameof(ServerConfig.LastKnownSeed),
            "Read when the configuration dialog opens, which is the only place a seed is shown.")
    };

    private static readonly Dictionary<string, Row> ByProperty =
        All.ToDictionary(r => r.ConfigProperty, StringComparer.Ordinal);

    /// <summary>The row for a config property, or null if it has none (which the tests forbid).</summary>
    public static Row? For(string configProperty) =>
        ByProperty.TryGetValue(configProperty, out var row) ? row : null;

    /// <summary>Everything every field feeds, for a refresh that cannot know what moved.</summary>
    public static Row Everything { get; } = new(
        "*",
        All.SelectMany(r => r.ServerProperties).Distinct().ToArray(),
        All.SelectMany(r => r.ModsProperties).Distinct().ToArray(),
        All.Aggregate(ConfigEffect.None, (all, r) => all | r.Effects));
}
