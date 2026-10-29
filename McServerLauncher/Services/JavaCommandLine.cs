using System.Text;

namespace McServerLauncher.Services;

/// <summary>
/// Java's arguments as a list, and the two ways a list meets a line of text: reading the user's
/// "extra JVM arguments" box into one, and writing one out for the console.
/// </summary>
/// <remarks>
/// <para>
/// The server used to be started from one string: the pieces were joined with spaces and the whole
/// handed to the operating system to cut up again. That is what made a jar name with a quote in it
/// break the command, and an args-file path with a space in it become two arguments. With a list
/// every piece stays one argument whatever is in it, and the quoting is left to
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>, which knows each platform's rules.
/// </para>
/// <para>
/// The one place text still has to be cut up is the user's own box, and it is cut the way the
/// operating system cut it before — spaces separate, double quotes group, a backslash only matters
/// in front of a quote — so what people already wrote there means what it always did.
/// </para>
/// </remarks>
internal static class JavaCommandLine
{
    /// <summary>Cuts a line into arguments: spaces separate, double quotes group and are removed.</summary>
    /// <remarks>
    /// A backslash is literal unless it comes before a double quote: <c>2n</c> backslashes and a quote
    /// are <c>n</c> backslashes and a quote that groups, <c>2n+1</c> are <c>n</c> backslashes and a
    /// literal quote. So <c>-Dpath=C:\mods\x</c> is left alone and <c>\"</c> is a quote character.
    /// </remarks>
    public static List<string> Split(string? line)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(line)) return args;

        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;   // distinguishes an empty "" argument from no argument at all
        var backslashes = 0;

        foreach (var c in line)
        {
            if (c == '\\') { backslashes++; continue; }

            if (c == '"')
            {
                current.Append('\\', backslashes / 2);
                if (backslashes % 2 == 1) current.Append('"');
                else inQuotes = !inQuotes;
                backslashes = 0;
                hasToken = true;
                continue;
            }

            current.Append('\\', backslashes);
            backslashes = 0;

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken) { args.Add(current.ToString()); current.Clear(); hasToken = false; }
                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        current.Append('\\', backslashes);
        if (hasToken || current.Length > 0) args.Add(current.ToString());
        return args;
    }

    /// <summary>
    /// The arguments as one line, for the console: each one that needs it is quoted so that
    /// <see cref="Split"/> would read the same list back.
    /// </summary>
    public static string Display(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    private static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"')) return arg;

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') quoted.Append('\\', backslashes * 2 + 1).Append('"');
            else quoted.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        // Backslashes at the end would escape the closing quote.
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
