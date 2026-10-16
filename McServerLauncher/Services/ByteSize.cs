using System.Globalization;

namespace McServerLauncher.Services;

/// <summary>A size on disk as a person reads it: "812 KB", "3.4 MB", "1.2 GB".</summary>
/// <remarks>
/// Three view models each had their own, with different thresholds and different units (one had no
/// GB, one no bytes, one wrote "3.0 MB"). Powers of 1024, the way the file manager shows them. Line
/// speeds are another matter — they are decimal, like the Mb/s beside them — and keep their own.
/// </remarks>
public static class ByteSize
{
    public static string Format(long bytes)
    {
        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
        return bytes switch
        {
            < 1024 => bytes.ToString(CultureInfo.CurrentCulture) + " B",
            < 1024 * 1024 => (bytes / Kb).ToString("0.#", CultureInfo.CurrentCulture) + " KB",
            < 1024L * 1024 * 1024 => (bytes / Mb).ToString("0.#", CultureInfo.CurrentCulture) + " MB",
            _ => (bytes / Gb).ToString("0.#", CultureInfo.CurrentCulture) + " GB",
        };
    }
}
