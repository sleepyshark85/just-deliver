namespace jd.core.resources;

/// <summary>
/// The resource types the platform understands. Single source of truth for the workload
/// schema's type enum, for validating references, and for the capability question a backend
/// answers - all three previously drifted from each other because each had its own list.
/// </summary>
public class ResourceTypeCatalog
{
    private readonly Dictionary<string, ResourceTypeDefinition> _types;

    public ResourceTypeCatalog(IEnumerable<ResourceTypeDefinition> types)
    {
        _types = new Dictionary<string, ResourceTypeDefinition>(StringComparer.Ordinal);

        foreach (var type in types)
        {
            if (string.IsNullOrWhiteSpace(type.Name))
            {
                throw new ArgumentException("A resource type definition has no name.", nameof(types));
            }

            if (!_types.TryAdd(type.Name, type))
            {
                throw new ArgumentException($"Resource type '{type.Name}' is declared more than once.", nameof(types));
            }
        }
    }

    public IReadOnlyCollection<string> Names => _types.Keys;

    public bool Contains(string name) => _types.ContainsKey(name);

    public bool TryGet(string name, out ResourceTypeDefinition definition) =>
        _types.TryGetValue(name, out definition!);

    public ResourceTypeDefinition Get(string name) =>
        TryGet(name, out var definition)
            ? definition
            : throw new ArgumentException(
                $"Unknown resource type '{name}'. Known: {string.Join(", ", Names.Order())}.", nameof(name));
}
