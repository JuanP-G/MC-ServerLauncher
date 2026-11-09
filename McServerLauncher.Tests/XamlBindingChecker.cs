using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace McServerLauncher.Tests;

/// <summary>
/// Reads a view's markup and checks every <c>{Binding ...}</c> against the view-model types it
/// binds to, following the data context the way Avalonia does.
/// </summary>
/// <remarks>
/// <para>
/// The views are <c>x:CompileBindings="False"</c>, so moving a property from one view model to another
/// compiles and shows nothing. Hosting a view catches that only for the bindings that are alive: the
/// row templates of an empty list never run, and <c>MainWindow</c> cannot be hosted at all (it builds
/// its own view model and wakes the servers when it loads). Reading the markup sees all of them.
/// </para>
/// <para>
/// The context is followed, not guessed: the root type for the view, then <c>DataContext="{Binding X}"</c>
/// (the type of <c>X</c>), a <c>DataTemplate</c>'s <c>DataType</c>, or the element type of the
/// <c>ItemsSource</c> a template belongs to, and <c>DataContext.X</c> with an <c>AncestorType</c> reads
/// the context of that ancestor. What cannot be followed (<c>ElementName</c>, <c>Source</c>, styles) is
/// skipped rather than guessed, so a miss reported here is a real one.
/// </para>
/// </remarks>
internal static partial class XamlBindingChecker
{
    private static readonly Assembly App = typeof(McServerLauncher.ViewModels.MainViewModel).Assembly;

    /// <summary>One binding that does not resolve, with where it is.</summary>
    internal sealed record Miss(string Path, string OnType, string Element);

    /// <summary>What a check found: the bindings that miss, and how many it could follow at all.</summary>
    internal sealed class Report
    {
        public readonly List<Miss> Misses = new();

        /// <summary>Bindings whose context was known, so they were really looked up.</summary>
        public int Followed;
    }

    /// <summary>Every binding of <paramref name="xaml"/> that names something its context does not have.</summary>
    internal static List<Miss> Check(string xaml, Type root) => Inspect(xaml, root).Misses;

    /// <summary>The same, with how many bindings were followed: a check that follows none proves nothing.</summary>
    internal static Report Inspect(string xaml, Type root)
    {
        var report = new Report();
        var doc = XDocument.Parse(xaml, LoadOptions.None);
        Walk(doc.Root!, root, new List<(XElement Element, Type? Context)>(), report);
        return report;
    }

    private static void Walk(XElement element, Type? inherited, List<(XElement, Type?)> stack, Report misses)
    {
        var name = element.Name.LocalName;
        if (IsSkipped(name)) return;

        // The DataContext binding of an element is resolved against its parent's context; every
        // other binding on it, against the context it just got.
        var context = inherited;
        if (AttributeOf(element, "DataContext") is { } dc && Parse(dc) is { } dcBinding)
            context = Resolve(dcBinding, inherited, stack, element, misses, forContext: true);
        else if (name == "DataTemplate")
            context = TemplateContext(element, inherited, stack);

        stack.Add((element, context));

        foreach (var attr in element.Attributes())
        {
            if (attr.Name.LocalName == "DataContext" && attr.Name.NamespaceName.Length == 0) continue;
            if (Parse(attr.Value) is { } b) Resolve(b, context, stack, element, misses, forContext: false);
        }

        // <Binding Path="..." /> inside a MultiBinding.
        if (name == "Binding" && AttributeOf(element, "Path") is { } p)
            Resolve(new Spec(p, AncestorOf(element), false), context, stack, element, misses, forContext: false);

        foreach (var child in element.Elements()) Walk(child, context, stack, misses);
        stack.RemoveAt(stack.Count - 1);
    }

    private static bool IsSkipped(string name) =>
        name is "Style" or "Styles" or "ControlTheme" or "ControlTemplate" or "KeyFrame"
        || name.EndsWith(".Styles", StringComparison.Ordinal)
        || name.EndsWith(".Resources", StringComparison.Ordinal)
        || name.EndsWith(".Transitions", StringComparison.Ordinal)
        || name.EndsWith(".KeyBindings", StringComparison.Ordinal);

    private static string? AttributeOf(XElement e, string name) => e.Attribute(name)?.Value;

    // ---- the data context of a template ----

    private static Type? TemplateContext(XElement template, Type? inherited, List<(XElement Element, Type? Context)> stack)
    {
        if (AttributeOf(template, "DataType") is { } dataType)
            return TypeNamed(dataType);

        // <ListBox><ListBox.ItemTemplate><DataTemplate>: the owner is two levels up, and what the
        // template shows is one item of its ItemsSource.
        if (stack.Count >= 2 && stack[^1].Element.Name.LocalName.EndsWith("Template", StringComparison.Ordinal))
        {
            var (owner, ownerContext) = stack[^2];
            if (AttributeOf(owner, "ItemsSource") is { } items && Parse(items) is { } spec
                && ownerContext is not null
                && Follow(ownerContext, spec.Path) is { } collection)
                return ElementTypeOf(collection);
        }
        return null;   // not followable: its bindings are skipped
    }

    private static Type? TypeNamed(string dataType)
    {
        var bare = dataType.Contains(':') ? dataType[(dataType.IndexOf(':') + 1)..] : dataType;
        return App.GetTypes().FirstOrDefault(t => t.Name == bare && t.Namespace?.StartsWith("McServerLauncher",
            StringComparison.Ordinal) == true);
    }

    private static Type? ElementTypeOf(Type collection)
    {
        if (collection.IsArray) return collection.GetElementType();
        var enumerable = collection.GetInterfaces().Concat(new[] { collection })
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    // ---- binding markup ----

    private sealed record Spec(string Path, string? Ancestor, bool Skip);

    private static Spec? Parse(string value)
    {
        value = value.Trim();
        var m = BindingMarkup().Match(value);
        if (!m.Success) return null;
        var body = m.Groups[1].Value.Trim();

        if (body.Contains("ElementName=", StringComparison.Ordinal)
            || Regex.IsMatch(body, @"\bSource=")
            || body.Contains("$parent", StringComparison.Ordinal)
            || body.Contains("$self", StringComparison.Ordinal)
            || body.Contains('#'))
            return new Spec("", null, Skip: true);

        string? ancestor = Regex.Match(body, @"AncestorType=(?:\w+:)?(\w+)") is { Success: true } a
            ? a.Groups[1].Value : null;

        var path = Regex.Match(body, @"\bPath=([^,}\s]+)") is { Success: true } named
            ? named.Groups[1].Value
            : FirstPositional(body);
        return new Spec(path, ancestor, Skip: false);
    }

    private static string FirstPositional(string body)
    {
        var first = body.Split(',')[0].Trim();
        return first.Contains('=') || first.StartsWith('{') ? "" : first;
    }

    private static string? AncestorOf(XElement binding) =>
        AttributeOf(binding, "RelativeSource") is { } rs
        && Regex.Match(rs, @"AncestorType=(?:\w+:)?(\w+)") is { Success: true } m ? m.Groups[1].Value : null;

    // ---- resolving a path ----

    private static Type? Resolve(Spec spec, Type? context, List<(XElement Element, Type? Context)> stack,
        XElement element, Report misses, bool forContext)
    {
        if (spec.Skip) return null;

        var path = spec.Path.TrimStart('!');
        var start = context;

        if (spec.Ancestor is not null)
        {
            // "DataContext.X" with an ancestor: X is looked up on that ancestor's own context.
            start = stack.LastOrDefault(s => s.Element.Name.LocalName == spec.Ancestor).Context;
            if (path.StartsWith("DataContext.", StringComparison.Ordinal)) path = path["DataContext.".Length..];
            else return null;   // a property of the ancestor control itself, not of a view model
        }

        if (start is null || path.Length == 0 || path == "." || path.StartsWith('(')) return null;

        misses.Followed++;
        var found = Follow(start, path, out var failed);
        if (failed is not null)
            misses.Misses.Add(new Miss(spec.Path, failed.Name, element.Name.LocalName));
        return forContext ? found : null;
    }

    private static Type? Follow(Type start, string path) => Follow(start, path, out _);

    /// <summary>The type the path ends on, or null if unknown; <paramref name="failedOn"/> is set on a miss.</summary>
    private static Type? Follow(Type start, string path, out Type? failedOn)
    {
        failedOn = null;
        var current = start;
        foreach (var raw in path.Split('.'))
        {
            var segment = raw.Contains('[') ? raw[..raw.IndexOf('[')] : raw;
            if (segment.Length == 0) return null;

            // Past the app's own types (strings, collections, framework types) there is nothing to check.
            if (current.Assembly != App) return null;

            var property = current.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance
                | BindingFlags.FlattenHierarchy);
            if (property is null)
            {
                failedOn = current;
                return null;
            }
            current = property.PropertyType;
        }
        return current;
    }

    [GeneratedRegex(@"^\{Binding(?:\s+(.*))?\}$", RegexOptions.Singleline)]
    private static partial Regex BindingMarkup();
}
