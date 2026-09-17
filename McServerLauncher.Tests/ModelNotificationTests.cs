using System.ComponentModel;
using System.Reflection;
using McServerLauncher.Models;

namespace McServerLauncher.Tests;

/// <summary>
/// The two persisted models announcing their own changes.
/// </summary>
/// <remarks>
/// <para>
/// Both used to be plain objects, and the view models share the instance the dialogs edit. Nothing
/// derived from them ever recomputed, so converting a server or moving it to another Minecraft
/// version changed the disk and left the app describing what the folder used to be, until it was
/// restarted.
/// </para>
/// <para>
/// These tests are driven by reflection rather than by a list of property names, because a list is
/// the thing that gets forgotten. A property added as a plain <c>{ get; set; }</c> fails here on the
/// day it is written, not on the day somebody notices the app is lying.
/// </para>
/// </remarks>
public class ModelNotificationTests
{
    /// <summary>Every public property that can be written, which is every one that can go stale.</summary>
    private static IEnumerable<PropertyInfo> SettableOf<T>() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.SetMethod is { IsPublic: true });

    /// <summary>A value this property does not currently hold, so writing it has to be a change.</summary>
    private static object? Different(PropertyInfo property, object instance)
    {
        var current = property.GetValue(instance);
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type == typeof(bool)) return !(bool)(current ?? false);
        if (type == typeof(int)) return (int)(current ?? 0) + 1;
        if (type == typeof(string)) return (current as string ?? string.Empty) + "-otro";
        if (type == typeof(ServerType))
            return (ServerType)current! == ServerType.Purpur ? ServerType.Vanilla : ServerType.Purpur;
        if (type == typeof(NotificationSettings)) return new NotificationSettings();

        throw new Xunit.Sdk.XunitException(
            $"{property.DeclaringType!.Name}.{property.Name} is a {type.Name}, which this test does " +
            "not know how to change. Teach Different() about it rather than skipping the property.");
    }

    private static void AssertEveryPropertyAnnounces<T>() where T : INotifyPropertyChanged, new()
    {
        var properties = SettableOf<T>().ToList();
        Assert.NotEmpty(properties);   // a reflection test that finds nothing passes for the wrong reason

        foreach (var property in properties)
        {
            var instance = new T();
            var seen = new List<string?>();
            instance.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

            property.SetValue(instance, Different(property, instance));

            Assert.True(seen.Contains(property.Name),
                $"{typeof(T).Name}.{property.Name} changed without announcing it. Anything derived " +
                $"from it goes stale until the app restarts. Expected a PropertyChanged for " +
                $"'{property.Name}'; got: {(seen.Count == 0 ? "nothing" : string.Join(", ", seen))}.");
        }
    }

    [Fact]
    public void EveryServerConfigPropertyAnnouncesItsOwnName() =>
        AssertEveryPropertyAnnounces<ServerConfig>();

    [Fact]
    public void EveryNotificationSettingsPropertyAnnouncesItsOwnName() =>
        AssertEveryPropertyAnnounces<NotificationSettings>();

    [Fact]
    public void WritingTheSameValueAnnouncesNothing()
    {
        // The store panel re-runs its search on a change. A setter that fired whatever it was given
        // would turn every save of an untouched dialog into a round of network requests.
        var config = new ServerConfig { Type = ServerType.Fabric, GameVersion = "1.21.1" };
        var seen = new List<string?>();
        config.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        config.Type = ServerType.Fabric;
        config.GameVersion = "1.21.1";

        Assert.Empty(seen);
    }

    [Fact]
    public void TheComputedPathsFollowTheFolderAndTheJar()
    {
        // These two are read-only and derived, so they cannot announce for themselves — the two
        // fields they are built from have to do it on their behalf.
        var config = new ServerConfig { FolderPath = Path.Combine("C:", "old"), JarFile = "server.jar" };
        var seen = new List<string?>();
        config.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        config.FolderPath = Path.Combine("C:", "new");
        Assert.Contains(nameof(ServerConfig.JarFullPath), seen);
        Assert.Contains(nameof(ServerConfig.PropertiesPath), seen);

        seen.Clear();
        config.JarFile = "fabric-server.jar";
        Assert.Contains(nameof(ServerConfig.JarFullPath), seen);
    }

    [Fact]
    public void ACloneDoesNotCarryTheOriginalSubscribers()
    {
        // The trap a MemberwiseClone would fall into: the delegate field would be copied with
        // everything else, and the copy would raise changes at whoever was listening to the
        // original. NotificationSettings.Clone is field-by-field, and this is why.
        var original = new NotificationSettings();
        var seen = new List<string?>();
        original.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        var copy = original.Clone();
        copy.PlayerJoined = !copy.PlayerJoined;

        Assert.Empty(seen);
    }
}
