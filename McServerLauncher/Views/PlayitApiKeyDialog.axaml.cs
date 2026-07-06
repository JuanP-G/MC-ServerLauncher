using Avalonia.Controls;
using Avalonia.Interactivity;
using McServerLauncher.Localization;
using McServerLauncher.Services;

namespace McServerLauncher.Views;

/// <summary>
/// Connects the user's Playit account. Primary path (partner configured): the third-party
/// setup-code flow — the user gets a setup code from playit.gg (opened only on a click), pastes it,
/// and the dialog exchanges it for a per-user self-managed agent (secret key + id) via
/// <see cref="PlayitPartnerService"/>. Fallback (partner NOT configured, e.g. dev builds): the
/// legacy behavior of pasting a Playit write key. Which one ran is signalled by
/// <see cref="IsSetupResult"/>.
/// </summary>
public partial class PlayitApiKeyDialog : Window
{
    private readonly bool _partnerConfigured = new PlayitPartnerService().IsConfigured;

    /// <summary>True when the setup-code flow ran (as opposed to the legacy write-key fallback).</summary>
    public bool IsSetupResult { get; private set; }

    /// <summary>The per-user agent secret key (valid when <see cref="IsSetupResult"/> and returned true).</summary>
    public string AgentSecretKey { get; private set; } = string.Empty;

    /// <summary>The agent id paired with <see cref="AgentSecretKey"/>.</summary>
    public string AgentId { get; private set; } = string.Empty;

    /// <summary>True if Playit reported the account is over its agent limit (still usable).</summary>
    public bool AgentOverLimit { get; private set; }

    /// <summary>The pasted write key (legacy fallback path; valid when NOT <see cref="IsSetupResult"/>).</summary>
    public string LegacyWriteKey { get; private set; } = string.Empty;

    public PlayitApiKeyDialog()
    {
        InitializeComponent();
    }

    private void OpenPlayit_Click(object? sender, RoutedEventArgs e)
    {
        // User-initiated open, per Playit's third-party rules (never auto-open the website).
        // Setup-code page when configured; the account page for the legacy write-key fallback.
        var url = _partnerConfigured ? "https://playit.gg/l/setup-third-party" : "https://playit.gg/account";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore if there's no browser available.
        }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var input = KeyBox.Text?.Trim() ?? string.Empty;
        if (input.Length == 0)
        {
            await MessageBox.ShowAsync(Localizer.Get("Msg_PasteSetupCode"), Localizer.Get("Pk_Title"), this);
            return;
        }

        // Legacy fallback: just capture the pasted write key (validated later by the API).
        if (!_partnerConfigured)
        {
            LegacyWriteKey = input;
            IsSetupResult = false;
            Close(true);
            return;
        }

        // Setup-code flow: exchange the code for a per-user agent.
        SetBusy(true);
        StatusText.IsVisible = false;
        try
        {
            var result = await new PlayitPartnerService().CreateAgentAsync(input);
            AgentSecretKey = result.AgentSecretKey;
            AgentId = result.AgentId;
            AgentOverLimit = result.AgentOverLimit;
            IsSetupResult = true;
            Close(true);
        }
        catch (Exception ex)
        {
            // Invalid/expired code, network, or not-configured: show it inline and let them retry.
            StatusText.Text = ex.Message;
            StatusText.IsVisible = true;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        KeyBox.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
