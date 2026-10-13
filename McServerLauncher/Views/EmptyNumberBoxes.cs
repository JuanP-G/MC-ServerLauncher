using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace McServerLauncher.Views;

/// <summary>
/// Puts back the last number of a <see cref="NumericUpDown"/> that was left empty.
/// </summary>
/// <remarks>
/// <para>
/// Deleting the number from one of these boxes leaves its <c>Value</c> null, and every setting behind
/// them is a plain <c>int</c>: the binding could not write a null into it and showed
/// <c>InvalidCastException: Could not convert '(null)' to System.Int32</c> under the box, in red.
/// </para>
/// <para>
/// While somebody is typing, an empty box is fine — they are about to put a number in, and
/// <see cref="ViewModels.WholeNumberConverter"/> simply writes nothing until they do. Once the box loses
/// focus still empty, the number it had comes back, so what is shown is again what is saved.
/// Installed once for every box in the app, including the ones a view fills by hand.
/// </para>
/// </remarks>
public static class EmptyNumberBoxes
{
    private static readonly AttachedProperty<decimal?> LastValueProperty =
        AvaloniaProperty.RegisterAttached<NumericUpDown, NumericUpDown, decimal?>("LastValue");

    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        NumericUpDown.ValueProperty.Changed.AddClassHandler<NumericUpDown>((box, e) =>
        {
            if (e.NewValue is decimal value) box.SetValue(LastValueProperty, value);
        });

        InputElement.LostFocusEvent.AddClassHandler<NumericUpDown>((box, _) => Restore(box));
    }

    /// <summary>Gives an empty box its last number back, or its minimum if it never had one.</summary>
    internal static void Restore(NumericUpDown box)
    {
        if (box.Value is not null) return;
        // A focus change inside the box (its text field to a spin button) is not leaving it.
        if (box.IsKeyboardFocusWithin) return;
        box.Value = box.GetValue(LastValueProperty) ?? box.Minimum;
    }
}
