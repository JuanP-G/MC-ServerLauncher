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

        // If the secret couldn't be encrypted, Save refused to persist it (never plaintext on disk):
        // it still works this session but will be asked for again next time.
        if (service.LastSaveCouldNotProtectKey)
            await MessageBox.ShowAsync(Localizer.Get("Msg_PlayitKeyNotProtected"), Localizer.Get("Pk_Title"), owner);
        if (dialog.AgentOverLimit)
            await MessageBox.ShowAsync(Localizer.Get("Msg_AgentOverLimit"), Localizer.Get("Pk_Title"), owner);

        return Credential(settings);
    }

    /// <summary>Clears the stored Playit connection (agent key + any legacy key).</summary>
    public static void Disconnect(AppSettings settings, AppSettingsService service)
    {
        settings.PlayitAgentSecretKey = null;
        settings.PlayitAgentId = null;
        settings.PlayitApiKey = null;
        service.Save(settings);
        PlayitApiService.SetAgentKey(null);
    }
}
