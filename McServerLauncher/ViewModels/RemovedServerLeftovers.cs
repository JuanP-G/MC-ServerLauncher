using System;
using System.IO;
using System.Threading.Tasks;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>
/// What a removed server leaves behind that the user asked to clear: its Playit tunnels and its
/// folder. Each says so on screen when it could not finish, because by then the server is gone from
/// the list and nothing else would tell the user an orphan is left.
/// </summary>
internal static class RemovedServerLeftovers
{
    /// <summary>
    /// Deletes the Playit tunnels a removed server had, and says so when one could not be found.
    /// </summary>
    /// <remarks>
    /// Not "&amp;&amp; port.HasValue". The Java port comes from server.properties, which can be
    /// unreadable or already gone, and hanging the whole block on it took the Bedrock tunnel
    /// down with it — even though that one is identified by Config.BedrockPort, which lives in
    /// servers.json and needs no file on disk. The result was an orphan UDP tunnel that nothing
    /// would ever clean up, sitting on a port the next server would be handed as free.
    /// </remarks>
    /// <param name="port">The Java port, if server.properties could still be read.</param>
    /// <param name="bedrockPort">The crossplay port, if the server had one.</param>
    /// <param name="ensureAgent">Gives the Playit key, asking the user for one if need be; null when none.</param>
    public static async Task DeleteTunnelsAsync(int? port, int? bedrockPort, Func<Task<string?>> ensureAgent)
    {
        var key = await ensureAgent();
        // The user didn't provide a key; the tunnel is not deleted.
        if (key is null) return;

        try
        {
            var api = new PlayitApiService();
            // Java is TCP, Bedrock is UDP. Naming the protocol is what keeps this from
            // deleting somebody else's tunnel that happens to share the port number.
            bool? javaDeleted = null;
            if (port.HasValue)
                javaDeleted = await api.DeleteTunnelForPortAsync(key, port.Value, udp: false);

            if (PlayitApiService.ShouldDeleteBedrockTunnel(userAskedToDelete: true, bedrockPort))
                await api.DeleteTunnelForPortAsync(key, bedrockPort!.Value, udp: true);

            if (!port.HasValue)
                await Dialogs.Current.ShowAsync(
                    Localizer.Get("Msg_JavaTunnelPortUnknown"),
                    Localizer.Get("Title_DeleteTunnel"));
            else if (javaDeleted == false)
                await Dialogs.Current.ShowAsync(
                    string.Format(Localizer.Get("Msg_NoTunnelForPort"), port),
                    Localizer.Get("Title_DeleteTunnel"));
        }
        catch (Exception ex)
        {
            await Dialogs.Current.ShowAsync(
                string.Format(Localizer.Get("Msg_TunnelDeleteError"), ErrorMessages.Describe(ex)),
                Localizer.Get("Title_DeleteTunnel"));
        }
    }

    /// <summary>Deletes a removed server's folder, telling the user if it could not be.</summary>
    public static async Task DeleteFolderAsync(string folder)
    {
        if (!Directory.Exists(folder)) return;
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            await Dialogs.Current.ShowAsync(
                string.Format(Localizer.Get("Msg_FilesDeleteError"), ErrorMessages.Describe(ex)),
                Localizer.Get("Title_DeleteFiles"));
        }
    }
}
