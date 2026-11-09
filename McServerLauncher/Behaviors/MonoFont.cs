using Avalonia.Media;

namespace McServerLauncher.Behaviors;

/// <summary>The monospaced font stack, for what is built in code rather than in markup.</summary>
/// <remarks>
/// The markup uses the <c>MonoFont</c> resource in <c>App.axaml</c>; this is the same text, because a
/// control created in C# (the code blocks of a mod's description) cannot always reach the resource.
/// <c>MonoFontTests</c> keeps the two equal and fails on any other copy of the stack, which had spread
/// to a dozen places in three spellings.
/// </remarks>
internal static class MonoFont
{
    /// <summary>Cascadia on a current Windows, Consolas on an older one, Menlo on macOS.</summary>
    public const string Stack = "Cascadia Mono, Consolas, Menlo, monospace";

    public static readonly FontFamily Family = new(Stack);
}
