using Avalonia.Controls;
using McServerLauncher.Services;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Views.ServerSettings;

/// <summary>
/// Ties controls to <c>server.properties</c> keys in a <see cref="ServerSettingsDraft"/>: each one
/// shows what the file says (or the game's default when it says nothing), and writes back into the
/// draft as it changes.
/// </summary>
/// <remarks>
/// <para>
/// What the draft is told the key holds is the value as the control would write it, not the raw
/// text of the file — see <see cref="ServerSettingsDraft.Track"/>. Otherwise a hand-written
/// <c>pvp=TRUE</c> would count as an unsaved change the moment the page is opened.
/// </para>
/// <para>
/// The defaults are the game's own, the same ones the old <c>server.properties</c> dialog used.
/// </para>
/// </remarks>
internal sealed class PropertyBindings
{
    private readonly ServerSettingsDraft _draft;
    private readonly List<Action> _loads = new();
    private bool _loading;

    public PropertyBindings(ServerSettingsDraft draft) => _draft = draft;

    public void Toggle(ToggleSwitch toggle, string key, bool fallback)
    {
        var on = ServerPropertiesService.ParseBool(_draft.FileValue(key), fallback);
        _draft.Track(key, Text(on));

        toggle.IsCheckedChanged += (_, _) =>
        {
            if (!_loading) _draft.Set(key, Text(toggle.IsChecked == true));
        };
        _loads.Add(() => toggle.IsChecked = _draft.Get(key) == "true");
    }

    public void Number(NumericUpDown box, string key, int fallback)
    {
        var raw = _draft.FileValue(key);
        _draft.Track(key, (int.TryParse(raw, out var n) ? n : fallback).ToString());

        box.ValueChanged += (_, _) =>
        {
            // Empty while somebody types: nothing to write yet. WholeNumberBoxes puts the number
            // back if they leave it like that.
            if (!_loading && box.Value is { } value)
                _draft.Set(key, WholeNumberConverter.ToWhole(value).ToString());
        };
        _loads.Add(() => box.Value = int.TryParse(_draft.Get(key), out var v) ? v : fallback);
    }

    /// <summary>A drop-down whose items carry the value to write in their <c>Tag</c>.</summary>
    /// <remarks>
    /// <para>
    /// A value the list does not know is kept as it is until something is picked, and the list shows
    /// nothing picked: it used to show its first item, so a server on a value it did not know read as
    /// "Peaceful" or "Survival" whatever it really played as.
    /// </para>
    /// <para>
    /// With <paramref name="numberedInOrder"/>, a number is read as the position of an item, the way
    /// Minecraft still accepts <c>difficulty=3</c> and <c>gamemode=1</c> (the items are listed in the
    /// order of those ids). It shows as that item and counts as unchanged, so the file is not rewritten
    /// unless something else is picked.
    /// </para>
    /// </remarks>
    public void Choice(ComboBox combo, string key, string fallback, bool numberedInOrder = false)
    {
        var raw = _draft.FileValue(key)?.Trim() ?? fallback;
        var byPosition = numberedInOrder && int.TryParse(raw, out var id)
            ? Items(combo).ElementAtOrDefault(id)
            : null;
        var known = Find(combo, raw) ?? byPosition;
        _draft.Track(key, known?.Tag as string ?? raw);

        combo.SelectionChanged += (_, _) =>
        {
            if (!_loading && combo.SelectedItem is ComboBoxItem { Tag: string tag }) _draft.Set(key, tag);
        };
        _loads.Add(() => combo.SelectedItem = Find(combo, _draft.Get(key)));
    }

    /// <summary>Shows every control what the draft holds, without that counting as a change.</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            foreach (var load in _loads) load();
        }
        finally
        {
            _loading = false;
        }
    }

    private static IEnumerable<ComboBoxItem> Items(ComboBox combo) => combo.Items.OfType<ComboBoxItem>();

    private static ComboBoxItem? Find(ComboBox combo, string value) =>
        Items(combo).FirstOrDefault(item =>
            string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase));

    private static string Text(bool value) => value ? "true" : "false";
}
