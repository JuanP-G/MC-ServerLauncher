using Avalonia;
using Avalonia.Controls;
using McServerLauncher.Models;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>
/// Who can reach the server: its port, the Bedrock port, the account checks and the tunnel — and
/// the connection test that says where a slow game is coming from.
/// </summary>
public partial class NetworkSection : UserControl, IServerSettingsSection
{
    private readonly ServerSettingsDraft _draft;
    private readonly PropertyBindings _bindings;
    private readonly ConnectionTestViewModel? _test;
    private bool _loading;

    // Parameterless constructor for the Avalonia XAML loader / designer only.
    public NetworkSection() : this(new ServerSettingsDraft(new ServerConfig())) { }

    /// <param name="draft">What the page edits.</param>
    /// <param name="test">The server's connection test; the card is left out without one.</param>
    public NetworkSection(ServerSettingsDraft draft, ConnectionTestViewModel? test = null)
    {
        InitializeComponent();
        _draft = draft;
        _test = test;
        // The card first: it must never inherit the config, which it does not bind to.
        TestCard.DataContext = test;
        TestCard.IsVisible = test is not null;
        DataContext = draft.Config;

        _bindings = new PropertyBindings(draft);
        _bindings.Number(PortBox, "server-port", 25565);
        _bindings.Toggle(OnlineModeToggle, "online-mode", true);
        _bindings.Toggle(WhitelistToggle, "white-list", false);

        BedrockPortBox.ValueChanged += (_, _) => OnBedrockPortChanged();

        Reload();
    }

    /// <remarks>
    /// A test left running when the page goes away — another page, the settings closed — would go
    /// on downloading 25 MB for nobody to read.
    /// </remarks>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _test?.Cancel();
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    public void Reload()
    {
        // Only with crossplay set up: without Geyser there is no Bedrock port to change. Asked on
        // every reload, since crossplay can be set up while the page is open.
        BedrockPortCard.IsVisible = CrossplayService.EffectiveBedrockPort(_draft.Live) is not null;
        _bindings.Load();
        _loading = true;
        BedrockPortBox.Value = CrossplayService.EffectiveBedrockPort(_draft.Config)
            ?? CrossplayService.DefaultBedrockPort;
        _loading = false;
    }

    /// <inheritdoc />
    public void ShowError(string? message) => ShowError(message, null);

    /// <inheritdoc />
    /// <remarks>
    /// Each port's problem under its own box: the Bedrock card is hidden without crossplay, and a
    /// problem with the server's own port shown there would not be seen at all.
    /// </remarks>
    public void ShowError(string? message, string? field)
    {
        var onJavaPort = field == "server-port";
        PortWarning.Text = onJavaPort ? message : null;
        PortWarning.IsVisible = onJavaPort && message is not null;
        BedrockPortWarning.Text = onJavaPort ? null : message;
        BedrockPortWarning.IsVisible = !onJavaPort && message is not null;
    }

    /// <remarks>
    /// The config keeps 0 for "Geyser's default". Typing the port it already has puts that 0 back
    /// instead of writing the number out, so going there and back is not a change.
    /// </remarks>
    private void OnBedrockPortChanged()
    {
        if (_loading) return;
        ShowError(null);

        var wanted = WholeNumberConverter.ToWhole(
            BedrockPortBox.Value ?? CrossplayService.DefaultBedrockPort);
        var saved = (int)_draft.Saved(nameof(ServerConfig.BedrockPort))!;
        var savedEffective = saved > 0 ? saved : CrossplayService.DefaultBedrockPort;
        _draft.Config.BedrockPort = wanted == savedEffective ? saved : wanted;
    }
}
