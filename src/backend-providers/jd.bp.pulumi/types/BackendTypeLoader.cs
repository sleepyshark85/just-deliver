using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace jd.bp.pulumi.types;

/// <summary>Reads the Pulumi type translations and enforces folder-level invariants.</summary>
public class BackendTypeLoader
{
    private const string SharedFileName = "_shared.yml";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Translations shipped with jd.bp.pulumi, from the backend-types folder beside the assembly.</summary>
    public static BackendTypeCatalog LoadDefault() =>
        LoadDirectory(Path.Combine(AppContext.BaseDirectory, "backend-types"));

    public static BackendTypeCatalog LoadDirectory(string directory)
    {
        var sharedPath = Path.Combine(directory, SharedFileName);
        if (!File.Exists(sharedPath))
        {
            throw new FileNotFoundException($"Shared backend settings not found at '{sharedPath}'.");
        }

        var shared = Deserializer.Deserialize<BackendSharedSettings>(File.ReadAllText(sharedPath))
            ?? throw new InvalidOperationException($"'{SharedFileName}' is empty.");

        var translations = new List<BackendTypeTranslation>();
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var deployment in shared.Foundation)
        {
            owner[deployment.Id] = SharedFileName;
        }

        foreach (var path in Directory.GetFiles(directory, "*.yml").OrderBy(p => p))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.StartsWith('_'))
            {
                continue;
            }

            var translation = Deserializer.Deserialize<BackendTypeTranslation>(File.ReadAllText(path))
                ?? throw new InvalidOperationException($"'{fileName}' is empty.");

            var expected = Path.GetFileNameWithoutExtension(fileName);
            if (translation.Type != expected)
            {
                throw new InvalidOperationException(
                    $"'{fileName}' declares type '{translation.Type}' but the filename says '{expected}'.");
            }

            // Ids are global across the folder, so a duplicate would silently rebind a
            // ${deployment.*} reference rather than collide visibly.
            foreach (var deployment in translation.Deployments)
            {
                if (owner.TryGetValue(deployment.Id, out var other))
                {
                    throw new InvalidOperationException(
                        $"Duplicate deployment id '{deployment.Id}' in '{fileName}' and '{other}'. Ids must be unique across all translations.");
                }

                owner[deployment.Id] = fileName;
            }

            translations.Add(translation);
        }

        return new BackendTypeCatalog(shared, translations);
    }
}

/// <summary>
/// The resource types this backend can realise. Answers the capability question a workload is
/// validated against: requiring a type with no translation should fail at submission, not
/// midway through provisioning.
/// </summary>
public class BackendTypeCatalog
{
    private readonly Dictionary<string, BackendTypeTranslation> _byType;

    public BackendTypeCatalog(BackendSharedSettings shared, IEnumerable<BackendTypeTranslation> translations)
    {
        Shared = shared;
        _byType = translations.ToDictionary(t => t.Type, StringComparer.Ordinal);
    }

    public BackendSharedSettings Shared { get; }

    public IReadOnlyCollection<string> Types => _byType.Keys;

    public bool Supports(string type) => _byType.ContainsKey(type);

    public BackendTypeTranslation Get(string type) =>
        _byType.TryGetValue(type, out var translation)
            ? translation
            : throw new ArgumentException(
                $"This backend has no translation for resource type '{type}'. Supported: {string.Join(", ", Types.Order())}.",
                nameof(type));

    /// <summary>Every deployment, including the shared foundation ones.</summary>
    public IEnumerable<BackendDeployment> AllDeployments =>
        Shared.Foundation.Concat(_byType.Values.SelectMany(t => t.Deployments));
}
