using jd.definitionvalidator;
using Newtonsoft.Json.Linq;
using YamlDotNet.Core;

namespace jd.resolver;

/// <summary>A source read as a document: where it came from, its kind and its tree (docs/architecture/documents.md section 2, concept 1).</summary>
internal sealed record Document(string Source, string Kind, JObject Body);

/// <summary>
/// Reads YAML text, or a tree already read, into <see cref="Document"/>s and validates each against the embedded schema that declares its kind. A caller
/// names the kinds it accepts; any other kind is refused. Parsing and validating are separate steps, so a caller can refuse a document before its schema
/// is applied.
/// </summary>
public static class DocumentLoader
{
    /// <summary>The one field read before a schema is known: it picks the schema. Public, so the writers of documents name it too.</summary>
    public const string KindField = "kind";

    /// <summary>Parses and validates YAML text; <paramref name="source"/> names it in errors.</summary>
    internal static async Task<LoadResult<Document>> LoadAsync(string source, string text, IReadOnlyCollection<string> kinds, CancellationToken cancellationToken) =>
        await ValidatedAsync(Parse(source, text, kinds), kinds, cancellationToken);

    /// <summary>Validates a tree already read, such as a JSON record; <paramref name="source"/> names it in errors.</summary>
    internal static async Task<LoadResult<Document>> LoadAsync(string source, JObject tree, IReadOnlyCollection<string> kinds, CancellationToken cancellationToken) =>
        await ValidatedAsync(Read(source, tree, kinds), kinds, cancellationToken);

    /// <summary>Parses YAML text into a document with a kind, without validating it; <paramref name="kinds"/> are named when the kind is missing.</summary>
    internal static LoadResult<Document> Parse(string source, string text, IReadOnlyCollection<string> kinds)
    {
        JToken tree;
        try
        {
            tree = YamlSchemaValidator.ParseYaml(text);
        }
        catch (YamlException ex)
        {
            return new LoadResult<Document>(null, [new LoadError(source, string.Empty, $"not valid YAML: {YamlSchemaValidator.DescribeYamlError(ex, text)}")]);
        }

        return Read(source, tree, kinds);
    }

    /// <summary>Refuses a kind outside <paramref name="kinds"/>, else validates the document against the schema of its kind.</summary>
    internal static async Task<IReadOnlyList<LoadError>> ValidateAsync(Document document, IReadOnlyCollection<string> kinds, CancellationToken cancellationToken)
    {
        if (!kinds.Contains(document.Kind))
        {
            return [new LoadError(document.Source, KindField, $"unknown kind '{document.Kind}'; expected one of {string.Join(", ", kinds)}.")];
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (await SchemaIndex.Shipped).Validate(document);
    }

    private static LoadResult<Document> Read(string source, JToken tree, IReadOnlyCollection<string> kinds)
    {
        if (tree is not JObject body)
        {
            return new LoadResult<Document>(null, [new LoadError(source, string.Empty, $"must be one YAML mapping with a '{KindField}' key.")]);
        }

        if (body[KindField] is not { Type: JTokenType.String } kind)
        {
            return new LoadResult<Document>(null, [new LoadError(source, KindField, $"required; one of {string.Join(", ", kinds)}.")]);
        }

        // The pattern above matched a string token, so the cast cannot give null.
        return new LoadResult<Document>(new Document(source, (string)kind!, body), []);
    }

    private static async Task<LoadResult<Document>> ValidatedAsync(LoadResult<Document> parsed, IReadOnlyCollection<string> kinds, CancellationToken cancellationToken)
    {
        if (parsed.Value is not { } document)
        {
            return parsed;
        }

        var errors = await ValidateAsync(document, kinds, cancellationToken);
        return errors.Count == 0 ? parsed : new LoadResult<Document>(null, errors);
    }
}
