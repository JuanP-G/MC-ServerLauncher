using Avalonia.Controls;

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
internal sealed class PropertyBindings(ServerSettingsDraft draft)
{
    private readonly List<Action> _loads = new();
    private bool _loading;

    public void Toggle(ToggleSwitch toggle, string key, bool fallback)
    {
        var raw = draft.FileValue(key);
        draft.Track(key, Text(raw is null ? fallback : raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)));

        toggle.IsCheckedChanged += (_, _) =>
        {
            if (!_loading) draft.Set(key, Text(toggle.IsChecked == true));
        };
        _loads.Add(() => toggle.IsChecked = draft.Get(key) == "true");
    }

    public void Number(NumericUpDown box, string key, int fallback)
    {
        var raw = draft.FileValue(key);
        draft.Track(key, (int.TryParse(raw, out var n) ? n : fallback).ToString());

        box.ValueChanged += (_, _) =>
        {
            if (!_loading) draft.Set(key, ((int)(box.Value ?? fallback)).ToString());
        };
        _loads.Add(() => box.Value = int.TryParse(draft.Get(key), out var v) ? v : fallback);
    }

    /// <summary>A drop-down whose items carry the value to write in their <c>Tag</c>.</summary>
    /// <remarks>
    /// A value the list does not know is kept as it is until something is picked: the first item is
    /// shown, but nothing is written for a choice the user did not make.
    /// </remarks>
    public void Choice(ComboBox combo, string key, string fallback)
    {
        var raw = draft.FileValue(key)?.Trim() ?? fallback;
        var known = Items(combo).FirstOrDefault(i => string.Equals(i.Tag as string, raw, StringComparison.OrdinalIgnoreCase));
        draft.Track(key, known?.Tag as string ?? raw);

        combo.SelectionChanged += (_, _) =>
        {
            if (!_loading && combo.SelectedItem is ComboBoxItem { Tag: string tag }) draft.Set(key, tag);
        };
        _loads.Add(() =>
        {
            var now = draft.Get(key);
            combo.SelectedItem = Items(combo).FirstOrDefault(i => string.Equals(i.Tag as string, now, StringComparison.OrdinalIgnoreCase))
                                 ?? Items(combo).FirstOrDefault();
        });
    }

    /// <summary>Shows every control what the draft holds, without that counting as a change.</summary>
    public void Load()
    {
        _loading = true;
        try { foreach (var load in _loads) load(); }
        finally { _loading = false; }
    }

    private static IEnumerable<ComboBoxItem> Items(ComboBox combo) => combo.Items.OfType<ComboBoxItem>();

    private static string Text(bool value) => value ? "true" : "false";
}
