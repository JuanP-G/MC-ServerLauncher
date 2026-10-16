using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;

namespace McServerLauncher.Views;

/// <summary>
/// What every <see cref="NumericUpDown"/> in the app is: a box for a whole number, typed in any
/// language, that never ends up empty or showing something it did not save.
/// </summary>
/// <remarks>
/// <para>
/// <b>Whole numbers only.</b> A box parses with <see cref="NumberStyles.Any"/> unless told otherwise,
/// which takes the culture's thousands separator — in Spanish, the dot. Typing <c>1.5</c> for the RAM
/// saved 15 GB. Every setting behind these boxes is an <c>int</c>, so they read
/// <see cref="NumberStyles.Integer"/>, and a decimal is simply not a number they accept.
/// </para>
/// <para>
/// <b>Never empty.</b> Deleting the number left <c>Value</c> null, and every setting behind these boxes
/// is a plain <c>int</c>: the binding could not write a null into it and showed
/// <c>InvalidCastException: Could not convert '(null)' to System.Int32</c> under the box, in red.
/// While somebody is typing, an empty box is fine — they are about to put a number in, and
/// <see cref="ViewModels.WholeNumberConverter"/> simply writes nothing until they do. Once the box
/// loses focus still empty, the number it had comes back.
/// </para>
/// <para>
/// <b>Never showing what it did not take.</b> Text that is not a whole number (<c>abc</c>, <c>1.5</c>)
/// never becomes the value, so once the box loses focus the value it holds is written back over it.
/// </para>
/// <para>
/// Installed once for every box in the app, including the ones a view fills by hand.
/// </para>
/// </remarks>
public static class WholeNumberBoxes
{
    private static readonly AttachedProperty<decimal?> LastValueProperty =
        AvaloniaProperty.RegisterAttached<NumericUpDown, NumericUpDown, decimal?>("LastValue");

    private static bool Installed;

    public static void Install()
    {
        if (Installed) return;
        Installed = true;

        // A style rather than a value on each box: it reaches the boxes a view builds in code too,
        // and a box that sets its own style in XAML still wins.
        Application.Current?.Styles.Add(new Style(x => x.OfType<NumericUpDown>())
        {
            Setters = { new Setter(NumericUpDown.ParsingNumberStyleProperty, NumberStyles.Integer) },
        });

        NumericUpDown.ValueProperty.Changed.AddClassHandler<NumericUpDown>((box, e) =>
        {
            if (e.NewValue is decimal value) box.SetValue(LastValueProperty, value);
        });

        InputElement.LostFocusEvent.AddClassHandler<NumericUpDown>((box, _) => Restore(box));
    }

    /// <summary>
    /// Gives an empty box its last number back (or its minimum if it never had one), and a box
    /// showing text it did not accept the number it holds.
    /// </summary>
    internal static void Restore(NumericUpDown box)
    {
        // A focus change inside the box (its text field to a spin button) is not leaving it.
        if (box.IsKeyboardFocusWithin) return;

        if (box.Value is not { } value)
        {
            box.Value = box.GetValue(LastValueProperty) ?? box.Minimum;
            return;
        }

        var shown = value.ToString(box.FormatString, box.NumberFormat);
        if (box.Text != shown) box.Text = shown;
    }
}
