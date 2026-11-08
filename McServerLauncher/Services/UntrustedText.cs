namespace McServerLauncher.Services;

/// <summary>
/// What is done to a string that came from a file or a server before it is shown to anyone.
/// </summary>
internal static class UntrustedText
{
    /// <summary>
    /// <paramref name="text"/> on one line and no longer than <paramref name="max"/>; null when
    /// nothing is left. Text that came from a model or a download has no business carrying line
    /// breaks or control characters into a card.
    /// </summary>
    internal static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var flat = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        flat = string.Join(' ', flat.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return flat.Length == 0 ? null : flat.Length > max ? flat[..max] : flat;
    }
}
