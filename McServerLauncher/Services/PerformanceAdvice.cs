using McServerLauncher.Models;

namespace McServerLauncher.Services;

/// <summary>A change to the server's settings that lightens its load.</summary>
public enum AdviceKind
{
    /// <summary>Lower <c>simulation-distance</c>: how far from players the world keeps ticking.</summary>
    LowerSimulationDistance,

    /// <summary>Lower <c>view-distance</c>: how far from players chunks are sent and loaded.</summary>
    LowerViewDistance,

    /// <summary>Give Java more memory.</summary>
    RaiseMemory,

    /// <summary>Install spark, because without it (or <c>tick query</c>) the speed cannot be measured.</summary>
    InstallSpark
}

/// <summary>One suggestion.</summary>
/// <param name="Kind">What to change.</param>
/// <param name="From">The current value, when there is one.</param>
/// <param name="To">The suggested value: chunks for the distances, MB for memory.</param>
public sealed record Advice(AdviceKind Kind, long? From, long? To);

/// <summary>What the advice depends on.</summary>
/// <param name="Type">The server type.</param>
/// <param name="SimulationDistance">Its <c>simulation-distance</c>.</param>
/// <param name="ViewDistance">Its <c>view-distance</c>.</param>
/// <param name="CeilingMb">The heap ceiling it runs with, in MB.</param>
/// <param name="TotalRamMb">The machine's memory, 0 when unknown.</param>
/// <param name="AvailableRamMb">What the machine has free right now, 0 when unknown.</param>
/// <param name="CanMeasure">The server can be asked for its speed (spark, <c>tick query</c> or Paper's <c>tps</c>).</param>
/// <param name="SparkAvailable">spark is installed or comes with the server.</param>
public sealed record AdviceInputs(
    ServerType Type,
    int SimulationDistance,
    int ViewDistance,
    long CeilingMb,
    long TotalRamMb,
    long AvailableRamMb,
    bool CanMeasure,
    bool SparkAvailable);

/// <summary>
/// Turns a verdict into the settings changes worth offering. Pure.
/// </summary>
/// <remarks>
/// <para>
/// Distances first, because they cost nothing and are undone with a number: a simulation distance
/// above 6 goes to 6 (4 when saturated), a view distance above 10 goes to 10 (8 when saturated), and
/// the view distance never ends up below the simulation distance, which would make players see the
/// world frozen before it ends.
/// </para>
/// <para>
/// More memory only when memory is the cause, or the server ran out of it, and the machine can spare
/// it: 2 GB more, leaving 2 GB of the free memory untouched, never above three quarters of the
/// machine and never above 32 GB (past that Java loses its compressed pointers and gets slower).
/// </para>
/// </remarks>
public static class PerformanceAdvice
{
    public const long RaiseByMb = 2048;
    private const long ReserveMb = 2048;
    private const long MaxCeilingMb = 32 * 1024;

    /// <summary>The settings changes to offer, most useful first.</summary>
    public static IReadOnlyList<Advice> For(PerformanceStatus status, AdviceInputs input)
    {
        var advice = new List<Advice>();
        if (status.Level < PerformanceLevel.Tight) return advice;

        var saturated = status.Level == PerformanceLevel.Saturated;
        var memoryCause = status.Cause is PerformanceCause.Memory or PerformanceCause.OutOfMemory;

        var simulation = input.SimulationDistance > 6 ? saturated ? 4 : 6 : input.SimulationDistance;
        var view = input.ViewDistance > 10 ? saturated && input.ViewDistance > 8 ? 8 : 10 : input.ViewDistance;
        view = Math.Max(view, simulation);

        if (simulation < input.SimulationDistance)
            advice.Add(new Advice(AdviceKind.LowerSimulationDistance, input.SimulationDistance, simulation));
        if (view < input.ViewDistance)
            advice.Add(new Advice(AdviceKind.LowerViewDistance, input.ViewDistance, view));

        if (memoryCause && RaisedCeilingMb(input) is { } raised)
            advice.Add(new Advice(AdviceKind.RaiseMemory, input.CeilingMb, raised));

        if (!input.CanMeasure && !input.SparkAvailable)
            advice.Add(new Advice(AdviceKind.InstallSpark, null, null));

        // For a memory problem the memory comes first; for anything else the distances do.
        return memoryCause
            ? advice.OrderBy(a => a.Kind == AdviceKind.RaiseMemory ? 0 : 1).ToList()
            : advice;
    }

    /// <summary>The ceiling after the raise, or null when the machine cannot spare it.</summary>
    public static long? RaisedCeilingMb(AdviceInputs input)
    {
        if (input.TotalRamMb <= 0 || input.AvailableRamMb <= 0) return null;

        var raised = input.CeilingMb + RaiseByMb;
        var limit = Math.Min(MaxCeilingMb, input.TotalRamMb * 3 / 4);
        if (raised > limit) return null;
        return input.AvailableRamMb - RaiseByMb >= ReserveMb ? raised : null;
    }
}
