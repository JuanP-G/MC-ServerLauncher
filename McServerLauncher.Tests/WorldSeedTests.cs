using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// Finding a world's seed, choosing one when a server is created, and linking to its map.
/// </summary>
/// <remarks>
/// <para>
/// <c>level-seed</c> is not the seed of the world: it is read once, at generation. The world keeps
/// the real one, and it has kept it in three places. The newest, <c>world_gen_settings.dat</c>,
/// came to light on a real 26.2 world whose <c>level.dat</c> had no seed at all. Every shape here
/// is the one Minecraft writes, built byte by byte.
/// </para>
/// </remarks>
public class WorldSeedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcl-seed-" + Guid.NewGuid().ToString("N"));

    public WorldSeedTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // --- A minimal NBT writer: just enough to build the files Minecraft writes ---

    /// <summary>A named tag: its type, name, and payload bytes.</summary>
    private sealed record Tag(byte Type, string Name, byte[] Payload);

    private static byte[] Str(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        return new[] { (byte)(bytes.Length >> 8), (byte)bytes.Length }.Concat(bytes).ToArray();
    }

    private static Tag Long(string name, long value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, value);
        return new Tag(4, name, b);
    }

    private static Tag Int(string name, int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        return new Tag(3, name, b);
    }

    private static Tag Text(string name, string value) => new(8, name, Str(value));

    private static Tag IntList(string name, params int[] values)
    {
        var b = new List<byte> { 3 };
        var n = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(n, values.Length);
        b.AddRange(n);
        foreach (var v in values)
        {
            BinaryPrimitives.WriteInt32BigEndian(n, v);
            b.AddRange(n);
        }
        return new Tag(9, name, b.ToArray());
    }

    private static Tag Compound(string name, params Tag[] children)
    {
        var b = new List<byte>();
        foreach (var c in children)
        {
            b.Add(c.Type);
            b.AddRange(Str(c.Name));
            b.AddRange(c.Payload);
        }
        b.Add(0);
        return new Tag(10, name, b.ToArray());
    }

    private static byte[] Gzip(Tag root)
    {
        var raw = new List<byte> { root.Type };
        raw.AddRange(Str(root.Name));
        raw.AddRange(root.Payload);

        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(raw.ToArray());
        return ms.ToArray();
    }

    private string Server(string? properties = "level-name=world\n")
    {
        var folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        if (properties is not null) File.WriteAllText(Path.Combine(folder, "server.properties"), properties);
        return folder;
    }

    private static void Put(string folder, string relative, byte[] bytes)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    // --- Where the seed lives ---

    [Fact]
    public void From26_1ItIsInWorldGenSettingsDat()
    {
        var server = Server();
        // level.dat still exists, with plenty in it, but no seed — exactly the 26.2 world that
        // showed this.
        Put(server, "world/level.dat", Gzip(Compound("", Compound("Data", Text("LevelName", "world"), Int("version", 19133)))));
        Put(server, "world/data/minecraft/world_gen_settings.dat",
            Gzip(Compound("", Compound("data", Long("seed", -8661663486445701832)), Int("DataVersion", 4700))));

        Assert.Equal(new SeedInfo(-8661663486445701832, SeedSource.World), WorldSeed.Read(server));
    }

    [Fact]
    public void From1_16ItIsInsideWorldGenSettings()
    {
        var server = Server();
        Put(server, "world/level.dat", Gzip(Compound("",
            Compound("Data",
                Text("LevelName", "world"),
                IntList("DataPacks", 1, 2, 3),        // something to skip on the way
                Compound("WorldGenSettings",
                    Compound("dimensions", Compound("minecraft:overworld", Text("type", "minecraft:overworld"))),
                    Long("seed", 1970052618634497367))))));

        Assert.Equal(1970052618634497367, WorldSeed.Read(server).Seed);
    }

    [Fact]
    public void Before1_16ItIsRandomSeed()
    {
        var server = Server();
        Put(server, "world/level.dat", Gzip(Compound("", Compound("Data", Long("RandomSeed", 42)))));

        Assert.Equal(42, WorldSeed.Read(server).Seed);
    }

    [Fact]
    public void ACustomLevelNameIsFollowed()
    {
        var server = Server("level-name=supervivencia\n");
        Put(server, "supervivencia/level.dat", Gzip(Compound("", Compound("Data", Long("RandomSeed", 7)))));
        Put(server, "world/level.dat", Gzip(Compound("", Compound("Data", Long("RandomSeed", 99)))));

        Assert.Equal(7, WorldSeed.Read(server).Seed);
    }

    [Fact]
    public void AWorldWithoutASeedAnywhereIsUnknown()
    {
        var server = Server("level-name=world\nlevel-seed=123\n");
        Put(server, "world/level.dat", Gzip(Compound("", Compound("Data", Text("LevelName", "world")))));

        // Not 123: the world exists, so it was generated with some seed already, and level-seed
        // may have been changed since.
        Assert.Equal(SeedInfo.None, WorldSeed.Read(server));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4 })]                // not gzip
    public void ADamagedFileIsNotFoundAndNothingThrows(byte[] bytes)
    {
        var server = Server();
        Put(server, "world/level.dat", bytes);

        Assert.Equal(SeedInfo.None, WorldSeed.Read(server));
    }

    [Fact]
    public void ATruncatedFileIsNotFoundAndNothingThrows()
    {
        var whole = Gzip(Compound("", Compound("Data", Long("RandomSeed", 42))));
        using var raw = new MemoryStream();
        using (var gz = new GZipStream(new MemoryStream(whole), CompressionMode.Decompress)) gz.CopyTo(raw);
        var cut = raw.ToArray()[..^6];   // the seed's last bytes and the closing tags are gone

        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(cut);
        ms.Position = 0;

        Assert.Null(NbtReader.ReadLong(ms, "Data", "RandomSeed"));
    }

    [Fact]
    public void ALengthTheFileCannotHoldIsRefused()
    {
        // A byte array claiming two billion bytes, before the seed: a damaged file must not be able
        // to make the reader try to walk that.
        var huge = new byte[] { 0x7F, 0xFF, 0xFF, 0xFF };
        var data = Compound("Data", new Tag(7, "junk", huge), Long("RandomSeed", 1));
        var path = Path.Combine(_root, "huge.dat");
        File.WriteAllBytes(path, Gzip(Compound("", data)));

        Assert.Null(NbtReader.ReadLong(path, "Data", "RandomSeed"));
    }

    // --- Before the world exists, and when the files cannot say ---

    [Fact]
    public void WithNoWorldYetTheAskedSeedIsPending()
    {
        Assert.Equal(new SeedInfo(12345, SeedSource.Pending), WorldSeed.Read(Server("level-seed=12345\n")));
        Assert.Equal(new SeedInfo(99162322, SeedSource.Pending), WorldSeed.Read(Server("level-seed=hello\n")));
        Assert.Equal(SeedInfo.None, WorldSeed.Read(Server("level-seed=\n")));
        Assert.Equal(SeedInfo.None, WorldSeed.Read(Server(properties: null)));
    }

    [Fact]
    public void WhatTheServerSaidIsTheFallback()
    {
        var server = Server();
        Put(server, "world/level.dat", Gzip(Compound("", Compound("Data", Text("LevelName", "world")))));

        Assert.Equal(new SeedInfo(-5, SeedSource.Console), WorldSeed.Read(server, lastKnown: -5));
    }

    [Fact]
    public void TheWorldOutranksWhatTheServerOnceSaid()
    {
        var server = Server();
        Put(server, "world/level.dat", Gzip(Compound("", Compound("Data", Long("RandomSeed", 42)))));

        Assert.Equal(new SeedInfo(42, SeedSource.World), WorldSeed.Read(server, lastKnown: -5));
    }

    [Theory]
    [InlineData("[21:10:03] [Server thread/INFO]: Seed: [-8661663486445701832]", -8661663486445701832)]
    [InlineData("[21:10:03 INFO]: Seed: [42]", 42L)]
    public void TheAnswerToSeedIsRecognised(string line, long seed)
    {
        Assert.Equal(seed, WorldSeed.FromConsoleLine(line));
    }

    [Theory]
    [InlineData("[21:10:03] [Server thread/INFO]: <Bob> Seed: [42]")]   // typed in chat
    [InlineData("[21:10:03] [Server thread/INFO]: Seed: [not a number]")]
    public void OnlyTheServersOwnAnswerCounts(string line)
    {
        Assert.Null(WorldSeed.FromConsoleLine(line));
    }

    // --- A typed seed, the way Minecraft turns it into a number ---

    [Theory]
    [InlineData("12345", 12345L)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("  777  ", 777L)]
    [InlineData("hello", 99162322L)]                 // Java: "hello".hashCode()
    [InlineData("Glacier", 1772835215L)]
    [InlineData("99999999999999999999", 1260560192L)] // too long for a long: hashed as text, like Java
    public void ASeedBecomesTheNumberMinecraftWouldUse(string typed, long expected)
    {
        Assert.Equal(expected, WorldSeed.ToNumeric(typed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NothingTypedMeansRandom(string? typed)
    {
        Assert.Null(WorldSeed.ToNumeric(typed));
    }

    // --- Writing it when the server is created ---

    [Fact]
    public void TheSeedIsWrittenForTheFirstStart()
    {
        var folder = Server(properties: null);
        new ServerCreationService().WriteInitialProperties(folder, 25565, "motd", "12345");

        Assert.Equal("12345", new ServerPropertiesService().Read(Path.Combine(folder, "server.properties"))["level-seed"]);
    }

    [Fact]
    public void NoSeedWritesNoKey()
    {
        var folder = Server(properties: null);
        new ServerCreationService().WriteInitialProperties(folder, 25565, "motd", "  ");

        Assert.False(new ServerPropertiesService().Read(Path.Combine(folder, "server.properties")).ContainsKey("level-seed"));
    }

    [Fact]
    public void APastedSeedCannotAddAKeyOfItsOwn()
    {
        var folder = Server(properties: null);
        new ServerCreationService().WriteInitialProperties(folder, 25565, "motd", "abc\nonline-mode=false");

        var props = new ServerPropertiesService().Read(Path.Combine(folder, "server.properties"));
        Assert.False(props.ContainsKey("online-mode"));
    }

    private const string Backslash = @"\";

    [Theory]
    [InlineData(@"a\b", @"a\\b")]          // the properties format's escape character
    [InlineData("semilla ñ", "semilla " + Backslash + "u00f1")]
    [InlineData("plain 123", "plain 123")]
    public void TheSeedIsEscapedSoTheServerReadsWhatWasTyped(string typed, string written)
    {
        Assert.Equal(written, WorldSeed.EscapeForProperties(typed));
        Assert.Equal(typed, WorldSeed.Unescape(written));
    }

    [Fact]
    public void AFolderWithAWorldKeepsItsFileAndTheSeedIsNotWritten()
    {
        var folder = Server("level-name=world\nserver-port=25570\n");
        Put(folder, "world/level.dat", Gzip(Compound("", Compound("Data", Long("RandomSeed", 1)))));

        new ServerCreationService().WriteInitialProperties(folder, 25565, "motd", "12345");

        var props = new ServerPropertiesService().Read(Path.Combine(folder, "server.properties"));
        Assert.False(props.ContainsKey("level-seed"));
        Assert.Equal("25570", props["server-port"]);
    }

    [Fact]
    public void AFolderWithAFileButNoWorldGetsTheSeed()
    {
        var folder = Server("level-name=world\nserver-port=25570\n");

        new ServerCreationService().WriteInitialProperties(folder, 25565, "motd", "12345");

        var props = new ServerPropertiesService().Read(Path.Combine(folder, "server.properties"));
        Assert.Equal("12345", props["level-seed"]);
        Assert.Equal("25570", props["server-port"]);
    }

    // --- The map ---

    [Theory]
    [InlineData("26.3", "java_26_3")]
    [InlineData("26.3.1", "java_26_3")]
    [InlineData("26.2", "java_26_2")]
    [InlineData("1.21.11", "java_1_21_9")]
    [InlineData("1.21.1", "java_1_21")]
    [InlineData("1.21", "java_1_21")]
    [InlineData("1.20.4", "java_1_20")]
    [InlineData("1.19.4", "java_1_19_3")]
    [InlineData("1.8.9", "java_1_8")]
    public void EachVersionOpensTheMapThatCoversIt(string version, string platform)
    {
        Assert.Equal(platform, SeedMapLink.PlatformFor(version));
    }

    [Theory]
    [InlineData("26.4")]        // newer than the table: Chunkbase's own newest is the better guess
    [InlineData("27.1")]
    [InlineData("25w14a")]      // a snapshot
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1.6.4")]       // older than anything Chunkbase has
    public void AVersionTheTableDoesNotKnowIsLeftToTheSite(string? version)
    {
        Assert.Null(SeedMapLink.PlatformFor(version));
        Assert.DoesNotContain("platform=", SeedMapLink.For(1, version));
    }

    [Fact]
    public void TheLinkCarriesTheSeed()
    {
        Assert.Equal("https://www.chunkbase.com/apps/seed-map#seed=-8661663486445701832&platform=java_26_2&dimension=overworld",
            SeedMapLink.For(-8661663486445701832, "26.2"));
        Assert.True(BrowserLauncher.IsWebUrl(SeedMapLink.For(1, "26.2")));
    }
}
