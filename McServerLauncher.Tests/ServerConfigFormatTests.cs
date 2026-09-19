using System.Text.Json;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// What <c>servers.json</c> is allowed to contain, held down independently of the class.
/// </summary>
/// <remarks>
/// <para>
/// This file is on every user's machine and is the only record of the servers they have registered.
/// <see cref="ServerConfig"/> was changed from a plain object to an observable one to stop the app
/// showing stale state, and the argument that it is safe — reflection-based System.Text.Json writes
/// public properties, the generated ones keep the old names, <c>ObservableObject</c> adds only
/// events — is an argument. This is the check.
/// </para>
/// <para>
/// Key <em>order</em> is deliberately not asserted. Nothing reads this file positionally, and
/// pinning it would break on a reordering that harms nobody. The name of every key, its type, and
/// the fact that no key appeared or vanished are the contract.
/// </para>
/// </remarks>
public class ServerConfigFormatTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "mcl-format-" + Guid.NewGuid().ToString("N"));

    public ServerConfigFormatTests() => Directory.CreateDirectory(_dataDir);

    /// <summary>
    /// Every key a server is written with. Changing this list changes what is on disk for everybody.
    /// </summary>
    private static readonly string[] ExpectedKeys =
    {
        "Id", "Name", "FolderPath", "JarFile", "Type", "GameVersion", "ModLoaderVersion", "ForgeArgs",
        "JavaPath", "MinRamGb", "MaxRamGb", "ExtraJvmArgs", "PlayitEnabled", "TunnelAddress",
        "BackupsEnabled", "BackupRetention", "IdleShutdownMinutes", "WakeOnDemand", "CrossplayEnabled",
        "BedrockModContentEnabled", "MultiVersionEnabled", "BedrockPort", "UseCustomNotifications",
        "Notifications", "LastKnownSeed"
    };

    private static readonly string[] ExpectedNotificationKeys =
    {
        "Enabled", "PlayerJoined", "PlayerLeft", "PlayerDeath", "ServerCrashed", "AutoRestartGaveUp",
        "IdleShutdown", "WokeOnDemand", "ColorInfo", "ColorSuccess", "ColorWarning", "ColorError"
    };

    /// <summary>A server with nothing left at its default, so no field can hide behind one.</summary>
    private static ServerConfig FullyPopulated() => new()
    {
        Id = "abc123",
        Name = "survival",
        FolderPath = Path.Combine("C:", "servers", "survival"),
        JarFile = "paper-1.21.1.jar",
        Type = ServerType.Paper,
        GameVersion = "1.21.1",
        ModLoaderVersion = "0.16.2",
        ForgeArgs = "1.20.1-47.2.0",
        JavaPath = "C:\\java\\bin\\java.exe",
        MinRamGb = 3,
        MaxRamGb = 6,
        ExtraJvmArgs = "-XX:+UseG1GC",
        PlayitEnabled = true,
        TunnelAddress = "algo.gl.joinmc.link",
        BackupsEnabled = false,
        BackupRetention = 9,
        IdleShutdownMinutes = 20,
        WakeOnDemand = true,
        CrossplayEnabled = true,
        BedrockModContentEnabled = true,
        MultiVersionEnabled = true,
        BedrockPort = 19133,
        UseCustomNotifications = true,
        Notifications = new NotificationSettings { PlayerDeath = false, ColorError = "#FF0000" }
    };

    /// <summary>Saves through the real service and reads the first server back as raw JSON.</summary>
    /// <remarks>Cloned, because a JsonElement does not outlive the document it came from.</remarks>
    private JsonElement SaveAndRead(params ServerConfig[] servers)
    {
        new ServerStorageService(_dataDir).Save(servers);
        var text = File.ReadAllText(Path.Combine(_dataDir, "servers.json"));
        using var document = JsonDocument.Parse(text);
        return document.RootElement[0].Clone();
    }

    [Fact]
    public void AServerIsWrittenWithExactlyTheKeysItAlwaysHad()
    {
        var written = SaveAndRead(FullyPopulated());

        var keys = written.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(ExpectedKeys.OrderBy(k => k), keys.OrderBy(k => k));

        var notifications = written.GetProperty("Notifications").EnumerateObject()
            .Select(p => p.Name).ToArray();
        Assert.Equal(ExpectedNotificationKeys.OrderBy(k => k), notifications.OrderBy(k => k));
    }

    [Fact]
    public void TheTypeIsStillANumberAndNotItsName()
    {
        // The enum's integers are the file format (see ServerConfig). A converter added to the
        // serializer options, here or anywhere upstream, would write "Paper" instead of 3 and every
        // saved server would come back as Vanilla on the next start.
        var written = SaveAndRead(FullyPopulated());

        Assert.Equal(JsonValueKind.Number, written.GetProperty("Type").ValueKind);
        Assert.Equal((int)ServerType.Paper, written.GetProperty("Type").GetInt32());
    }

    [Fact]
    public void TheComputedPathsAreNotWritten()
    {
        // Both are [JsonIgnore] and both are absolute paths: writing them would put a second,
        // rotting copy of the folder in the file.
        var written = SaveAndRead(FullyPopulated());

        Assert.False(written.TryGetProperty(nameof(ServerConfig.JarFullPath), out _));
        Assert.False(written.TryGetProperty(nameof(ServerConfig.PropertiesPath), out _));
    }

    [Fact]
    public void EverythingSurvivesASaveAndALoad()
    {
        var original = FullyPopulated();
        var storage = new ServerStorageService(_dataDir);
        storage.Save(new[] { original });

        var loaded = Assert.Single(storage.Load());

        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal(original.Name, loaded.Name);
        Assert.Equal(original.FolderPath, loaded.FolderPath);
        Assert.Equal(original.JarFile, loaded.JarFile);
        Assert.Equal(original.Type, loaded.Type);
        Assert.Equal(original.GameVersion, loaded.GameVersion);
        Assert.Equal(original.ModLoaderVersion, loaded.ModLoaderVersion);
        Assert.Equal(original.ForgeArgs, loaded.ForgeArgs);
        Assert.Equal(original.JavaPath, loaded.JavaPath);
        Assert.Equal(original.MinRamGb, loaded.MinRamGb);
        Assert.Equal(original.MaxRamGb, loaded.MaxRamGb);
        Assert.Equal(original.ExtraJvmArgs, loaded.ExtraJvmArgs);
        Assert.Equal(original.PlayitEnabled, loaded.PlayitEnabled);
        Assert.Equal(original.TunnelAddress, loaded.TunnelAddress);
        Assert.Equal(original.BackupsEnabled, loaded.BackupsEnabled);
        Assert.Equal(original.BackupRetention, loaded.BackupRetention);
        Assert.Equal(original.IdleShutdownMinutes, loaded.IdleShutdownMinutes);
        Assert.Equal(original.WakeOnDemand, loaded.WakeOnDemand);
        Assert.Equal(original.CrossplayEnabled, loaded.CrossplayEnabled);
        Assert.Equal(original.BedrockModContentEnabled, loaded.BedrockModContentEnabled);
        Assert.Equal(original.MultiVersionEnabled, loaded.MultiVersionEnabled);
        Assert.Equal(original.BedrockPort, loaded.BedrockPort);
        Assert.Equal(original.UseCustomNotifications, loaded.UseCustomNotifications);
        Assert.False(loaded.Notifications!.PlayerDeath);
        Assert.Equal("#FF0000", loaded.Notifications.ColorError);
    }

    [Fact]
    public void AFileWrittenByAnOlderVersionStillOpens()
    {
        // Verbatim shape of a real servers.json from before the model became observable, including
        // a Forge server whose Type is the integer 2. If this ever fails, somebody's server list
        // has been lost, not merely reformatted.
        const string old = """
        [
          {
            "Id": "9f1c2b",
            "Name": "Modded",
            "FolderPath": "C:\\servers\\modded",
            "JarFile": "server.jar",
            "Type": 2,
            "GameVersion": "1.20.1",
            "ModLoaderVersion": "47.2.0",
            "ForgeArgs": "1.20.1-47.2.0",
            "JavaPath": "java",
            "MinRamGb": 2,
            "MaxRamGb": 4,
            "ExtraJvmArgs": "",
            "PlayitEnabled": false,
            "TunnelAddress": null,
            "BackupsEnabled": true,
            "BackupRetention": 5,
            "IdleShutdownMinutes": 0,
            "WakeOnDemand": false,
            "CrossplayEnabled": false,
            "BedrockModContentEnabled": false,
            "MultiVersionEnabled": false,
            "BedrockPort": 0,
            "UseCustomNotifications": false,
            "Notifications": null
          }
        ]
        """;
        File.WriteAllText(Path.Combine(_dataDir, "servers.json"), old);

        var loaded = Assert.Single(new ServerStorageService(_dataDir).Load());

        Assert.Equal("9f1c2b", loaded.Id);
        Assert.Equal("Modded", loaded.Name);
        Assert.Equal(ServerType.Forge, loaded.Type);
        Assert.Equal("1.20.1", loaded.GameVersion);
        Assert.Equal("1.20.1-47.2.0", loaded.ForgeArgs);
        Assert.Equal(4, loaded.MaxRamGb);
        Assert.True(loaded.BackupsEnabled);
        Assert.Null(loaded.TunnelAddress);
        Assert.Null(loaded.Notifications);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best-effort */ }
    }
}
