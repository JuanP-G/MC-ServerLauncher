using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace McServerLauncher.ViewModels;

/// <summary>
/// Binds an <c>int</c> setting to a <see cref="Avalonia.Controls.NumericUpDown"/>, whose value is a
/// <c>decimal?</c> that is null while the box is empty.
/// </summary>
/// <remarks>
/// An empty box writes nothing, rather than failing to write a null into an <c>int</c> and showing the
/// exception under the box. <see cref="Views.WholeNumberBoxes"/> puts the number back once the box is
/// left empty.
/// </remarks>
public class WholeNumberConverter : IValueConverter
{
    public static readonly WholeNumberConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? null : System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is decimal number
            ? System.Convert.ChangeType(ToWhole(number),
                Nullable.GetUnderlyingType(targetType) ?? targetType, CultureInfo.InvariantCulture)
            : BindingOperations.DoNothing;

    /// <summary>A box's value as the whole number it stands for, rounded the way the box shows it.</summary>
    /// <remarks>
    /// The one place a box's <c>decimal</c> becomes an <c>int</c>. Some code used to cast instead,
    /// which truncates: a box showing 3 (its format rounds 2.5) wrote 2.
    /// </remarks>
    public static int ToWhole(decimal value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
