using System.Reflection;
using McServerLauncher.Models;
using McServerLauncher.ViewModels;

namespace McServerLauncher.Tests;

/// <summary>
/// The table that says what each field of a server's config feeds.
/// </summary>
/// <remarks>
/// <para>
/// This is the test the whole arrangement exists for. The bug this repository kept shipping was
/// never hard: a field changed, and some getter went on returning the old answer because nobody
/// remembered it was derived from that field. Every fix was another method somebody had to
/// remember to extend, so the next field was a fresh chance to forget.
/// </para>
/// <para>
/// Here forgetting is not possible. A property added to <see cref="ServerConfig"/> without a row in
/// <see cref="ServerConfigEffects"/> fails on the day it is written, and the failure says what to do
/// about it. A row that names a view-model property which has since been renamed fails too, instead
/// of quietly announcing a name nothing listens for.
/// </para>
/// </remarks>
public class ServerConfigEffectsTests
{
    private static IEnumerable<PropertyInfo> WritableConfigProperties() =>
        typeof(ServerConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.SetMethod is { IsPublic: true });

    [Fact]
    public void EveryFieldOfTheConfigIsDeclaredExactlyOnce()
    {
        var declared = ServerConfigEffects.All.Select(r => r.ConfigProperty).ToList();

        var duplicates = declared.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        Assert.True(duplicates.Length == 0,
            "Declared twice in ServerConfigEffects, so one row silently wins: " +
            string.Join(", ", duplicates));

        var missing = WritableConfigProperties().Select(p => p.Name).Except(declared).ToArray();
        Assert.True(missing.Length == 0,
            $"New in ServerConfig and not in ServerConfigEffects: {string.Join(", ", missing)}. " +
            "Add a row saying which view-model properties it feeds and what has to be redone, or " +
            "an Unshown row saying why nothing on screen derives from it. Left out, it is a field " +
            "the app will go on showing the old value of until it is restarted.");

        var stale = declared.Except(WritableConfigProperties().Select(p => p.Name)).ToArray();
        Assert.True(stale.Length == 0,
            $"In ServerConfigEffects but no longer on ServerConfig: {string.Join(", ", stale)}.");
    }

    [Fact]
    public void ARowEitherFeedsSomethingOrSaysWhyItDoesNot()
    {
        foreach (var row in ServerConfigEffects.All)
        {
            var feedsSomething = row.ServerProperties.Length > 0
                                 || row.ModsProperties.Length > 0
                                 || row.Effects != ConfigEffect.None;

            Assert.True(feedsSomething == (row.NothingShowsIt is null),
                $"{row.ConfigProperty}: a row must either feed something or explain why it feeds " +
                "nothing — never both, and never neither. Both is a contradiction; neither is the " +
                "omission this table exists to prevent.");

            if (row.NothingShowsIt is { } reason)
                Assert.True(reason.Length > 20,
                    $"{row.ConfigProperty}: \"{reason}\" is not a reason anybody can check later.");
        }
    }

    [Fact]
    public void EveryPropertyNameInTheTableStillExists()
    {
        // A rename that misses the table leaves it announcing a name nothing is listening for,
        // which looks exactly like the original bug and is just as quiet.
        foreach (var row in ServerConfigEffects.All)
        {
            foreach (var name in row.ServerProperties)
                Assert.True(typeof(ServerViewModel).GetProperty(name) is not null,
                    $"{row.ConfigProperty} announces ServerViewModel.{name}, which does not exist.");

            foreach (var name in row.ModsProperties)
                Assert.True(typeof(ServerModsViewModel).GetProperty(name) is not null,
                    $"{row.ConfigProperty} announces ServerModsViewModel.{name}, which does not exist.");
        }
    }

    [Fact]
    public void EverythingIsTheUnionOfEveryRow()
    {
        // Everything is what ServerModsViewModel.RefreshFromConfig applies in one go, for a panel
        // built on its own. If it drifted from the sum of the rows, the wholesale path and the
        // one-field-at-a-time path would disagree, and only one of them would be tested.
        var all = ServerConfigEffects.Everything;

        Assert.Equal(
            ServerConfigEffects.All.SelectMany(r => r.ServerProperties).Distinct().OrderBy(n => n),
            all.ServerProperties.OrderBy(n => n));
        Assert.Equal(
            ServerConfigEffects.All.SelectMany(r => r.ModsProperties).Distinct().OrderBy(n => n),
            all.ModsProperties.OrderBy(n => n));

        foreach (var row in ServerConfigEffects.All)
            Assert.True(all.Effects.HasFlag(row.Effects),
                $"Everything is missing {row.Effects} from {row.ConfigProperty}.");
    }

    [Fact]
    public void TheFieldsThatDecideWhatAServerIsAreNotInTheUnshownDrawer()
    {
        // These four are the report that started all of this: convert a server or move it to another
        // Minecraft version and the app went on describing what the folder used to be. Anyone
        // tempted to quieten a failing test by moving one of them into the drawer has to delete
        // this test first, and then explain why.
        foreach (var property in new[]
                 {
                     nameof(ServerConfig.Type),
                     nameof(ServerConfig.GameVersion),
                     nameof(ServerConfig.FolderPath),
                     nameof(ServerConfig.Name)
                 })
        {
            var row = ServerConfigEffects.For(property);
            Assert.NotNull(row);
            Assert.Null(row!.NothingShowsIt);
        }
    }

    [Fact]
    public void ChangingTheTypeReachesBothTheCardAndTheStore()
    {
        var row = ServerConfigEffects.For(nameof(ServerConfig.Type))!;

        Assert.Contains(nameof(ServerViewModel.IsModded), row.ServerProperties);
        Assert.Contains(nameof(ServerModsViewModel.ContentTabTitle), row.ModsProperties);
        Assert.True(row.Effects.HasFlag(ConfigEffect.RescanContent));
        Assert.True(row.Effects.HasFlag(ConfigEffect.ReSearchStore));
    }

    [Fact]
    public void ChangingTheVersionReachesTheStoreEvenThoughTheTypeDidNot()
    {
        // The half that was missing: the old code only rebuilt when the *type* changed, so a server
        // moved from 1.21.1 to 1.21.4 kept offering mods chosen for the version it no longer ran.
        var row = ServerConfigEffects.For(nameof(ServerConfig.GameVersion))!;

        Assert.Contains(nameof(ServerViewModel.GameVersionText), row.ServerProperties);
        Assert.Contains(nameof(ServerModsViewModel.FilterVersionText), row.ModsProperties);
        Assert.True(row.Effects.HasFlag(ConfigEffect.ReSearchStore));
        Assert.True(row.Effects.HasFlag(ConfigEffect.CloseDetails));
    }

    [Fact]
    public void MovingTheFolderTakesEveryPanelWithIt()
    {
        // The folder is the server's whole identity on disk, and setting it used to refresh only
        // the MOTD — leaving the port, the installed mods and the backup list describing a
        // directory that no longer existed under that name.
        var row = ServerConfigEffects.For(nameof(ServerConfig.FolderPath))!;

        Assert.True(row.Effects.HasFlag(ConfigEffect.RereadPort));
        Assert.True(row.Effects.HasFlag(ConfigEffect.RereadInfo));
        Assert.True(row.Effects.HasFlag(ConfigEffect.RescanContent));
        Assert.True(row.Effects.HasFlag(ConfigEffect.ReloadBackups));
        Assert.True(row.Effects.HasFlag(ConfigEffect.RestartWakeListener));
    }
}
