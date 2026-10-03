using System.Threading.Tasks;
using Avalonia.Controls;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Views;

/// <summary>
/// Shared "connect your Playit account" flow, used both from the per-server tunnel actions and from
/// the Settings dialog. Encapsulates the setup-code dialog, persisting the resulting agent secret
/// (encrypted) and wiring <see cref="PlayitApiService"/>, so the user never touches keys or files.
/// </summary>
public static class PlayitConnection
{
    /// <summary>The stored credential (per-user agent key, or a legacy write key), or null if none.</summary>
    public static string? Credential(AppSettings s) =>
        !string.IsNullOrWhiteSpace(s.PlayitAgentSecretKey) ? s.PlayitAgentSecretKey
        : !string.IsNullOrWhiteSpace(s.PlayitApiKey) ? s.PlayitApiKey
        : null;

    /// <summary>Where a way of reaching the account came from.</summary>
    public enum SourceKind
    {
        /// <summary>The agent this app downloaded and connected through the setup code.</summary>
        AppAgent,

        /// <summary>The agent the user installed themselves, whose key is in playit.toml.</summary>
        InstalledAgent,

        /// <summary>A write key pasted in an older version.</summary>
        SavedKey,
    }

    /// <summary>One credential that might open the account, and where it came from.</summary>
    public sealed record Source(SourceKind Kind, string Key);

    /// <summary>
    /// Every credential this machine has for Playit, in the order to try them, without repeats.
    /// </summary>
    /// <remarks>
    /// The tunnels screen used to look only at what had been saved through the connect flow, so
    /// someone who had installed Playit's own agent — and had working tunnels, which the server
    /// cards found by reading <c>playit.toml</c> — saw "not connected" and an empty table. The
    /// installed agent's key is as good a way into the account as any, so it counts.
    /// </remarks>
    /// <param name="settings">What the connect flow saved.</param>
    /// <param name="installedKey">Reads the key from playit.toml; a parameter so a test need not touch the machine.</param>
    public static IReadOnlyList<Source> Sources(AppSettings settings, Func<string?>? installedKey = null)
    {
        var found = new List<Source>();

        void Add(SourceKind kind, string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (found.Any(s => s.Key == key)) return;   // the same key twice is one way in, not two
            found.Add(new Source(kind, key));
        }

        Add(SourceKind.AppAgent, settings.PlayitAgentSecretKey);
        Add(SourceKind.InstalledAgent, (installedKey ?? (() => new PlayitApiService().ReadSecretKey()))());
        Add(SourceKind.SavedKey, settings.PlayitApiKey);
        return found;
    }

    /// <summary>True if the user has connected their Playit account (or has a legacy key).</summary>
    public static bool IsConnected(AppSettings s) => Credential(s) is not null;

    /// <summary>
    /// Returns the stored credential if already connected; otherwise runs the connect flow. Returns
    /// null if the user cancels.
    /// </summary>
    public static async Task<string?> EnsureAsync(Window owner, AppSettings settings, AppSettingsService service)
        => Credential(settings) ?? await ConnectAsync(owner, settings, service);

    /// <summary>
    /// Shows the setup-code dialog, stores the result (encrypted) and wires it. Returns the
    /// credential, or null if cancelled.
    /// </summary>
    public static async Task<string?> ConnectAsync(Window owner, AppSettings settings, AppSettingsService service)
    {
        var dialog = new PlayitApiKeyDialog();
        if (!await dialog.ShowDialog<bool>(owner))
            return null;

        if (dialog.IsSetupResult)
        {
            settings.PlayitAgentSecretKey = dialog.AgentSecretKey;
            settings.PlayitAgentId = dialog.AgentId;
        }
        else
        {
            settings.PlayitApiKey = dialog.LegacyWriteKey;
        }
        service.Save(settings);
        PlayitApiService.SetAgentKey(settings.PlayitAgentSecretKey); // no-op for the legacy path

        // Start the embedded Playit agent so the user's tunnels actually forward traffic (the app
        // downloads and runs it; nothing to install). Only for the partner agent key, not a legacy
        // write key (that model relies on the user's own installed agent).
        if (!string.IsNullOrWhiteSpace(settings.PlayitAgentSecretKey))
            _ = PlayitAgentRunner.Shared.StartAsync(settings.PlayitAgentSecretKey);

        // If the secret couldn't be encrypted, Save refused to persist it (never plaintext on disk):
        // it still works this session but will be asked for again next time.
        if (service.LastSaveCouldNotProtectKey)
            await MessageBox.ShowAsync(Localizer.Get("Msg_PlayitKeyNotProtected"), Localizer.Get("Pk_Title"), owner);
        if (dialog.AgentOverLimit)
            await MessageBox.ShowAsync(Localizer.Get("Msg_AgentOverLimit"), Localizer.Get("Pk_Title"), owner);

        return Credential(settings);
    }

    /// <summary>Clears the stored Playit connection (agent key + any legacy key) and stops the agent.</summary>
    public static void Disconnect(AppSettings settings, AppSettingsService service)
    {
        settings.PlayitAgentSecretKey = null;
        settings.PlayitAgentId = null;
        settings.PlayitApiKey = null;
        service.Save(settings);
        PlayitApiService.SetAgentKey(null);
        PlayitAgentRunner.Shared.Stop();
    }
}
