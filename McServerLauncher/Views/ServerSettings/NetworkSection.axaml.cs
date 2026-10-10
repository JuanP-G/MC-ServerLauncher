using Avalonia.Controls;
using McServerLauncher.Models;
using McServerLauncher.Services;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>Who can reach the server: its port, the Bedrock port, the account checks and the tunnel.</summary>
public partial class NetworkSection : UserControl, IServerSettingsSection
{
    private readonly ServerSettingsDraft _draft;
    private readonly PropertyBindings _bindings;
    private bool _loading;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public NetworkSection() : this(new ServerSettingsDraft(new ServerConfig())) { }

    public NetworkSection(ServerSettingsDraft draft)
    {
        InitializeComponent();
        _draft = draft;
        DataContext = draft.Config;

        _bindings = new PropertyBindings(draft);
        _bindings.Number(PortBox, "server-port", 25565);
        _bindings.Toggle(OnlineModeToggle, "online-mode", true);
        _bindings.Toggle(WhitelistToggle, "white-list", false);

        // Only with crossplay set up: without Geyser there is no Bedrock port to change.
        BedrockPortCard.IsVisible = CrossplayService.EffectiveBedrockPort(draft.Live) is not null;
        BedrockPortBox.ValueChanged += (_, _) => OnBedrockPortChanged();

        Reload();
    }

    /// <inheritdoc />
    public void Reload()
    {
        _bindings.Load();
        _loading = true;
        BedrockPortBox.Value = CrossplayService.EffectiveBedrockPort(_draft.Config) ?? CrossplayService.DefaultBedrockPort;
        _loading = false;
    }

    /// <inheritdoc />
    public void ShowError(string? message)
    {
        BedrockPortWarning.Text = message;
        BedrockPortWarning.IsVisible = message is not null;
    }

    /// <remarks>
    /// The config keeps 0 for "Geyser's default". Typing the port it already has puts that 0 back
    /// instead of writing the number out, so going there and back is not a change.
    /// </remarks>
    private void OnBedrockPortChanged()
    {
        if (_loading) return;
        ShowError(null);

        var wanted = (int)(BedrockPortBox.Value ?? CrossplayService.DefaultBedrockPort);
        var saved = (int)_draft.Saved(nameof(ServerConfig.BedrockPort))!;
        var savedEffective = saved > 0 ? saved : CrossplayService.DefaultBedrockPort;
        _draft.Config.BedrockPort = wanted == savedEffective ? saved : wanted;
    }
}
