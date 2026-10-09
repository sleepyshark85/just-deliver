namespace jd.resolver.expansion;

/// <summary>
/// The one definition of a config leaf: anything but an object, so an array is one leaf. Paths are dotted
/// (<c>consistencyPolicy.level</c>) and are the keys of a node's provenance.
/// </summary>
public static class ConfigLeaves
{
    private const char PathSeparator = '.';

    /// <summary>The leaves under <paramref name="value"/> in ordinal path order; <paramref name="path"/> is its prefix, empty for a node's config.</summary>
    public static IEnumerable<(string Path, ConfigValue Value)> Of(string path, ConfigValue value) =>
        value is ConfigObject obj
            ? obj.Properties.OrderBy(p => p.Key, StringComparer.Ordinal)
                .SelectMany(p => Of(path.Length == 0 ? p.Key : path + PathSeparator + p.Key, p.Value))
            : [(path, value)];
}
