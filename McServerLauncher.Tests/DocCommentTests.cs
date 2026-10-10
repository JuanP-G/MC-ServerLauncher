namespace McServerLauncher.Tests;

/// <summary>
/// Keeps each XML doc comment on the member it describes.
/// </summary>
/// <remarks>
/// <para>
/// Inserting a member just above an existing doc comment leaves that comment stranded on top of
/// the new member's own one: two <c>&lt;summary&gt;</c> blocks in a row before a single signature.
/// The compiler accepts it without a warning (CS1571 only covers duplicate <c>&lt;param&gt;</c>),
/// so the generated McServerLauncher.xml, which DocFX publishes, documents the new member with its
/// neighbour's text and leaves the neighbour with none.
/// </para>
/// <para>
/// It happened twice in <c>ServerViewModel</c>, was fixed, and came back in three new places. A
/// rule that only a reviewer enforces does not hold, so this test enforces it.
/// </para>
/// </remarks>
public class DocCommentTests
{
    [Fact]
    public void NoMemberCarriesTwoSummaries()
    {
        var source = Path.Combine(LocalizationTests.RepoRoot(), "McServerLauncher");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("obj" + Path.DirectorySeparatorChar) ||
                relative.StartsWith("bin" + Path.DirectorySeparatorChar))
                continue;

            var lines = File.ReadAllLines(file);
            var closedAt = -1;   // line of the last </summary> still inside the same doc comment

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (!line.StartsWith("///"))
                {
                    closedAt = -1;   // a signature (or anything else) ends the doc comment
                    continue;
                }

                if (line.Contains("<summary>") && closedAt >= 0)
                    offenders.Add($"{relative}:{closedAt + 1} -> {i + 1}");

                if (line.Contains("</summary>")) closedAt = i;
            }
        }

        Assert.True(offenders.Count == 0,
            "A doc comment ends and another begins before any signature (the first one belongs to " +
            "some other member):\n  " + string.Join("\n  ", offenders));
    }
}
