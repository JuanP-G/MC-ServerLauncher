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
/// <param name="PropertyChanges">The <c>server.properties</c> values written, by key.</param>
public sealed record ServerSettingsSaved(
    string OldName, bool JavaPortChanged, bool BedrockPortChanged,
    bool CrossplayTurnedOn, bool MultiVersionTurnedOn, bool ModContentTurnedOn,
    IReadOnlyDictionary<string, string> PropertyChanges);

/// <summary>Something that keeps a page from being saved, and where.</summary>
/// <param name="Page">The page it is on.</param>
/// <param name="Message">What is wrong, for the user.</param>
/// <param name="Field">The config field or <c>server.properties</c> key it is about, if one.</param>
public sealed record SettingsProblem(ServerSettingsPage Page, string Message, string? Field = null);

/// <summary>Who already has a port: the name of another server on it, or null when none is.</summary>
/// <remarks>
/// Lets the page refuse a port by saying whose it is. Two servers on one Java port fail to start, or
/// to wake on demand, one of them; two on one Bedrock port end up behind one tunnel.
/// </remarks>
/// <param name="Java">Another server whose <c>server-port</c> this is.</param>
/// <param name="Bedrock">Another server whose Bedrock port this is.</param>
public sealed record PortOwners(Func<int, string?> Java, Func<int, string?> Bedrock)
{
    /// <summary>Nobody else: for a page with no other servers to ask about.</summary>
    public static readonly PortOwners None = new(_ => null, _ => null);
}

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
    private readonly ServerPropertiesService _service = new();
    private readonly ServerIconService _icons = new();
    private readonly PortOwners _portOwners;

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

    /// <summary>
    /// Raised when something other than the page changed a field it shows, and the page took it in.
    /// </summary>
    public event Action? ChangedOutside;

    /// <param name="live">The server being configured.</param>
    /// <param name="portOwners">
    /// The name of another server already on a Bedrock port, or null when none is. Lets the page
    /// refuse a port by saying whose it is, instead of letting two servers end up behind one tunnel.
    /// </param>
    public ServerSettingsDraft(ServerConfig live, PortOwners? portOwners = null)
    {
        Live = live;
        _portOwners = portOwners ?? PortOwners.None;

        // The per-server notification override must be non-null for the checkboxes to bind. Seeded
        // on the live config as well, as the edit dialog always did, so that it is not a change.
        Live.Notifications ??= NotificationPreferences.Global.Clone();

        Config = Copy(live);
        _baseline = Copy(live);
        CoerceToType();
        _file = _service.Read(live.PropertiesPath);

        Config.PropertyChanged += OnConfigChanged;
        Live.PropertyChanged += OnLiveChanged;
        WatchNotifications();
    }

    /// <summary>Stops following the live config: the page is gone.</summary>
    public void Detach() => Live.PropertyChanged -= OnLiveChanged;

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
        if (page == ServerSettingsPage.Appearance && (NewIcon is not null || RemoveIcon)) return true;
        var info = ServerSettingsPages.For(page);
        return info.Fields.Any(f => !Same(Config, _baseline, f)) || info.Keys.Any(PropertyChanged);
    }

    /// <summary>The value <paramref name="field"/> had when the page was opened or last saved.</summary>
    public object? Saved(string field) => typeof(ServerConfig).GetProperty(field)!.GetValue(_baseline);

    /// <summary>The pages that cannot be saved as they are, each with what is wrong.</summary>
    /// <remarks>
    /// Only what was changed is checked. A server whose folder has since gone missing must still be
    /// able to change its difficulty; it was the edit dialog's to refuse, not this page's.
    /// </remarks>
    public IReadOnlyList<SettingsProblem> Validate()
    {
        var problems = new List<SettingsProblem>();

        if (string.IsNullOrWhiteSpace(Config.Name))
            problems.Add(new(ServerSettingsPage.Appearance, Localizer.Get("Msg_NameEmpty")));

        if (Config.MaxRamGb < Config.MinRamGb)
            problems.Add(new(ServerSettingsPage.Performance, Localizer.Get("Msg_RamMaxMin")));

        if (!Same(Config, _baseline, nameof(ServerConfig.BedrockPort))
            && CrossplayService.EffectiveBedrockPort(Live) is not null)
        {
            var wanted = Config.BedrockPort;
            if (wanted < 1024 || wanted > 65535)
                problems.Add(new(ServerSettingsPage.Network, Localizer.Get("Cfg_BedrockPortRange"),
                    nameof(ServerConfig.BedrockPort)));
            else if (_portOwners.Bedrock(wanted) is { } owner)
                problems.Add(new(ServerSettingsPage.Network,
                    string.Format(Localizer.Get("Cfg_BedrockPortTakenFmt"), owner), nameof(ServerConfig.BedrockPort)));
        }

        if (PropertyChanged("server-port") && int.TryParse(Get("server-port"), out var javaPort)
            && _portOwners.Java(javaPort) is { } javaOwner)
            problems.Add(new(ServerSettingsPage.Network,
                string.Format(Localizer.Get("Cfg_JavaPortTakenFmt"), javaOwner), "server-port"));

        if (!Same(Config, _baseline, nameof(ServerConfig.FolderPath)) && !Directory.Exists(Config.FolderPath))
            problems.Add(new(ServerSettingsPage.Advanced, Localizer.Get("Msg_FolderNotExist")));

        return problems;
    }

    /// <summary>Puts every page back the way it was saved.</summary>
    public void Revert()
    {
        foreach (var field in ServerSettingsPages.EditableFields)
            if (!Same(Config, _baseline, field)) CopyField(_baseline, Config, field);
        foreach (var key in _propsBaseline.Keys) _props[key] = _propsBaseline[key];
        NewIcon = null;
        RemoveIcon = false;
        Changed?.Invoke();
    }

    /// <summary>Writes what changed to the server: <c>server.properties</c>, its icon, and its config.</summary>
    /// <remarks>
    /// <para>
    /// The files go first, into the folder the draft names — the folder may be what changed — and the
    /// live config last. It used to be the other way round, so a file that could not be written (a
    /// folder that had gone missing, a file in use) left the live config already changed: the server
    /// list showed the new name and RAM, Discard only put the page back, and the next save of
    /// <c>servers.json</c> made the half-done change permanent. Now a failure leaves the server as it
    /// was.
    /// </para>
    /// <para>
    /// Each file counts as saved as soon as it is written, so a failure after it does not leave the
    /// page claiming something unsaved that is already on disk. Throws on the first file that cannot
    /// be written; the page keeps the rest so it can be saved again.
    /// </para>
    /// <para>
    /// Last also means the live config's own reactions run with the files already in place: turning
    /// on "join to start it" in the same save as a new port opens the listener on the new port.
    /// </para>
    /// </remarks>
    public ServerSettingsSaved Apply()
    {
        // Only what changed: rewriting an untouched value would reformat a MOTD written by hand, and
        // would create server.properties on a server that never ran.
        var changes = _props.Where(p => PropertyChanged(p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

        var saved = new ServerSettingsSaved(
            OldName: Live.Name,
            JavaPortChanged: changes.ContainsKey("server-port"),
            BedrockPortChanged: !Same(Config, _baseline, nameof(ServerConfig.BedrockPort)),
            CrossplayTurnedOn: !_baseline.CrossplayEnabled && Config.CrossplayEnabled,
            MultiVersionTurnedOn: !_baseline.MultiVersionEnabled && Config.MultiVersionEnabled,
            ModContentTurnedOn: !_baseline.BedrockModContentEnabled && Config.BedrockModContentEnabled,
            PropertyChanges: changes);

        Config.Name = Config.Name.Trim();

        if (changes.Count > 0)
        {
            _service.Update(Config.PropertiesPath, changes);
            foreach (var key in changes.Keys) _propsBaseline[key] = _props[key];
            _file = _service.Read(Config.PropertiesPath);
        }

        if (NewIcon is not null) _icons.WriteIcon(Config.FolderPath, NewIcon);
        else if (RemoveIcon) _icons.RemoveIcon(Config.FolderPath);
        NewIcon = null;
        RemoveIcon = false;

        if (saved.BedrockPortChanged && CrossplayService.EffectiveBedrockPort(Config) is not null)
            new CrossplayService().WriteConfig(Config, null);

        foreach (var field in ServerSettingsPages.EditableFields)
            if (!Same(Config, _baseline, field)) CopyField(Config, Live, field);
        _baseline = Copy(Config);

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

    /// <summary>
    /// Takes in a change to the live config that did not come from this page, unless the user has
    /// edited that field here — their edit is what Save is for.
    /// </summary>
    /// <remarks>
    /// The page used to read the live config once. Whatever changed it afterwards went unseen: the
    /// app switching multi-version or crossplay back off when their install failed (the switch went
    /// on saying on, and saving it "on" again was no change, so nothing retried), the Bedrock port
    /// a crossplay setup picked, the folder renamed for Paper, the Java found for the server.
    /// </remarks>
    private void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        var field = e.PropertyName;
        if (field is null || !ServerSettingsPages.EditableFields.Contains(field)) return;
        if (!Same(Config, _baseline, field)) return;   // the user's own edit, for Save to decide
        if (Same(Live, _baseline, field)) return;      // nothing new: Save copying it there, say

        CopyField(Live, Config, field);
        CopyField(Live, _baseline, field);
        ChangedOutside?.Invoke();
        Changed?.Invoke();
    }

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
