using Newtonsoft.Json.Linq;

namespace jd.resolver;

/// <summary>
/// The JSON Schemas embedded in this assembly (see jd.resolver.csproj), by the document kinds each declares in its <c>kind</c> property's
/// <c>enum</c>; no kind or schema name is spelled in code (docs/architecture/documents.md section 2, concept 1).
/// </summary>
internal static class EmbeddedSchemas
{
    /// <summary>Each declared kind and the text of the schema that declares it.</summary>
    public static IReadOnlyDictionary<string, string> ByKind { get; } = Index(ReadAll());

    /// <summary>
    /// Maps every kind the <paramref name="schemas"/> declare to its schema's text. Throws <see cref="InvalidOperationException"/> when a schema
    /// declares no kind or two declare the same one: the schemas ship with jd, so either is a mistake in jd, not in a user's file.
    /// </summary>
    internal static Dictionary<string, string> Index(IEnumerable<(string Name, string Text)> schemas)
    {
        var byKind = new Dictionary<string, (string Name, string Text)>();
        foreach (var (name, text) in schemas)
        {
            var kinds = JObject.Parse(text)["properties"]?[DocumentLoader.KindField]?["enum"]?.Values<string>().OfType<string>().ToList() ?? [];
            if (kinds.Count == 0)
            {
                throw new InvalidOperationException($"Schema '{name}' declares no kind: it needs a '{DocumentLoader.KindField}' property with an enum.");
            }

            foreach (var kind in kinds)
            {
                if (!byKind.TryAdd(kind, (name, text)))
                {
                    throw new InvalidOperationException($"Schemas '{byKind[kind].Name}' and '{name}' both declare kind '{kind}'.");
                }
            }
        }

        return byKind.ToDictionary(k => k.Key, k => k.Value.Text);
    }

    // Every resource of the assembly is a schema: jd.resolver.csproj embeds nothing else.
    private static IEnumerable<(string Name, string Text)> ReadAll()
    {
        var assembly = typeof(EmbeddedSchemas).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            // The assembly listed the name itself, so the resource exists.
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            yield return (name, reader.ReadToEnd());
        }
    }
}
