using Avalonia.Threading;
using McServerLauncher.Localization;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.Views;

namespace McServerLauncher.ViewModels;

/// <summary>The Discord presence: what is running, told to Discord every few seconds.</summary>
public partial class MainViewModel
{
    /// <summary>
    /// Four rounds every twenty seconds, under Discord's limit of five. Not slower: Pumpkin starts in
    /// under a second, and at fifteen seconds a short run could come and go without ever showing.
    /// </summary>
    private static readonly TimeSpan DiscordInterval = TimeSpan.FromSeconds(5);

    private readonly DiscordPresenceService _discord = new();
    private DispatcherTimer? _discordTimer;

    private void StartDiscordPresence()
    {
        if (!DiscordPresenceService.IsAvailable || _discordTimer is not null) return;
        _discordTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.UIThread)
        {
            Interval = DiscordInterval,
        };
        _discordTimer.Tick += (_, _) => _ = UpdateDiscordPresenceAsync();
        _discordTimer.Start();
        _ = UpdateDiscordPresenceAsync();
    }

    /// <summary>One round: also called when the switch in Settings moves.</summary>
    internal async Task UpdateDiscordPresenceAsync()
    {
        if (_discordTimer is null) return;
        try
        {
            if (!_appSettings.DiscordPresence)
            {
                await _discord.DisconnectAsync();
                Settings.SetDiscordFound(null);
                return;
            }

            var running = Servers.Select(s => s.DiscordSnapshot()).OfType<DiscordServer>().ToList();
            var found = await _discord.UpdateAsync(DiscordActivity.For(running));
            Settings.SetDiscordFound(found);

            // Once ever: it switched itself on, so it has to say so and say where to switch it off.
            if (found && !_appSettings.DiscordNoticeShown)
            {
                _appSettings.DiscordNoticeShown = true;
                _settings.Save(_appSettings);
                ToastService.Shared.Notify(Localizer.Get("Discord_Title"), "MC Server Launcher",
                    Localizer.Get("Discord_FirstNotice"), FluentIcons.Common.Icon.Chat,
                    NotificationLevel.Info, NotificationPreferences.Global);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("DiscordPresence", ex);
        }
    }

    private async Task StopDiscordPresenceAsync()
    {
        _discordTimer?.Stop();
        _discordTimer = null;
        await _discord.DisconnectAsync();
    }
}
