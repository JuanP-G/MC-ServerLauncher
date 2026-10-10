namespace McServerLauncher.Views.ServerSettings;

/// <summary>One page of a server's settings, as the page around it sees it.</summary>
/// <remarks>
/// Deliberately small. What a page has changed is not its own to say: the draft knows which fields
/// and keys each page owns (<see cref="ServerSettingsDraft.Owned"/>), so a page that has never been
/// opened can still be asked, and nothing has to be kept in step by hand.
/// </remarks>
internal interface IServerSettingsSection
{
    /// <summary>Shows the controls what the draft holds: after Discard, and after a save.</summary>
    void Reload();

    /// <summary>Says what is wrong under the controls, or clears it with null.</summary>
    void ShowError(string? message);
}
