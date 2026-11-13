namespace McServerLauncher.Services;

/// <summary>What a Minecraft Java player name looks like, in one place.</summary>
/// <remarks>
/// Letters, digits and underscore, one to sixteen of them. Five classes each kept their own regex for
/// this; they agreed, but the next one to change would have to find the other four. The rule matters
/// beyond looks: it is what stops a name from carrying a line break into a console command, and from
/// ever becoming part of a path.
/// </remarks>
public static class PlayerNames
{
    /// <summary>The rule as a regex fragment, for patterns that pick a name out of a line.</summary>
    public const string Pattern = "[A-Za-z0-9_]{1,16}";

    /// <summary>Whether <paramref name="name"/> is, exactly, a name Minecraft would accept.</summary>
    /// <remarks>
    /// Not a regex: <c>$</c> also matches before a final line break, so "Alice\n" passed.
    /// </remarks>
    public static bool IsValid(string? name) =>
        name is { Length: >= 1 and <= 16 } && name.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')
            or (>= '0' and <= '9') or '_');

    /// <summary>
    /// What Floodgate puts in front of a Bedrock player's name on a Java server unless its owner
    /// changed <c>username-prefix</c>. What a given server really uses is
    /// <see cref="FloodgateConfig.ReadUsernamePrefix"/>.
    /// </summary>
    public const string DefaultFloodgatePrefix = ".";

    /// <summary>
    /// The characters a Floodgate prefix may be made of for the app to accept it.
    /// </summary>
    /// <remarks>
    /// Chosen by what they cannot do, since the name they lead ends up in a console command, in a
    /// file name and next to chat: none of them is a space or a line break (a second argument, a
    /// second command), a path separator, a quote, <c>@</c> (a selector: <c>kick @a</c>), or a
    /// bracket (<c>&lt;name&gt;</c> and <c>[name]</c> are how chat is recognised). <c>*</c> is the
    /// one that is not a valid file name on Windows; <see cref="FileKey"/> deals with it.
    /// </remarks>
    public const string PrefixCharacters = ".*+-~!#$^=";

    /// <summary>Floodgate cuts the name to fit sixteen characters; a longer prefix leaves no name.</summary>
    public const int MaxPrefixLength = 3;

    /// <summary>Whether the app accepts <paramref name="prefix"/> as a server's Floodgate prefix.</summary>
    public static bool IsUsablePrefix(string? prefix) =>
        prefix is { Length: >= 1 and <= MaxPrefixLength } && prefix.All(c => PrefixCharacters.Contains(c));

    /// <summary>
    /// Whether <paramref name="name"/> is somebody the server can list as connected: a Java name, or
    /// a Bedrock one with this server's Floodgate prefix.
    /// </summary>
    /// <param name="name">The name as the server wrote it.</param>
    /// <param name="floodgatePrefix">
    /// The server's prefix, from <see cref="FloodgateConfig.ReadUsernamePrefix"/>. Empty means its
    /// Bedrock names are ordinary ones. One the app would not accept counts as empty.
    /// </param>
    /// <remarks>
    /// For reading the server's log and for the commands that act on a player already on it
    /// (<c>kick .Steve</c> works). Not for anything that creates a profile or a path from a name the
    /// owner typed: there <see cref="IsValid"/> still holds, and it is what keeps the Java rule
    /// strict. The prefix is exactly the server's, once, and what follows must be a name: with a
    /// dot, <c>..</c>, <c>..Steve</c> and <c>../x</c> still fail.
    /// </remarks>
    public static bool IsOnServer(string? name, string floodgatePrefix = DefaultFloodgatePrefix)
    {
        if (IsValid(name)) return true;

        return name is not null && IsUsablePrefix(floodgatePrefix)
            && name.Length > floodgatePrefix.Length
            && name.StartsWith(floodgatePrefix, StringComparison.Ordinal)
            && IsValid(name[floodgatePrefix.Length..]);
    }

    /// <summary>
    /// Whether <paramref name="name"/> has the shape of a name some server could have listed: a Java
    /// name, or one behind any prefix the app accepts.
    /// </summary>
    /// <remarks>
    /// For what keeps names without knowing which server's rule read them — the player history. It
    /// is the guard that stops a name from becoming a path there: letters, digits and underscores
    /// behind at most <see cref="MaxPrefixLength"/> of <see cref="PrefixCharacters"/>, none of which
    /// is a separator.
    /// </remarks>
    public static bool IsStorable(string? name)
    {
        if (name is null) return false;

        var start = 0;
        while (start < name.Length && start < MaxPrefixLength && PrefixCharacters.Contains(name[start])) start++;
        return IsValid(name[start..]);
    }

    /// <summary>
    /// A storable name as a file name: lower case, with the one prefix character Windows refuses in
    /// a file name written out.
    /// </summary>
    /// <remarks>
    /// <c>%</c> is not a prefix character, so the escape cannot collide with another name.
    /// </remarks>
    public static string FileKey(string name) => name.ToLowerInvariant().Replace("*", "%2a");
}
