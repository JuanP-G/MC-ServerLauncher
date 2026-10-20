using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;
using McServerLauncher.Views;
using McServerLauncher.Views.ServerSettings;
using SkiaSharp;
using L = McServerLauncher.Localization.Localizer;

// Records the promo video's footage: the real views, driven by real pointer and keyboard input,
// with made-up servers, every frame at twice the window's size. Usage: Recorder <outDir> <lang> <scene...>
var outRoot = args[0];
var lang = args[1];
var scenes = args.Skip(2).ToHashSet();
var es = lang == "es";
var culture = new CultureInfo(lang switch { "es" => "es-ES", "pt" => "pt-BR", "fr" => "fr-FR", "de" => "de-DE", _ => "en-US" });
var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

var temp = Path.Combine(Path.GetTempPath(), "mcl-h7-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
PlayerHistoryStore.RootDirectory = Path.Combine(temp, "players");   // never the real history

AppBuilder.Configure<McServerLauncher.App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .LogToTrace(LogEventLevel.Error)
    .SetupWithoutStarting();
CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = culture;
CultureInfo.DefaultThreadCurrentCulture = culture; CultureInfo.DefaultThreadCurrentUICulture = culture;
_ = typeof(FluentIcons.Avalonia.SymbolIcon);
_ = new TunnelsView();

var xaml = File.ReadAllText(Path.Combine(repo, "McServerLauncher", "Views", "MainWindow.axaml"));
xaml = System.Text.RegularExpressions.Regex.Replace(xaml, @"\s+x:Class=""[^""]*""", "");
xaml = new System.Text.RegularExpressions.Regex(@"\s+Icon=""/Resources/app.ico""").Replace(xaml, "", 1);
xaml = System.Text.RegularExpressions.Regex.Replace(xaml, @"\s+x:CompileBindings=""False""", "");
xaml = System.Text.RegularExpressions.Regex.Replace(xaml, @"\s+(Click|DoubleTapped|Tapped|KeyDown|SelectionChanged|TextChanged|PointerPressed|Opened|Closed|Loaded|Opening|Closing|Checked|Unchecked|IsCheckedChanged|DropDownClosed)=""[A-Za-z_]\w*""", "");

char S = (char)0xA7;
string T(string esText, string enText) => es ? esText : enText;
var green = (IBrush)typeof(ServerViewModel).GetField("BrushGreen", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

// ---------------------------------------------------------------- demo servers
ServerViewModel Make(string name, ServerType type, string version, int port, int max, string motd, bool icon, bool world = false)
{
    var folder = Path.Combine(temp, Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "server.properties"), $"server-port={port}\nmax-players={max}\nmotd=A Minecraft Server\n");
    if (world)
    {
        Directory.CreateDirectory(Path.Combine(folder, "world", "region"));
        File.WriteAllText(Path.Combine(folder, "world", "level.dat"), new string('x', 4096));
        for (var i = 0; i < 6; i++) File.WriteAllBytes(Path.Combine(folder, "world", "region", $"r.{i}.0.mca"), new byte[300_000 + i * 50_000]);
    }
    if (icon) WriteGrassIcon(Path.Combine(folder, "server-icon.png"));
    var vm = new ServerViewModel(new ServerConfig { Name = name, FolderPath = folder, Type = type, GameVersion = version, PlayitEnabled = true });
    vm.MotdText = motd;
    if (icon) vm.ServerIcon = new Bitmap(Path.Combine(folder, "server-icon.png"));
    return vm;
}

void Run(ServerViewModel vm, string players, string cpu, string ram, string uptime, string port)
{
    vm.State = ServerState.Running;
    vm.StatusText = L.Get("Status_Running");
    vm.StatusBrush = green;
    vm.SignalBrush = green;
    vm.PlayerCountText = players;
    vm.CpuText = cpu; vm.RamText = ram; vm.UptimeText = uptime; vm.PortText = port;
    var rnd = new Random(int.Parse(port));
    vm.CpuSeries = Enumerable.Range(0, 40).Select(i => 9 + 6 * Math.Sin(i / 3.0) + rnd.NextDouble() * 4).ToList();
    vm.RamSeries = Enumerable.Range(0, 40).Select(i => 2.6 + i * 0.02 + rnd.NextDouble() * 0.1).ToList();
    vm.PlayitState = PlayitState.Running;
    vm.PlayitStatusText = L.Get("Playit_Active");
    Notify(vm);
}

void Notify(ServerViewModel vm)
{
    foreach (var name in new[] { "StartCommand", "StopCommand", "RestartCommand", "SendCommandCommand" })
        (typeof(ServerViewModel).GetProperty(name)?.GetValue(vm) as CommunityToolkit.Mvvm.Input.IRelayCommand)?.NotifyCanExecuteChanged();
}

void Console_(ServerViewModel vm, IEnumerable<string> lines, HashSet<string>? online = null)
{
    ConsoleLineKind? prev = null;
    foreach (var raw in lines)
    {
        ConsoleLine line;
        if (raw.StartsWith("L|")) line = new ConsoleLine(raw[2..], ConsoleLineKind.Launcher);
        else if (raw.StartsWith("> ")) line = new ConsoleLine(raw, ConsoleLineKind.Command);
        else { var k = ConsoleLineClassifier.Classify(raw, ConsoleSource.Stdout, prev, online); prev = k; line = new ConsoleLine(raw, k); }
        vm.ConsoleLines.Add(line);
        vm.VisibleConsoleLines.Add(line);
    }
    ConsoleKindFilter.Recount(vm.ConsoleLines, vm.ConsoleKinds);
}

string[] StartLog(string jar, string version, int port) => new[]
{
    $"L|{T("Iniciando", "Starting")}: java -Xms2G -Xmx6G -jar \"{jar}\" nogui",
    $"[18:42:03 INFO]: [bootstrap] Running Java 25 (OpenJDK 64-Bit Server VM 25+36-LTS; Eclipse Adoptium Temurin-25+36) on Windows 11 10.0 (amd64)",
    $"[18:42:07 INFO]: Loaded 1585 recipes",
    $"[18:42:08 INFO]: Loaded 1688 advancements",
    $"[18:42:08 INFO]: Starting minecraft server version {version}",
    "[18:42:08 INFO]: Loading properties",
    "[18:42:09 INFO]: Default game type: SURVIVAL",
    "[18:42:09 INFO]: Generating keypair",
    $"[18:42:09 INFO]: Starting Minecraft server on *:{port}",
    "[18:42:10 INFO]: Preparing level \"world\"",
    "[18:42:12 INFO]: Preparing spawn area: 100%",
    "[18:42:12 INFO]: Done (8.914s)! For help, type \"help\"",
};

var survivalName = T("Supervivencia", "Survival");
var survival = Make(survivalName, ServerType.Paper, "26.2", 25565, 20,
    $"{S}a{T("Supervivencia con amigos", "Survival with friends")} {S}8| {S}e{T("Temporada 3", "Season 3")}\n{S}7{T("¡El nether ya tiene estación!", "The nether hub is open!")}", true, world: true);
var creative = Make(T("Construcciones", "Creative builds"), ServerType.Fabric, "26.2", 25566, 10, T("Construcciones", "Creative builds"), false, world: true);
Directory.CreateDirectory(Path.Combine(creative.Config.FolderPath, "mods"));
var modded = Make(T("Aventura con mods", "Modded adventure"), ServerType.NeoForge, "26.1", 25567, 8, T("Aventura con mods", "Modded adventure"), false);
var skyblock = Make("Skyblock", ServerType.Purpur, "26.2", 25568, 16, "Skyblock", false);

Run(survival, "3/20", "14 %", "3.4 GB", "1h 16m", "25565");
foreach (var p in new[] { "Steve", "Alex", "Kai" }) survival.ConnectedPlayers.Add(p);
survival.PlayerCountText = "3/20";
survival.TunnelAddress = "survival-friends.gl.joinmc.link";
survival.TunnelState = TunnelAddressState.Ready;
var online = new HashSet<string> { "Steve", "Alex", "Kai" };
Console_(survival, StartLog("paper-26.2.jar", "26.2", 25565).Concat(new[]
{
    "[18:44:31 INFO]: Steve joined the game",
    $"[18:44:35 INFO]: <Steve> {T("¡buenas! ¿alguien se apunta al nether?", "evening! anyone up for the nether?")}",
    "[18:45:02 INFO]: Alex joined the game",
    $"[18:45:10 INFO]: <Alex> {T("dame 5 minutos, que acabo la granja", "give me 5 min, finishing the farm")}",
    "[18:51:20 INFO]: Kai joined the game",
    "[18:52:03 INFO]: Steve has made the advancement [We Need to Go Deeper]",
    "[18:53:47 INFO]: Alex was slain by Zombified Piglin",
    $"[18:53:52 INFO]: <Alex> {T("...otra vez no", "...not again")}",
    $"[18:58:14 INFO]: <Kai> {T("¿quién viene a asaltar un bastión?", "who wants to raid a bastion?")}",
}), online);
SeedHistory(survival);
foreach (var (vm, addr) in new[] { (creative, "creative-builds.gl.joinmc.link"), (skyblock, "skyblock-island.gl.joinmc.link") })
{
    vm.PlayitState = PlayitState.Running;
    vm.PlayitStatusText = L.Get("Playit_Active");
    vm.TunnelAddress = addr;
    vm.TunnelState = TunnelAddressState.Ready;
}
var stub = new Stub();
foreach (var s in new[] { survival, creative, modded, skyblock }) stub.Servers.Add(s);
stub.SelectedServer = survival;
stub.Tunnels = new TunnelsViewModel(stub.Servers, Stub.DemoSettings, new AppSettingsService(), () => null, _ => Task.CompletedTask);
typeof(SettingsViewModel).GetProperty("SaveOverride", BindingFlags.NonPublic | BindingFlags.Instance)!
    .SetValue(stub.Settings, (Action<AppSettings>)(_ => { }));

// ---------------------------------------------------------------- the window and the recorder
var w = Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader.Parse<Window>(xaml, typeof(ServerCardView).Assembly);
w.DataContext = stub;
w.Width = 1600; w.Height = 900;
w.Show();
var rec = new Rec(w);
rec.Settle(500);

DemoTunnels();
if (scenes.Contains("tour")) SceneTour();
if (scenes.Contains("internet")) SceneInternet();
if (scenes.Contains("mods")) SceneMods();
if (scenes.Contains("backups")) SceneBackups();
if (scenes.Contains("update")) SceneUpdate();
if (scenes.Contains("create")) SceneCreate();
if (scenes.Contains("hero")) SceneHero();
if (scenes.Contains("console")) SceneConsole();
if (scenes.Contains("players")) ScenePlayers();
if (scenes.Contains("bedrock")) SceneBedrock();
if (scenes.Contains("sleep")) SceneSleep();
Console.WriteLine("done");
return 0;

// ---------------------------------------------------------------- scenes
void SeedHistory(ServerViewModel vm)
{
    var store = PlayerHistoryStore.For(vm.Config.Id);
    var now = DateTime.UtcNow;
    var who = new[] { "Steve", "Alex", "Kai", "Sunny", "Zuri", "Ari", "Noor", "Efe", "Makena" };
    var rnd = new Random(11);
    for (var d = 12; d >= 0; d--)
    for (var k = 0; k < who.Length; k++)
    {
        if (rnd.NextDouble() < 0.45) continue;
        var start = now.AddDays(-d).AddHours(-rnd.Next(1, 6)).AddMinutes(-rnd.Next(60));
        store.Record(new PlayerEvent(PlayerEventKind.Join, who[k], null), start);
        if (rnd.NextDouble() < 0.5) store.Record(new PlayerEvent(PlayerEventKind.Chat, who[k], T("¿alguien tiene hierro?", "anyone got iron?")), start.AddMinutes(5));
        if (rnd.NextDouble() < 0.2) store.Record(new PlayerEvent(PlayerEventKind.Death, who[k], who[k] + " fell from a high place"), start.AddMinutes(20));
        if (d == 0 && k < 3) continue;   // Steve, Alex and Kai are on now
        store.Record(new PlayerEvent(PlayerEventKind.Leave, who[k], null), start.AddMinutes(rnd.Next(25, 140)));
    }
    store.Flush();
    vm.History.Refresh();
}

void DemoTunnels()
{
    PlayitApiService.PlayitTunnel Tn(string id, string name, int port, string? addr) => new(id, name, port, addr, null, "tcp", port);
    var tunnels = new List<PlayitApiService.PlayitTunnel>
    {
        Tn("t1", survivalName, 25565, "survival-friends.gl.joinmc.link"),
        Tn("t2", creative.Name, 25566, "creative-builds.gl.joinmc.link"),
        Tn("t3", creative.Name + " 2", 25566, "creative-builds-2.gl.joinmc.link"),
        Tn("t4", "Skyblock", 25568, "skyblock-island.gl.joinmc.link"),
        Tn("t5", T("prueba-vieja", "old-test"), 25590, "old-test.gl.joinmc.link"),
    };
    var servers = stub.Servers.Select(s => new ServerPorts(s.Config.Id, s.Name, int.Parse(File.ReadAllLines(s.Config.PropertiesPath)[0].Split('=')[1]), null, true)).ToList();
    stub.Tunnels.ShowReport(TunnelInventory.Build(tunnels, servers));
}

void SceneCreate()
{
    stub.SelectedServer = survival; stub.Panel = null; stub.Section = 0;
    rec.Begin(Path.Combine(outRoot, $"create-{lang}"), new Point(700, 420));
    rec.Hold(700);
    NewServerView? panel = null;
    stub.OnNew = () =>
    {
        panel = new NewServerView(new[] { 25565, 25566, 25567, 25568 });
        panel.FindControl<TextBox>("ParentFolderBox")!.Text = es ? @"D:\Servidores de Minecraft" : @"D:\Minecraft servers";
        stub.Panel = panel; stub.SelectedServer = null;
    };
    rec.ClickOn(rec.Find<Button>(b => b.Classes.Contains("newserver")));
    rec.Hold(700);
    rec.ClickOn(panel!.FindControl<Button>("CreateCard")!);
    rec.Hold(800);
    panel.FindControl<TextBox>("ParentFolderBox")!.Text = es ? @"D:\Servidores de Minecraft" : @"D:\Minecraft servers";
    rec.ClickOn(panel.FindControl<TextBox>("NameBox")!);
    rec.Type(T("Eventos", "Events"));
    rec.Hold(400);
    var paper = panel.FindControl<ServerTypePicker>("TypePicker")!.GetVisualDescendants().OfType<RadioButton>().ElementAt(1);
    rec.ClickOn(paper);
    rec.Hold(700);
    // The EULA is the user's to accept: the box beside the button, which stays shut until it is ticked.
    rec.ClickOn(panel.FindControl<CheckBox>("EulaCheck")!);
    rec.Hold(700);
    // Create: pressed, but released off the button so nothing is really downloaded.
    var create = panel.FindControl<Button>("CreateButton")!;
    rec.MoveTo(rec.Center(create));
    rec.Press();
    typeof(NewServerView).GetMethod("SetBusy", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(panel, new object[] { true });
    var log = panel.FindControl<TextBox>("ProgressLog")!;
    string[] steps =
    {
        T("Descargando Paper 26.2 (build 41)...", "Downloading Paper 26.2 (build 41)..."),
        string.Format(L.Get("Msg_DownloadingJarSize"), 52),
        T("Huella SHA-256 comprobada ✔", "SHA-256 checksum verified ✔"),
        T("Java 25 ya está instalado ✔", "Java 25 is already installed ✔"),
        T("EULA aceptado, server.properties con el puerto 25569", "EULA accepted, server.properties on port 25569"),
        T("Creando el túnel de Playit.gg...", "Creating the Playit.gg tunnel..."),
        T("Listo.", "Done."),
    };
    foreach (var line in steps) { log.Text = (log.Text + line + "\n"); rec.Hold(450); }
    // The new server joins the list, selected, and starts.
    var events = Make(T("Eventos", "Events"), ServerType.Paper, "26.3", 25569, 20, T("Eventos", "Events"), false, world: true);
    events.TunnelState = TunnelAddressState.Waiting;
    stub.Panel = null;
    stub.Servers.Add(events);
    stub.SelectedServer = events;
    events.State = ServerState.Starting; events.StatusText = L.Get("Status_Starting");
    rec.Hold(500);
    var startLines = StartLog("paper-26.3.jar", "26.3", 25569);
    for (var i = 0; i < startLines.Length; i += 3)
    {
        Console_(events, startLines.Skip(i).Take(3));
        rec.Hold(300);
    }
    Run(events, "0/20", "9 %", "1.8 GB", "0m 12s", "25569");
    events.TunnelAddress = "events-season.gl.joinmc.link";
    events.TunnelState = TunnelAddressState.Ready;
    rec.Hold(1800);
    rec.End();
}


TabItem Tab(int i) => (TabItem)rec.Find<TabControl>(t => t.Name == "ServerTabs").ContainerFromIndex(i)!;
Button Rail(int i) => rec.Find<Grid>(g => g.Name == "RailItems").GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("rail")).ElementAt(i);
System.Windows.Input.ICommand Cmd(object o, string name) => (System.Windows.Input.ICommand)o.GetType().GetProperty(name)!.GetValue(o)!;
void Reset(ServerViewModel sel, int tab = 0)
{
    stub.Panel = null; stub.Section = 0; stub.UpdateAvailable = false; rec.Overlay = null;
    stub.SelectedServer = sel;
    rec.Settle(300);
    rec.Find<TabControl>(t => t.Name == "ServerTabs").SelectedIndex = tab;
    rec.Settle(400);
}

void SceneTour()
{
    Reset(survival);
    rec.Begin(Path.Combine(outRoot, $"tour-{lang}"), new Point(800, 560));
    rec.Hold(1100);
    rec.ClickOn(Tab(1));
    rec.Hold(1500);
    rec.ClickOn(Tab(2));
    rec.Hold(1300);
    rec.ClickOn(Rail(1));
    rec.Hold(1700);
    stub.Settings.Page = SettingsPage.Notifications;   // General needs the real app for its language list
    rec.ClickOn(Rail(2));
    rec.Hold(900);
    var pages = rec.Find<StackPanel>(sp => sp.Name == "PageItems").Children.OfType<Button>().ToList();
    rec.ClickOn(pages[2]);
    rec.Hold(1300);
    rec.ClickOn(Rail(0));
    rec.Hold(300);
    rec.ClickOn(Tab(0));
    rec.Hold(900);
    rec.End();
}

void SceneInternet()
{
    Reset(survival);
    survival.TunnelAddress = null;
    survival.TunnelState = TunnelAddressState.Waiting;
    rec.Begin(Path.Combine(outRoot, $"internet-{lang}"), new Point(900, 520));
    rec.Hold(1600);
    survival.TunnelAddress = "survival-friends.gl.joinmc.link";
    survival.TunnelState = TunnelAddressState.Ready;
    rec.Hold(900);
    var copy = rec.Find<Button>(b => b.Command == survival.CopyTunnelAddressCommand);
    rec.ClickOn(copy);
    rec.Hold(700);
    var copyAt = rec.Center(copy);
    var toTunnels = rec.All<Button>(b => b != copy && Math.Abs(rec.Center(b).Y - copyAt.Y) < 4 && rec.Center(b).X > copyAt.X).First();
    rec.ClickOn(toTunnels);
    rec.Hold(1400);
    var fix = rec.All<Button>(b => b.Classes.Contains("accent") && b.FindAncestorOfType<TunnelsView>() is not null).FirstOrDefault();
    if (fix is not null) { rec.MoveTo(rec.Center(fix), 700); rec.Hold(1200); }
    rec.Hold(800);
    rec.End();
}

void SceneMods()
{
    Reset(creative);
    rec.Begin(Path.Combine(outRoot, $"mods-{lang}"), new Point(700, 300));
    rec.Hold(600);
    rec.ClickOn(Tab(2));
    rec.Hold(900);
    var search = rec.Find<TextBox>(tb => tb.Watermark?.Contains("Modrinth") == true);
    rec.ClickOn(search);
    rec.Type("lithium");
    rec.Hold(300);
    var searchCmd = Cmd(creative.Mods, "SearchCommand");
    rec.ClickOn(rec.Find<Button>(b => b.Command == searchCmd));
    for (var k = 0; k < 120 && !ResultsReady(); k++) rec.Hold(250);
    rec.Hold(900);
    rec.ClickOn(rec.Find<Button>(b => b.Content as string == L.Get("Install")));
    var folder = Path.Combine(creative.Config.FolderPath, "mods");
    for (var k = 0; k < 120 && !Directory.EnumerateFiles(folder, "*.jar").Any(); k++) rec.Hold(250);
    rec.Hold(2200);
    rec.End();
    bool ResultsReady() => rec.All<Button>(b => b.Content as string == L.Get("Install")).Any();
}

void SceneBackups()
{
    Reset(creative, 3);
    // A tab with history: the automatic pile full, the way a server that has been played on looks.
    var dir = Path.Combine(creative.Config.FolderPath, "backups");
    Directory.CreateDirectory(dir);
    var today = DateTime.Today;
    void Zip(string trigger, DateTime at, int mb)
    {
        var f = Path.Combine(dir, $"world-{at:yyyyMMdd-HHmmss}--{trigger}.zip");
        using (var fs = File.Create(f)) fs.SetLength(mb * 1024L * 1024L);
        File.SetLastWriteTime(f, at);
    }
    creative.Config.BackupRetention = 5;
    Zip("stop", today.AddHours(13.4), 182);
    Zip("auto", today.AddHours(13.05), 181);
    Zip("auto", today.AddHours(12.05), 181);
    Zip("start", today.AddHours(11.03), 180);
    Zip("stop", today.AddDays(-1).AddHours(22.6), 179);
    Cmd(creative.Backups, "RefreshCommand").Execute(null);
    rec.Settle(800);

    var backupNow = Cmd(creative.Backups, "BackupNowCommand");
    rec.Begin(Path.Combine(outRoot, $"backups-{lang}"), new Point(800, 500));
    rec.Hold(1400);
    rec.ClickOn(rec.Find<Button>(b => b.Command == backupNow));
    for (var k = 0; k < 100 && creative.Backups.ManualCount == 0; k++) rec.Hold(100);
    rec.Hold(1800);
    var manual = Cmd(creative.Backups, "ShowManualCommand");
    rec.ClickOn(rec.Find<Avalonia.Controls.Primitives.ToggleButton>(b => b.Command == manual));
    rec.Hold(1500);
    var all = Cmd(creative.Backups, "ShowAllCommand");
    rec.ClickOn(rec.Find<Avalonia.Controls.Primitives.ToggleButton>(b => b.Command == all));
    rec.Hold(1200);
    var restoreCmd = Cmd(creative.Backups, "RestoreCommand");
    var restore = rec.All<Button>(b => b.Command == restoreCmd).FirstOrDefault();
    if (restore is not null) { rec.MoveTo(rec.Center(restore), 700); rec.Hold(1400); }
    rec.End();
}

void SceneUpdate()
{
    Reset(survival);
    rec.Begin(Path.Combine(outRoot, $"update-{lang}"), new Point(760, 420));
    rec.Hold(700);
    stub.UpdateText = string.Format(L.Get("Msg_UpdateAvailableFmt"), "1.18.0");
    stub.UpdateAvailable = true;
    rec.Hold(1300);
    stub.OnUpdate = () => stub.UpdateText = L.Get("Update_Downloading");
    rec.ClickOn(rec.Find<Button>(b => b.Content as string == L.Get("Update_Now")));
    rec.Hold(1400);
    stub.UpdateText = L.Get("Msg_VerifyingChecksum");
    rec.Hold(900);
    stub.UpdateText = L.Get("Update_Installing");
    rec.Hold(1100);
    // The installer runs and the app comes back: what's new.
    stub.UpdateAvailable = false;
    var dialog = new WhatsNewDialog("1.18.0", Changelog.NotesSince(new Version(1, 17, 0), new Version(1, 18, 0)));
    dialog.Show();
    rec.Overlay = dialog;
    rec.Hold(3500);
    rec.Overlay = null;
    dialog.Hide();
    rec.End();
}


// ---------------------------------------------------------------- promo scenes
void SceneHero()
{
    // A still of the window at rest, for the moment it rises out of the ground.
    Reset(survival);
    rec.Begin(Path.Combine(outRoot, $"hero-{lang}"), new Point(1500, 860));
    rec.Hold(300);
    rec.End();
}

void SceneConsole()
{
    // The console filling up live, then a chip and a search that marks the word inside the lines.
    Reset(survival);
    survival.ConsoleLines.Clear(); survival.VisibleConsoleLines.Clear();
    ConsoleKindFilter.Recount(survival.ConsoleLines, survival.ConsoleKinds);
    rec.Begin(Path.Combine(outRoot, $"console-{lang}"), new Point(1100, 700));
    var lines = StartLog("paper-26.2.jar", "26.2", 25565).Concat(new[]
    {
        "[18:42:13 INFO]: [EssentialsX] Enabling EssentialsX v2.21.2",
        "[18:42:13 INFO]: [LuckPerms] Enabling LuckPerms v5.5.10",
        "[18:44:31 INFO]: Steve joined the game",
        $"[18:44:35 INFO]: <Steve> {T("¡buenas! ¿alguien se apunta al nether?", "evening! anyone up for the nether?")}",
        "[18:45:02 INFO]: Alex joined the game",
        $"[18:45:10 INFO]: <Alex> {T("dame 5 minutos, que acabo la granja", "give me 5 min, finishing the farm")}",
        "[18:47:55 WARN]: Can't keep up! Is the server overloaded? Running 2143ms or 42 ticks behind",
        "[18:51:20 INFO]: Kai joined the game",
        "[18:52:03 INFO]: Steve has made the advancement [We Need to Go Deeper]",
        "[18:53:47 INFO]: Alex was slain by Zombified Piglin",
        $"[18:53:52 INFO]: <Alex> {T("...otra vez no", "...not again")}",
        $"[18:58:14 INFO]: <Kai> {T("¿quién viene al nether a por un bastión?", "who's coming to the nether for a bastion?")}",
    }).ToArray();
    var online = new HashSet<string>();
    foreach (var l in lines)
    {
        if (l.Contains(" joined the game")) online.Add(l.Split(' ')[2]);
        Console_(survival, new[] { l }, online);
        rec.Settle(20);
        rec.Snap(70);
    }
    rec.Hold(500);
    var chat = rec.All<Avalonia.Controls.Primitives.ToggleButton>(b => b.Classes.Contains("kindchip")).ElementAt(2);
    rec.ClickOn(chat);
    rec.Hold(900);
    rec.ClickOn(chat);
    rec.Hold(300);
    var filter = rec.Find<TextBox>(tb => tb.Watermark == L.Get("Console_FilterPlaceholder"));
    rec.ClickOn(filter);
    rec.Type("nether");
    rec.Hold(1600);
    rec.End();
}

void ScenePlayers()
{
    // The Players tab, then one player's profile.
    Reset(survival, 1);
    rec.Begin(Path.Combine(outRoot, $"players-{lang}"), new Point(1000, 300));
    rec.Hold(900);
    var open = rec.All<Button>(b => b.Command == survival.History.OpenCommand).FirstOrDefault();
    if (open is not null) rec.ClickOn(open);
    rec.Hold(2600);
    rec.End();
}

void SceneBedrock()
{
    // Configure → Crossplay: one switch, and the Bedrock address shows up on the server.
    Reset(survival);
    rec.Begin(Path.Combine(outRoot, $"bedrock-{lang}"), new Point(1300, 140));
    rec.Hold(500);
    var configure = rec.All<Button>(b => b.Command == stub.ConfigureServerCommand).FirstOrDefault();
    if (configure is not null) rec.ClickOn(configure);
    stub.ServerSettingsPanel = new ServerSettingsView(survival, ServerSettingsPage.Crossplay);
    rec.Hold(900);
    var toggle = rec.All<ToggleSwitch>(t => t.Name == "CrossplayCheck").FirstOrDefault();
    if (toggle is not null) { rec.ClickOn(toggle); rec.Hold(1500); }
    stub.ServerSettingsPanel = null;
    survival.Config.CrossplayEnabled = true;
    survival.Config.BedrockPort = 19132;
    TrySet(survival, "BedrockHost", "survival-friends.gl.at.ply.gg");
    TrySet(survival, "BedrockPortText", "41267");
    TrySet(survival, "BedrockState", BedrockAddressState.Ready);
    Raise(survival, "IsCrossplayOn", "BedrockPortText", "BedrockLocalPortText", "BedrockStateText", "HasBedrockAddress");
    rec.Settle(200);
    rec.Hold(2200);
    rec.End();
}

void SceneSleep()
{
    survival.Config.CrossplayEnabled = false;
    TrySet(survival, "BedrockHost", null);
    Raise(survival, "IsCrossplayOn");
    // Nobody left: the countdown runs out, the server goes to sleep, someone joins and it wakes up.
    Reset(survival);
    survival.ConnectedPlayers.Clear();
    survival.PlayerCountText = "0/20";
    rec.Begin(Path.Combine(outRoot, $"sleep-{lang}"), new Point(1450, 700));
    for (var sLeft = 5; sLeft >= 1; sLeft--)
    {
        survival.IdleCountdownText = string.Format(L.Get("Idle_CountdownFmt"), $"0:0{sLeft}");
        rec.Hold(400);
    }
    survival.IdleCountdownText = null;
    Console_(survival, new[] { "L|" + string.Format(L.Get("Msg_IdleShutdownFmt"), 10) });
    survival.State = ServerState.Stopped; survival.StatusText = L.Get("Status_Stopped");
    var red = (IBrush)typeof(ServerViewModel).GetField("BrushRed", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)!;
    if (red is not null) { survival.StatusBrush = red; survival.SignalBrush = red; }
    survival.CpuText = "—"; survival.RamText = "—"; survival.UptimeText = "—";
    Notify(survival);
    rec.Hold(1400);
    Console_(survival, new[] { "L|" + L.Get("Msg_WakeStarting") });
    survival.State = ServerState.Starting; survival.StatusText = L.Get("Status_Starting");
    Notify(survival);
    rec.Hold(900);
    Console_(survival, new[] { "[19:20:41 INFO]: Done (7.102s)! For help, type \"help\"", "[19:20:49 INFO]: Steve joined the game" });
    Run(survival, "1/20", "11 %", "2.9 GB", "0m 08s", "25565");
    survival.ConnectedPlayers.Add("Steve");
    rec.Hold(1600);
    rec.End();
}

void TrySet(object o, string name, object? value)
{
    try { o.GetType().GetProperty(name)?.SetValue(o, value); } catch (Exception e) { Console.WriteLine($"set {name}: {e.Message}"); }
}

void Raise(object o, params string[] names)
{
    var m = o.GetType().GetMethod("OnPropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) });
    foreach (var n in names) m?.Invoke(o, new object?[] { n });
}

static void WriteGrassIcon(string path)
{
    var rnd = new Random(3);
    using var bmp = new SKBitmap(64, 64);
    using var c = new SKCanvas(bmp);
    var greens = new[] { "#5DA83A", "#4E9A2E", "#6DBB45", "#448A28" };
    var dirts = new[] { "#8B5E3C", "#7A5133", "#966A45", "#6B462B" };
    for (var y = 0; y < 16; y++)
    for (var x = 0; x < 16; x++)
    {
        var edge = 3 + ((x * 7 + 3) % 3 == 0 ? 1 : 0) + (x % 5 == 0 ? 1 : 0);
        var pal = y < edge ? greens : dirts;
        using var paint = new SKPaint { Color = SKColor.Parse(pal[rnd.Next(pal.Length)]) };
        c.DrawRect(x * 4, y * 4, 4, 4, paint);
    }
    using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, data.ToArray());
}

/// <summary>Drives the window like a person would and keeps every frame with where the cursor was.</summary>
class Rec
{
    private readonly Window _w;
    private string _dir = "";
    private int _i;
    private List<string> _manifest = new();
    public Point Cur;
    public const int Scale = 2;

    public Rec(Window w) => _w = w;

    public void Begin(string dir, Point start)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        _dir = dir; _i = 0; _manifest = new(); Cur = start;
        _w.MouseMove(Cur);
        Settle(300);
    }

    public void End()
    {
        File.WriteAllLines(Path.Combine(_dir, "manifest.tsv"), _manifest);
        Console.WriteLine($"{Path.GetFileName(_dir)}: {_i} frames, {_manifest.Sum(l => int.Parse(l.Split('\t')[3])) / 1000.0:0.0}s");
    }

    private static SKBitmap ToSk(RenderTargetBitmap r)
    {
        using var ms = new MemoryStream();
        r.Save(ms);
        ms.Position = 0;
        return SKBitmap.Decode(ms);
    }

    public void Pump() { for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); _w.UpdateLayout(); } }

    public void Settle(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        do { Thread.Sleep(5); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Pump(); } while (sw.ElapsedMilliseconds < ms);
    }

    public void Snap(int ms, int click = 0)
    {
        Pump();
        var file = $"f{_i:0000}.png";
        using (var rtb = new RenderTargetBitmap(new PixelSize((int)_w.Width * Scale, (int)_w.Height * Scale), new Vector(96 * Scale, 96 * Scale)))
        {
            rtb.Render(_w);
            if (Overlay is null) rtb.Save(Path.Combine(_dir, file));
            else
            {
                Overlay.UpdateLayout();
                using var ortb = new RenderTargetBitmap(new PixelSize((int)Overlay.Bounds.Width * Scale, (int)Overlay.Bounds.Height * Scale), new Vector(96 * Scale, 96 * Scale));
                ortb.Render(Overlay);
                using var a = ToSk(rtb);
                using var b = ToSk(ortb);
                using (var canvas = new SKCanvas(a))
                {
                    using var dim = new SKPaint { Color = new SKColor(0, 0, 0, 120) };
                    canvas.DrawRect(0, 0, a.Width, a.Height, dim);
                    float x = (a.Width - b.Width) / 2f, y = (a.Height - b.Height) / 2f;
                    canvas.DrawBitmap(b, x, y);
                    using var edge = new SKPaint { Color = new SKColor(255, 255, 255, 40), IsStroke = true, StrokeWidth = 1 };
                    canvas.DrawRect(x - 0.5f, y - 0.5f, b.Width + 1, b.Height + 1, edge);
                }
                using var img = SKImage.FromBitmap(a);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(_dir, file), data.ToArray());
            }
        }
        _manifest.Add($"{file}\t{Cur.X:0}\t{Cur.Y:0}\t{ms}\t{click}");
        _i++;
    }

    /// <summary>Lets time pass on screen: animations run, a frame every ~100 ms.</summary>
    public void Hold(int ms)
    {
        var n = Math.Max(1, ms / 100);
        for (var k = 0; k < n; k++) { Settle(60); Snap(ms / n); }
    }

    public Window? Overlay;

    public IEnumerable<T> All<T>(Func<T, bool> pick) where T : Visual =>
        _w.GetVisualDescendants().OfType<T>().Where(c => c.IsEffectivelyVisible && pick(c));

    public T Find<T>(Func<T, bool> pick) where T : Visual =>
        _w.GetVisualDescendants().OfType<T>().First(c => c.IsEffectivelyVisible && pick(c));

    public Point Center(Visual c) =>
        c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), _w) ?? Cur;

    public void MoveTo(Point to, int ms = 550)
    {
        var from = Cur;
        var n = Math.Max(4, ms / 55);
        for (var k = 1; k <= n; k++)
        {
            var t = (double)k / n;
            var e = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            Cur = new Point(from.X + (to.X - from.X) * e, from.Y + (to.Y - from.Y) * e);
            _w.MouseMove(Cur);
            Settle(15);
            Snap(ms / n);
        }
    }

    public void Press()
    {
        _w.MouseDown(Cur, MouseButton.Left);
        Settle(30);
        Snap(110, 1);
        // Released somewhere harmless: the pressed look without the click.
        _w.MouseUp(new Point(2, _w.Height - 2), MouseButton.Left);
        _w.MouseMove(Cur);
        Settle(30);
        Snap(110, 2);
    }

    public void Click()
    {
        _w.MouseDown(Cur, MouseButton.Left);
        Settle(30);
        Snap(110, 1);
        _w.MouseUp(Cur, MouseButton.Left);
        Settle(40);
        Snap(110, 2);
    }

    public void ClickOn(Visual c, int moveMs = 550) { MoveTo(Center(c), moveMs); Click(); }

    public void Type(string text)
    {
        for (var k = 0; k < text.Length; k++)
        {
            _w.KeyTextInput(text[k].ToString());
            Settle(10);
            if (k % 2 == 1 || k == text.Length - 1) Snap(130);
        }
    }
}

class Stub : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void On(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    static System.Windows.Input.ICommand C() => new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
    public Action? OnNew, OnUpdate, OnServers, OnTunnels, OnSettings, OnAbout;
    public System.Windows.Input.ICommand ShowServersCommand => new CommunityToolkit.Mvvm.Input.RelayCommand(() => { Section = 0; OnServers?.Invoke(); });
    public System.Windows.Input.ICommand ShowTunnelsCommand => new CommunityToolkit.Mvvm.Input.RelayCommand(() => { Section = 1; OnTunnels?.Invoke(); });
    public System.Windows.Input.ICommand ShowSettingsCommand => new CommunityToolkit.Mvvm.Input.RelayCommand(() => { Section = 2; OnSettings?.Invoke(); });
    public System.Windows.Input.ICommand ShowAboutCommand => new CommunityToolkit.Mvvm.Input.RelayCommand(() => { Section = 3; OnAbout?.Invoke(); });
    public System.Windows.Input.ICommand OpenBackupSettingsCommand { get; } = C();
    public System.Windows.Input.ICommand TestConnectionCommand { get; } = C();
    object? _settings;
    public object? ServerSettingsPanel { get => _settings; set { _settings = value; On(nameof(ServerSettingsPanel)); On(nameof(IsEditingServer)); On(nameof(ShowServerDetail)); } }
    public bool IsEditingServer => _settings is not null;
    public System.Windows.Input.ICommand RemoveServerCommand { get; } = C();
    public System.Windows.Input.ICommand EditAppearanceCommand { get; } = C();
    public System.Windows.Input.ICommand ConfigureServerCommand { get; } = C();
    public System.Windows.Input.ICommand ShowNewServerCommand => new CommunityToolkit.Mvvm.Input.RelayCommand(() => OnNew?.Invoke());
    public System.Windows.Input.ICommand CheckForUpdatesNowCommand { get; } = C();
    public System.Windows.Input.ICommand UpdateNowCommand => new CommunityToolkit.Mvvm.Input.RelayCommand(() => OnUpdate?.Invoke());
    public System.Windows.Input.ICommand DismissUpdateCommand { get; } = C();
    public System.Windows.Input.ICommand OpenLinkCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand<string>(_ => { });
    public ObservableCollection<ServerViewModel> Servers { get; } = new();
    ServerViewModel? _sel;
    public ServerViewModel? SelectedServer { get => _sel; set { _sel = value; On(nameof(SelectedServer)); On(nameof(ShowServerDetail)); On(nameof(ShowEmptyState)); On(nameof(HasSelection)); } }
    NewServerView? _panel;
    public NewServerView? Panel { set { _panel = value; On(nameof(NewServerPanel)); On(nameof(IsCreatingServer)); On(nameof(ShowServerDetail)); On(nameof(ShowEmptyState)); } }
    public NewServerView? NewServerPanel => _panel;
    public bool IsCreatingServer => _panel is not null;
    public bool HasNewServerDraft => false;
    bool _upd; string _updText = "";
    public bool UpdateAvailable { get => _upd; set { _upd = value; On(nameof(UpdateAvailable)); } }
    public string UpdateText { get => _updText; set { _updText = value; On(nameof(UpdateText)); } }
    public string NewServerTip => L.Get("New_Title");
    public bool HasSelection => _sel is not null;
    public bool ShowServerDetail => _sel is not null && _panel is null && _settings is null;
    public bool ShowEmptyState => _sel is null && _panel is null;
    int _section;
    public int Section { get => _section; set { _section = value; foreach (var n in new[] { nameof(IsServersSection), nameof(IsTunnelsSection), nameof(IsSettingsSection), nameof(IsAboutSection) }) On(n); } }
    public bool IsServersSection => _section == 0;
    public bool IsTunnelsSection => _section == 1;
    public bool IsSettingsSection => _section == 2;
    public bool IsAboutSection => _section == 3;
    public bool IsCheckingUpdates => false;
    public bool IsUpToDate => true;
    public bool UpdateCheckFailed => false;
    public string UpdateCheckText => L.Get("About_UpToDate");
    public string VersionLineText => "1.18.0";
    public static readonly AppSettings DemoSettings = new() { PlayitApiKey = "demo" };
    public SettingsViewModel Settings { get; } = new(null, new AppSettings(), new AppSettingsService(), () => { }, () => { });
    public TunnelsViewModel Tunnels { get; set; } = null!;
}
