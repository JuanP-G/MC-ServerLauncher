using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>
/// Which installer a player needs to run so their Minecraft can load the pack, and how to check it.
/// </summary>
/// <remarks>
/// <para>
/// The app already downloads these installers for the server side; this is the same three,
/// resolved for a client and handed to the pack's script rather than run here. Nothing in this
/// file installs anything: it works out a URL, an official hash and the arguments, all at export
/// time, so the script the player runs has no version resolution to do and no choices to make.
/// </para>
/// <para>
/// The hash is the point. The script downloads a jar over the network and runs it with Java on
/// somebody else's machine, which is the single most consequential thing the whole pack does.
/// Every hash here comes from the loader's own maven repository, fetched next to the file it
/// describes, and the script refuses to execute anything that does not match. If the hash cannot be
/// had, this returns null and the pack ships without the offer — the script then only warns, which
/// is what it did before any of this existed.
/// </para>
/// </remarks>
internal static class ClientLoaderInstall
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Everything the script needs to fetch, verify and run one installer.</summary>
    /// <param name="Url">The installer jar, on the loader's own maven.</param>
    /// <param name="Sha">Its hash, as published beside it.</param>
    /// <param name="Bits">160 or 256 — which SHA the loader publishes.</param>
    /// <param name="Arguments">
    /// What to pass java before the Minecraft folder, which the script appends last. Both installer
    /// families take the directory as the final argument, which is what lets this be one string.
    /// </param>
    internal sealed record Plan(string Url, string Sha, int Bits, string Arguments);

    /// <summary>Works out the installer for a server's loader, or null if it cannot be had.</summary>
    /// <remarks>
    /// Null on any failure at all, deliberately. Every caller's fallback is to ship a pack that
    /// tells the player what to install and where to get it — which is a worse pack, not a broken
    /// one — and an export that failed because Modrinth or a maven was slow would be the wrong
    /// trade entirely.
    /// </remarks>
    internal static async Task<Plan?> ResolveAsync(
        ServerType type, string gameVersion, string? loaderVersion, CancellationToken ct)
    {
        try
        {
            return type switch
            {
                ServerType.Fabric => await FabricAsync(gameVersion, loaderVersion, ct),
                ServerType.Forge => await ForgeAsync(gameVersion, loaderVersion, ct),
                ServerType.NeoForge => await NeoForgeAsync(loaderVersion, ct),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    // --- Fabric ---
    //
    // The only one of the three with an installer CLI written for this: "client" installs into a
    // directory and writes a launcher profile, so the player has something to pick afterwards.

    private static async Task<Plan?> FabricAsync(string gameVersion, string? loaderVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gameVersion)) return null;

        var installers = await Http.GetStringAsync("https://meta.fabricmc.net/v2/versions/installer", ct);
        using var doc = JsonDocument.Parse(installers);

        var newest = doc.RootElement.EnumerateArray()
            .FirstOrDefault(e => e.TryGetProperty("stable", out var stable) && stable.GetBoolean());

        if (newest.ValueKind != JsonValueKind.Object ||
            !newest.TryGetProperty("url", out var urlProperty) ||
            urlProperty.GetString() is not { Length: > 0 } url)
            return null;

        var loader = string.IsNullOrWhiteSpace(loaderVersion)
            ? await new ModLoaderService().GetLatestFabricLoaderVersionAsync(ct)
            : loaderVersion!;

        if (await HashAsync(url + ".sha1", ct) is not { } sha) return null;

        return new Plan(url, sha, 160, $"client -mcversion {gameVersion} -loader {loader} -dir");
    }

    // --- Forge and NeoForge ---
    //
    // Both installers are built around a window, but both take --installClient, which does the
    // whole thing headless. Older Forge builds are the shakiest part of this; a failure there is
    // reported by the script and the mods are copied anyway.

    private static async Task<Plan?> ForgeAsync(string gameVersion, string? loaderVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gameVersion)) return null;

        var forge = string.IsNullOrWhiteSpace(loaderVersion)
            ? await new ModLoaderService().GetRecommendedForgeVersionAsync(gameVersion, ct)
            : loaderVersion;

        if (string.IsNullOrWhiteSpace(forge)) return null;

        var full = $"{gameVersion}-{forge}";
        var url = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{full}/forge-{full}-installer.jar";

        return await HashAsync(url + ".sha1", ct) is { } sha
            ? new Plan(url, sha, 160, "--installClient")
            : null;
    }

    private static async Task<Plan?> NeoForgeAsync(string? loaderVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(loaderVersion)) return null;

        var url = "https://maven.neoforged.net/releases/net/neoforged/neoforge/" +
                  $"{loaderVersion}/neoforge-{loaderVersion}-installer.jar";

        return await HashAsync(url + ".sha256", ct) is { } sha
            ? new Plan(url, sha, 256, "--installClient")
            : null;
    }

    /// <summary>The hash published beside a jar, or null.</summary>
    /// <remarks>
    /// Maven checksum files are sometimes "&lt;hash&gt;  &lt;filename&gt;" and sometimes just the
    /// hash, so the first word is taken. Anything that is not plausible hex is treated as no answer
    /// at all — a malformed checksum must not become a comparison the script can never satisfy, and
    /// still less one it might satisfy by accident.
    /// </remarks>
    private static async Task<string?> HashAsync(string url, CancellationToken ct)
    {
        try
        {
            var text = (await Http.GetStringAsync(url, ct)).Trim();
            var first = text.Split(' ', '\t', '\n', '\r')[0].Trim().ToLowerInvariant();

            return first.Length is 40 or 64 && first.All(Uri.IsHexDigit) ? first : null;
        }
        catch
        {
            return null;
        }
    }
}
