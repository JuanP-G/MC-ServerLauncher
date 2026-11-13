using FluentIcons.Common;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>One page of a server's settings: how the index shows it and what it owns.</summary>
/// <param name="Page">Which page.</param>
/// <param name="Icon">Its icon in the index.</param>
/// <param name="Title">The resource key of its name.</param>
/// <param name="Hint">The resource key of the line under the title, and of the index's tooltip.</param>
/// <param name="Group">
/// The resource key of the heading above it in the index; empty for a gap with no heading, null for
/// none.
/// </param>
/// <param name="Fields">The <see cref="ServerConfig"/> fields it edits.</param>
/// <param name="Keys">The <c>server.properties</c> keys it edits.</param>
internal sealed record ServerSettingsPageInfo(
    ServerSettingsPage Page, Icon Icon, string Title, string Hint, string? Group,
    string[] Fields, string[] Keys);

/// <summary>Every page of a server's settings, in the order the index lists them.</summary>
/// <remarks>
/// <para>
/// One row per page, and everything that is the same kind of fact about every page in it. This used
/// to be three lists kept in step by hand: the index's icons and titles in the view, which fields and
/// keys each page owns in the draft, and every field the page can edit in another list beside it.
/// Adding a setting meant finding all three, and a field missing from one of them was saved without
/// lighting its page's dot, or lit the dot and was never saved.
/// </para>
/// <para>
/// Adding a page is now this row, its value in <see cref="ServerSettingsPage"/>, its control in
/// <see cref="ServerSettingsView.Section"/>, and its two texts in the .resx files.
/// <c>ServerSettingsPagesTests</c> fails if a page is missing from any of them.
/// </para>
/// </remarks>
internal static class ServerSettingsPages
{
    public static readonly IReadOnlyList<ServerSettingsPageInfo> All =
    [
        new(ServerSettingsPage.Appearance, Icon.Image, "Sset_Appearance", "Sset_AppearanceHint", null,
            [nameof(ServerConfig.Name)], ["motd"]),
        new(ServerSettingsPage.Game, Icon.Games, "Sset_Game", "Sset_GameHint", "Sset_GroupGame",
            [], ["gamemode", "difficulty", "max-players", "pvp", "hardcore", "allow-flight",
                 "enable-command-block", "spawn-protection"]),
        new(ServerSettingsPage.World, Icon.Globe, "Sset_World", "Sset_WorldHint", null,
            [], ["view-distance", "simulation-distance", "generate-structures"]),
        new(ServerSettingsPage.Loader, Icon.Box, "Sset_Loader", "Sset_LoaderHint", "Sset_GroupServer",
            [], []),
        new(ServerSettingsPage.Performance, Icon.Gauge, "Sset_Performance", "Sset_PerformanceHint", null,
            [nameof(ServerConfig.MemoryMode), nameof(ServerConfig.MinRamGb), nameof(ServerConfig.MaxRamGb),
             nameof(ServerConfig.JavaPath)], []),
        new(ServerSettingsPage.Network, Icon.PlugConnected, "Sset_Network", "Sset_NetworkHint", null,
            [nameof(ServerConfig.BedrockPort), nameof(ServerConfig.PlayitEnabled)],
            ["server-port", "online-mode", "white-list"]),
        new(ServerSettingsPage.Crossplay, Icon.Phone, "Sset_Crossplay", "Sset_CrossplayHint", null,
            [nameof(ServerConfig.CrossplayEnabled), nameof(ServerConfig.MultiVersionEnabled),
             nameof(ServerConfig.BedrockModContentEnabled)], []),
        new(ServerSettingsPage.Power, Icon.Power, "Sset_Power", "Sset_PowerHint", "Sset_GroupAuto",
            [nameof(ServerConfig.WakeOnDemand), nameof(ServerConfig.WakeOnlyForWhitelist),
             nameof(ServerConfig.IdleShutdownMinutes)], []),
        new(ServerSettingsPage.Backups, Icon.Archive, "Sset_Backups", "Sset_BackupsHint", null,
            [nameof(ServerConfig.BackupsEnabled), nameof(ServerConfig.BackupRetention),
             nameof(ServerConfig.ManualBackupRetention), nameof(ServerConfig.AutoBackupEnabled),
             nameof(ServerConfig.BackupIntervalMinutes)], []),
        new(ServerSettingsPage.Notifications, Icon.Alert, "Sset_Notifications", "Sset_NotificationsHint",
            null,
            [nameof(ServerConfig.UseCustomNotifications), nameof(ServerConfig.Notifications)], []),
        new(ServerSettingsPage.Storage, Icon.HardDrive, "Sset_Storage", "Sset_StorageHint", "",
            [], []),
        new(ServerSettingsPage.Advanced, Icon.Wrench, "Sset_Advanced", "Sset_AdvancedHint", null,
            [nameof(ServerConfig.FolderPath), nameof(ServerConfig.JarFile),
             nameof(ServerConfig.ExtraJvmArgs)], []),
    ];

    /// <summary>The row of <paramref name="page"/>.</summary>
    public static ServerSettingsPageInfo For(ServerSettingsPage page) => All.First(p => p.Page == page);

    /// <summary>
    /// The pages that are about Java or about <c>server.properties</c> and nothing else, which a
    /// native server (Pumpkin) has neither of.
    /// </summary>
    private static readonly ServerSettingsPage[] JavaOnly =
    [
        ServerSettingsPage.Game, ServerSettingsPage.World, ServerSettingsPage.Loader,
        ServerSettingsPage.Performance, ServerSettingsPage.Crossplay,
    ];

    /// <summary>Whether <paramref name="page"/> has anything to say about a server of this type.</summary>
    /// <remarks>
    /// A page left out is not built, so it can neither show a setting that does nothing nor try to
    /// save one into a file the server never reads.
    /// </remarks>
    public static bool IsOffered(ServerSettingsPage page, ServerType type) =>
        !ServerTypeCatalog.IsNative(type) || !JavaOnly.Contains(page);

    /// <summary>The page to open when the one asked for is not offered for this type.</summary>
    public static ServerSettingsPage Offered(ServerSettingsPage page, ServerType type) =>
        IsOffered(page, type) ? page : ServerSettingsPage.Appearance;

    /// <summary>Every <see cref="ServerConfig"/> field the page edits, whichever page shows it.</summary>
    public static readonly string[] EditableFields = All.SelectMany(p => p.Fields).Distinct().ToArray();
}
