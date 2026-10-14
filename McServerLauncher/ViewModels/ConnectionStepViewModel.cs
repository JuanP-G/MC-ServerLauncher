using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;

namespace McServerLauncher.ViewModels;

/// <summary>How one stretch of the connection did.</summary>
public enum StepState { Waiting, Running, Good, Fair, Bad, Skipped }

/// <summary>One stretch of the way a player's packets travel, and how it did.</summary>
public partial class ConnectionStepViewModel : ObservableObject
{
    public ConnectionStepViewModel(string title) => _title = title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon), nameof(IsGood), nameof(IsFair), nameof(IsBad), nameof(IsRunning), nameof(IsIdle))]
    private StepState _state;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _detail = string.Empty;

    /// <summary>The number beside it: "3 ms", "↑ 42 Mb/s".</summary>
    [ObservableProperty]
    private string _badge = string.Empty;

    public Symbol Icon => State switch
    {
        StepState.Good => Symbol.Checkmark,
        StepState.Fair => Symbol.Warning,
        StepState.Bad => Symbol.ErrorCircle,
        StepState.Running => Symbol.ArrowSync,
        StepState.Skipped => Symbol.Subtract,
        _ => Symbol.Circle,
    };

    public bool IsGood => State == StepState.Good;
    public bool IsFair => State == StepState.Fair;
    public bool IsBad => State == StepState.Bad;
    public bool IsRunning => State == StepState.Running;
    public bool IsIdle => State is StepState.Waiting or StepState.Skipped;

    internal void Set(StepState state, string title, string detail, string badge = "")
    {
        State = state;
        Title = title;
        Detail = detail;
        Badge = badge;
    }
}

/// <summary>The verdict's colour, from its state.</summary>
public static class StepStateConverters
{
    public static readonly Avalonia.Data.Converters.FuncValueConverter<StepState, bool> IsFair = new(s => s == StepState.Fair);
    public static readonly Avalonia.Data.Converters.FuncValueConverter<StepState, bool> IsBad = new(s => s == StepState.Bad);
}
