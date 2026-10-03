using System.Security.Cryptography;
using System.Text.RegularExpressions;
using McServerLauncher.Services;

namespace McServerLauncher.Tests;

/// <summary>
/// The SHA-1 the app sends to Modrinth to ask what a jar is.
/// </summary>
/// <remarks>
/// <para>
/// Modrinth matches hashes literally and expects lowercase hex. Everything this app computes comes
/// out of <see cref="Convert.ToHexString"/>, which produces upper case and has no other setting —
/// so both hash endpoints were answering <c>{}</c> with a perfectly healthy <c>200 OK</c>.
/// </para>
/// <para>
/// The damage was entirely invisible, which is why it lasted: "no updates available" and "nothing
/// is missing" are what a well-kept server looks like. Checked here with a real jar, hashed the way
/// the app hashes it, because the bug lived in the gap between two functions that were each right.
/// </para>
/// </remarks>
public class StoreHashTests : IDisposable
{
    private readonly string _file = Path.Combine(
        Path.GetTempPath(), "mcl-hash-" + Guid.NewGuid().ToString("N") + ".jar");

    public StoreHashTests() => File.WriteAllText(_file, "cualquier cosa");

    [Fact]
    public async Task WhatTheAppHashesIsSentInLowerCase()
    {
        // The whole bug in three lines, end to end: hash a file the way the Mods tab does, hand it
        // to the store the way the Mods tab does, and look at what would go out on the wire.
        var computed = await new FileHashCache().Sha1Async(_file);

        Assert.Equal(computed.ToLowerInvariant(), Assert.Single(ModrinthService.ApiHashes(new[] { computed })));
    }

    [Fact]
    public void TheHashesTheAppComputesAreUpperCase()
    {
        // Not a complaint, a record. This is why the normalisation has to exist and cannot be
        // dropped as redundant later: Convert.ToHexString has no lowercase option.
        var hex = Convert.ToHexString(SHA1.HashData("cualquier cosa"u8.ToArray()));

        Assert.Equal(hex.ToUpperInvariant(), hex);
        Assert.NotEqual(hex.ToLowerInvariant(), hex);
    }

    [Fact]
    public void TheSameJarIsNotAskedForTwiceUnderTwoSpellings()
    {
        var hashes = ModrinthService.ApiHashes(new[]
        {
            "86D6618F772320BB5E66B4696396A450D5CB845E",
            "86d6618f772320bb5e66b4696396a450d5cb845e"
        });

        Assert.Single(hashes);
    }

    [Fact]
    public void EmptiesAreDroppedRatherThanSentAsBlanks()
    {
        Assert.Empty(ModrinthService.ApiHashes(new[] { "", null! }));
    }

    [Fact]
    public void EveryHashBodySentToTheStoreIsBuiltFromApiHashes()
    {
        // There were two endpoints taking hashes and both got this wrong in the same way, because
        // the second was written by copying the first. A third would have been too. Checked against
        // the file, since the failure is a 200 OK with an empty body — no exception, no log line,
        // nothing on screen except the good news that there is nothing to do.
        var source = File.ReadAllText(Path.Combine(
            LocalizationTests.RepoRoot(), "McServerLauncher", "Services", "ModrinthService.cs"));

        var bodies = Regex.Matches(source, @"\[""hashes""\] = new JsonArray\((\w+)\.Select")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal(2, bodies.Count);
        foreach (var variable in bodies)
            Assert.Contains($"var {variable} = ApiHashes(", source, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { File.Delete(_file); } catch { /* best-effort */ }
    }
}
