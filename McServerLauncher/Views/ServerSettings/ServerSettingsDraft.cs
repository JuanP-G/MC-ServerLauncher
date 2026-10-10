using System.ComponentModel;
using System.IO;
using System.Text.Json;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>The pages of a server's settings, in the order the index lists them.</summary>
public enum ServerSettingsPage
{
    Appearance,
    Game,
    World,
    Loader,
    Performance,
    Network,
    Crossplay,
    Power,
    Backups,
    Notifications,
    Advanced
}

/// <summary>What a save changed, for the steps only the main window can take afterwards.</summary>
/// <param name="OldName">The name before the save: the tunnels are found by it.</param>
/// <param name="JavaPortChanged">The server's own port moved, so the tunnels list may be out of date.</param>
/// <param name="BedrockPortChanged">Geyser's port moved, so its tunnel has to be looked up again.</param>
/// <param name="CrossplayTurnedOn">Crossplay was switched on and has to be installed.</param>
/// <param name="MultiVersionTurnedOn">Version bridging was switched on and has to be installed.</param>
/// <param name="ModContentTurnedOn">Hydraulic was switched on and has to be installed.</param>
public sealed record ServerSettingsSaved(
    string OldName, bool JavaPortChanged, bool BedrockPortChanged,
    bool CrossplayTurnedOn, bool MultiVersionTurnedOn, bool ModContentTurnedOn);

/// <summary>
/// Everything a server's settings page has changed and not yet saved: a copy of its
/// <see cref="ServerConfig"/>, the <c>server.properties</c> values it shows, and the new icon.
/// </summary>
/// <remarks>
/// <para>
/// The page used to be three dialogs, and the main one wrote straight into the live config — the
/// same instance the server list shows — and put a snapshot back on Cancel. That only worked
/// because a dialog is modal: nothing else could look at the config while it was half edited. A
/// page in the window is not modal, so the controls edit <see cref="Config"/>, a copy, and nothing
/// reaches the server until <see cref="Apply"/>.
/// </para>
/// <para>
/// Saving copies back only the fields that changed. The live config goes on being written while
/// the page is open — a loader install, the seed read from the console — and copying the whole
/// draft over it would put back whatever it held when the page was opened.
/// </para>
/// </remarks>
public sealed class ServerSettingsDraft
{
    /// <summary>The fields of <see cref="ServerConfig"/> the page edits.</summary>
    internal static readonly string[] EditableFields =
    [
        nameof(ServerConfig.Name), nameof(ServerConfig.FolderPath), nameof(ServerConfig.JarFile),
        nameof(ServerConfig.JavaPath), nameof(ServerConfig.MinRamGb), nameof(ServerConfig.MaxRamGb),
        nameof(ServerConfig.ExtraJvmArgs), nameof(ServerConfig.PlayitEnabled),
        nameof(ServerConfig.IdleShutdownMinutes), nameof(ServerConfig.WakeOnlyForWhitelist),
        nameof(ServerConfig.WakeOnDemand), nameof(ServerConfig.CrossplayEnabled),
        nameof(ServerConfig.MultiVersionEnabled), nameof(ServerConfig.BedrockModContentEnabled),
        nameof(ServerConfig.BedrockPort), nameof(ServerConfig.BackupsEnabled),
        nameof(ServerConfig.BackupRetention), nameof(ServerConfig.AutoBackupEnabled),
        nameof(ServerConfig.BackupIntervalMinutes), nameof(ServerConfig.ManualBackupRetention),
        nameof(ServerConfig.UseCustomNotifications), nameof(ServerConfig.Notifications),
    ];

    /// <summary>Which config fields and which <c>server.properties</c> keys each page owns.</summary>
    /// <remarks>What puts the dot beside a page in the index, and what Discard has to undo.</remarks>
    internal static readonly IReadOnlyDictionary<ServerSettingsPage, (string[] Fields, string[] Keys)> Owned =
        new Dictionary<ServerSettingsPage, (string[], string[])>
        {
            [ServerSettingsPage.Appearance] = ([nameof(ServerConfig.Name)], ["motd"]),
            [ServerSettingsPage.Game] = ([], ["gamemode", "difficulty", "max-players", "pvp", "hardcore",
                                              "allow-flight", "enable-command-block", "spawn-protection"]),
            [ServerSettingsPage.World] = ([], ["view-distance", "simulation-distance", "generate-structures"]),
            [ServerSettingsPage.Loader] = ([], []),
            [ServerSettingsPage.Performance] = ([nameof(ServerConfig.MinRamGb), nameof(ServerConfig.MaxRamGb),
                                                 nameof(ServerConfig.JavaPath)], []),
            [ServerSettingsPage.Network] = ([nameof(ServerConfig.BedrockPort), nameof(ServerConfig.PlayitEnabled)],
                                            ["server-port", "online-mode", "white-list"]),
            [ServerSettingsPage.Crossplay] = ([nameof(ServerConfig.CrossplayEnabled), nameof(ServerConfig.MultiVersionEnabled),
                                               nameof(ServerConfig.BedrockModContentEnabled)], []),
            [ServerSettingsPage.Power] = ([nameof(ServerConfig.WakeOnDemand), nameof(ServerConfig.WakeOnlyForWhitelist),
                                           nameof(ServerConfig.IdleShutdownMinutes)], []),
            [ServerSettingsPage.Backups] = ([nameof(ServerConfig.BackupsEnabled), nameof(ServerConfig.BackupRetention),
                                             nameof(ServerConfig.ManualBackupRetention), nameof(ServerConfig.AutoBackupEnabled),
                                             nameof(ServerConfig.BackupIntervalMinutes)], []),
            [ServerSettingsPage.Notifications] = ([nameof(ServerConfig.UseCustomNotifications),
                                                   nameof(ServerConfig.Notifications)], []),
            [ServerSettingsPage.Advanced] = ([nameof(ServerConfig.FolderPath), nameof(ServerConfig.JarFile),
                                              nameof(ServerConfig.ExtraJvmArgs)], []),
        };

    private readonly ServerPropertiesService _service = new();
    private readonly ServerIconService _icons = new();
    private readonly Func<int, string?> _bedrockPortOwner;

    private ServerConfig _baseline;
    private IDictionary<string, string> _file = new Dictionary<string, string>();
    private readonly Dictionary<string, string> _props = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _propsBaseline = new(StringComparer.OrdinalIgnoreCase);
    private NotificationSettings? _watchedNotifications;

    /// <summary>The server's own config, the one the rest of the app shows.</summary>
    public ServerConfig Live { get; }

    /// <summary>The copy the controls are bound to.</summary>
    public ServerConfig Config { get; }

    /// <summary>The icon picked and not yet written, already rendered the way the server stores it.</summary>
    public byte[]? NewIcon { get; private set; }

    /// <summary>True when the icon is to be taken away on save.</summary>
    public bool RemoveIcon { get; private set; }

    /// <summary>Raised on every change, to the config copy, a property or the icon.</summary>
    public event Action? Changed;

    /// <param name="live">The server being configured.</param>
    /// <param name="bedrockPortOwner">
    /// The name of another server already on a Bedrock port, or null when none is. Lets the page
    /// refuse a port by saying whose it is, instead of letting two servers end up behind one tunnel.
    /// </param>
    public ServerSettingsDraft(ServerConfig live, Func<int, string?>? bedrockPortOwner = null)
    {
        Live = live;
        _bedrockPortOwner = bedrockPortOwner ?? (_ => null);

        // The per-server notification override must be non-null for the checkboxes to bind. Seeded
        // on the live config as well, as the edit dialog always did, so that it is not a change.
        Live.Notifications ??= NotificationPreferences.Global.Clone();

        Config = Copy(live);
        _baseline = Copy(live);
        CoerceToType();
        _file = _service.Read(live.PropertiesPath);

        Config.PropertyChanged += OnConfigChanged;
        WatchNotifications();
    }

    // ---------------------------------------------------------------- server.properties

    /// <summary>What the file says for <paramref name="key"/>, or null when it does not say.</summary>
    public string? FileValue(string key) => _file.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// Starts following <paramref name="key"/>, with <paramref name="value"/> as what it holds now.
    /// </summary>
    /// <remarks>
    /// The page passes the value as its control would write it, not as the file has it. A file that
    /// says <c>pvp=TRUE</c> would otherwise count as changed the moment it is shown, because the
    /// switch writes <c>true</c>.
    /// </remarks>
    public void Track(string key, string value)
    {
        _props[key] = value;
        _propsBaseline[key] = value;
    }

    public string Get(string key) => _props.TryGetValue(key, out var v) ? v : string.Empty;

    public void Set(string key, string value)
    {
        if (_props.TryGetValue(key, out var old) && old == value) return;
        _props[key] = value;
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- icon

    public void UseIcon(byte[] png)
    {
        NewIcon = png;
        RemoveIcon = false;
        Changed?.Invoke();
    }

    public void ClearIcon()
    {
        NewIcon = null;
        RemoveIcon = File.Exists(Path.Combine(Live.FolderPath, ServerIconService.FileName));
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- state

    public bool IsDirty => Enum.GetValues<ServerSettingsPage>().Any(IsPageDirty);

    public bool IsPageDirty(ServerSettingsPage page)
    {
        var (fields, keys) = Owned[page];
        if (page == ServerSettingsPage.Appearance && (NewIcon is not null || RemoveIcon)) return true;
        return fields.Any(f => !Same(Config, _baseline, f)) || keys.Any(PropertyChanged);
    }

    /// <summary>The value <paramref name="field"/> had when the page was opened or last saved.</summary>
    public object? Saved(string field) => typeof(ServerConfig).GetProperty(field)!.GetValue(_baseline);

    /// <summary>The pages that cannot be saved as they are, each with what is wrong.</summary>
    /// <remarks>
    /// Only what was changed is checked. A server whose folder has since gone missing must still be
    /// able to change its difficulty; it was the edit dialog's to refuse, not this page's.
    /// </remarks>
    public IReadOnlyList<(ServerSettingsPage Page, string Message)> Validate()
    {
        var problems = new List<(ServerSettingsPage, string)>();

        if (string.IsNullOrWhiteSpace(Config.Name))
            problems.Add((ServerSettingsPage.Appearance, Localizer.Get("Msg_NameEmpty")));

        if (Config.MaxRamGb < Config.MinRamGb)
            problems.Add((ServerSettingsPage.Performance, Localizer.Get("Msg_RamMaxMin")));

        if (!Same(Config, _baseline, nameof(ServerConfig.BedrockPort))
            && CrossplayService.EffectiveBedrockPort(Live) is not null)
        {
            var wanted = Config.BedrockPort;
            if (wanted < 1024 || wanted > 65535)
                problems.Add((ServerSettingsPage.Network, Localizer.Get("Cfg_BedrockPortRange")));
            else if (_bedrockPortOwner(wanted) is { } owner)
                problems.Add((ServerSettingsPage.Network, string.Format(Localizer.Get("Cfg_BedrockPortTakenFmt"), owner)));
        }

        if (!Same(Config, _baseline, nameof(ServerConfig.FolderPath)) && !Directory.Exists(Config.FolderPath))
            problems.Add((ServerSettingsPage.Advanced, Localizer.Get("Msg_FolderNotExist")));

        return problems;
    }

    /// <summary>Puts every page back the way it was saved.</summary>
    public void Revert()
    {
        foreach (var field in EditableFields)
            if (!Same(Config, _baseline, field)) CopyField(_baseline, Config, field);
        foreach (var key in _propsBaseline.Keys) _props[key] = _propsBaseline[key];
        NewIcon = null;
        RemoveIcon = false;
        Changed?.Invoke();
    }

    /// <summary>Writes what changed to the server: its config, <c>server.properties</c> and its icon.</summary>
    /// <remarks>
    /// The config goes first, because the folder may be what changed and the rest is written into
    /// the folder the server now has. Throws when a file cannot be written; what had been written
    /// stays written, and the page keeps the rest so it can be saved again.
    /// </remarks>
    public ServerSettingsSaved Apply()
    {
        var saved = new ServerSettingsSaved(
            OldName: Live.Name,
            JavaPortChanged: PropertyChanged("server-port"),
            BedrockPortChanged: !Same(Config, _baseline, nameof(ServerConfig.BedrockPort)),
            CrossplayTurnedOn: !_baseline.CrossplayEnabled && Config.CrossplayEnabled,
            MultiVersionTurnedOn: !_baseline.MultiVersionEnabled && Config.MultiVersionEnabled,
            ModContentTurnedOn: !_baseline.BedrockModContentEnabled && Config.BedrockModContentEnabled);

        Config.Name = Config.Name.Trim();
        foreach (var field in EditableFields)
            if (!Same(Config, _baseline, field)) CopyField(Config, Live, field);

        if (saved.BedrockPortChanged && CrossplayService.EffectiveBedrockPort(Live) is not null)
            new CrossplayService().WriteConfig(Live, null);

        // Only what changed: rewriting an untouched value would reformat a MOTD written by hand, and
        // would create server.properties on a server that never ran.
        var changes = _props.Where(p => PropertyChanged(p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (changes.Count > 0) _service.Update(Live.PropertiesPath, changes);

        if (NewIcon is not null) _icons.WriteIcon(Live.FolderPath, NewIcon);
        else if (RemoveIcon) _icons.RemoveIcon(Live.FolderPath);

        _baseline = Copy(Config);
        foreach (var key in _props.Keys) _propsBaseline[key] = _props[key];
        _file = _service.Read(Live.PropertiesPath);
        NewIcon = null;
        RemoveIcon = false;
        Changed?.Invoke();
        return saved;
    }

    /// <summary>
    /// Takes <paramref name="fields"/> from the live config into both the copy and what counts as
    /// saved: something other than this page changed them, and they are not the user's edit.
    /// </summary>
    /// <remarks>
    /// A loader install writes the type, the version, the jar and Java straight into the live
    /// config and the disk. Without this the page would go on offering the old type's options, and
    /// Save would copy the old jar back over the new one.
    /// </remarks>
    public void Adopt(params string[] fields)
    {
        foreach (var field in fields)
        {
            CopyField(Live, Config, field);
            CopyField(Live, _baseline, field);
        }
        CoerceToType(alsoLive: true);
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Switches off what the server's type cannot do, in the copy and in what counts as saved.
    /// </summary>
    /// <remarks>
    /// Not a change the user made, so it must not light the save bar the moment the page opens on a
    /// config that says, say, crossplay on a Vanilla server.
    /// </remarks>
    private void CoerceToType(bool alsoLive = false)
    {
        var type = Live.Type;
        foreach (var config in alsoLive ? new[] { Config, _baseline, Live } : [Config, _baseline])
        {
            if (!CrossplayService.CanEnable(type)) config.CrossplayEnabled = false;
            if (!MultiVersionService.CanEnable(type)) config.MultiVersionEnabled = false;
            if (!HydraulicService.CanEnable(type)) config.BedrockModContentEnabled = false;
        }
    }

    private bool PropertyChanged(string key) =>
        _props.TryGetValue(key, out var now) && (!_propsBaseline.TryGetValue(key, out var was) || was != now);

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerConfig.Notifications)) WatchNotifications();
        Changed?.Invoke();
    }

    // The per-server notification switches live in an object of their own; a tick in one of them
    // does not raise anything on the config.
    private void WatchNotifications()
    {
        if (_watchedNotifications is not null) _watchedNotifications.PropertyChanged -= OnNotificationsChanged;
        _watchedNotifications = Config.Notifications;
        if (_watchedNotifications is not null) _watchedNotifications.PropertyChanged += OnNotificationsChanged;
    }

    private void OnNotificationsChanged(object? sender, PropertyChangedEventArgs e) => Changed?.Invoke();

    private static ServerConfig Copy(ServerConfig config) =>
        JsonSerializer.Deserialize<ServerConfig>(JsonSerializer.Serialize(config))!;

    private static bool Same(ServerConfig a, ServerConfig b, string field)
    {
        var property = typeof(ServerConfig).GetProperty(field)!;
        return JsonSerializer.Serialize(property.GetValue(a)) == JsonSerializer.Serialize(property.GetValue(b));
    }

    // Notifications is the one field that is an object, and it is copied rather than shared: two
    // configs holding one instance would have the page's ticks reaching the server unsaved.
    private static void CopyField(ServerConfig from, ServerConfig to, string field)
    {
        var property = typeof(ServerConfig).GetProperty(field)!;
        var value = property.GetValue(from);
        if (value is NotificationSettings settings) value = settings.Clone();
        property.SetValue(to, value);
    }
}
