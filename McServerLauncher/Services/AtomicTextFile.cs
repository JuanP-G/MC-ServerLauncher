using System.IO;

namespace McServerLauncher.Services;

/// <summary>
/// Writing a config file the app owns: only when it actually changed, and never half-written.
/// </summary>
/// <remarks>
/// <para>
/// Both halves earn their place. Geyser's <c>config.yml</c> is rewritten by the address refresh
/// every thirty seconds for as long as the server runs, and an unconditional
/// <see cref="File.WriteAllText(string,string)"/> there means the file is destroyed and recreated a
/// hundred and twenty times an hour to end up byte-for-byte identical — which also turns any bug in
/// what is written into something that reaches disk on its own, with nobody touching anything.
/// </para>
/// <para>
/// And the write goes through a temporary file for the same reason
/// <see cref="AtomicJsonFile"/> does: cut the power in the middle of the direct version and the
/// server is left with a truncated config that Geyser cannot parse.
/// </para>
/// </remarks>
public static class AtomicTextFile
{
    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> unless it is already there.
    /// </summary>
    /// <returns>True when the file was written, false when it already said exactly this.</returns>
    public static bool WriteIfChanged(string path, string text)
    {
        if (File.Exists(path))
        {
            try
            {
                if (File.ReadAllText(path) == text) return false;
            }
            catch
            {
                // Unreadable: fall through and write. Refusing to write because the comparison
                // failed would leave a file that may be the corrupt one exactly as it is.
            }
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);

        if (File.Exists(path))
            File.Replace(tmp, path, null);
        else
            File.Move(tmp, path);

        return true;
    }
}
