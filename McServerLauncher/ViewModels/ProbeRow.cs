using System.Globalization;
using McServerLauncher.Services;

namespace McServerLauncher.ViewModels;

/// <summary>One row of the technical table: the round trips to one place.</summary>
public sealed record ProbeRow(string Name, string Min, string Average, string Max, string Jitter, string Loss)
{
    /// <summary>Milliseconds with a decimal under ten: on a LAN "0" says nothing, "0.3" does.</summary>
    internal static string Number(double ms) =>
        ms.ToString(ms < 10 ? "0.0" : "0", CultureInfo.CurrentCulture);

    internal static ProbeRow From(string name, LatencyStats stats)
    {
        static string Ms(double v) => Number(v);
        return stats.AnyReply
            ? new(name, Ms(stats.Min), Ms(stats.Average), Ms(stats.Max), Ms(stats.Jitter),
                (stats.Loss * 100).ToString("0", CultureInfo.CurrentCulture) + " %")
            : new(name, "—", "—", "—", "—", stats.Sent > 0 ? "100 %" : "—");
    }
}
