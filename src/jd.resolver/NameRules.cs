using Newtonsoft.Json.Linq;

namespace jd.resolver;

/// <summary>
/// The generic name rules over a set of documents (docs/architecture/documents.md sections 3 and 4): <c>one</c>, <c>define</c> and <c>ref</c>,
/// driven by the fields <see cref="SchemaIndex"/> finds annotated. No field, kind or namespace is spelled here; each error is located at the
/// value the index's path found.
/// </summary>
internal static class NameRules
{
    /// <summary>Every name problem in <paramref name="set"/>.</summary>
    public static IReadOnlyList<LoadError> Check(SchemaIndex index, DocumentSet set)
    {
        var errors = new List<LoadError>();
        var documents = set.Documents.Where(document => set.Kinds.Contains(document.Kind)).ToList();
        // A second document of a one kind is reported once, by its kind, so its names are not read.
        var named = documents.Except(One(index, set, documents, errors)).ToList();
        var defined = Define(index, named, errors);
        // A document that failed its schema is reported there; its references are not checked as well.
        Ref(index, set.Name, named.Where(set.Valid.Contains), defined, errors);
        return errors;
    }

    // Exactly one document of each one kind: none is an error unless an unreadable file might have been it. Returns the extra documents.
    private static List<Document> One(SchemaIndex index, DocumentSet set, List<Document> documents, List<LoadError> errors)
    {
        var extras = new List<Document>();
        foreach (var kind in set.Kinds.Where(kind => index.NamesOf(kind).IsOne))
        {
            var ofKind = documents.Where(document => document.Kind == kind).ToList();
            if (ofKind.Count == 0 && set.Unreadable == 0)
            {
                errors.Add(new LoadError($"({set.Name})", string.Empty, $"no file of kind {kind}; exactly one is required."));
            }

            foreach (var extra in ofKind.Skip(1))
            {
                errors.Add(new LoadError(extra.Source, DocumentLoader.KindField, $"a second {kind} file; exactly one is allowed (first: {ofKind[0].Source})."));
                extras.Add(extra);
            }
        }

        return extras;
    }

    // Each name once per namespace across the set; a field that defines in several namespaces clashes once, naming the file that holds the
    // name first. A document that failed its schema still defines its names, so a reference to one is not reported as a second mistake.
    // Returns the file that defined each name, by namespace and name.
    private static Dictionary<(string Namespace, string Name), string> Define(SchemaIndex index, List<Document> documents, List<LoadError> errors)
    {
        var defined = new Dictionary<(string Namespace, string Name), string>();
        foreach (var document in documents)
        {
            foreach (var field in index.NamesOf(document.Kind).Defines.GroupBy(field => field.Path))
            {
                var namespaces = field.Select(defining => defining.Namespace).ToList();
                foreach (var (location, name) in Names(document.Body, field.Key))
                {
                    if (namespaces.Select(space => defined.GetValueOrDefault((space, name))).FirstOrDefault(file => file is not null) is { } first)
                    {
                        errors.Add(new LoadError(document.Source, location, $"'{name}' is already declared in {first}."));
                        continue;
                    }

                    namespaces.ForEach(space => defined[(space, name)] = document.Source);
                }
            }
        }

        return defined;
    }

    // Every referring value is a name defined in its namespace; the key a map names its fallback is a key, not a reference.
    private static void Ref(
        SchemaIndex index, string set, IEnumerable<Document> documents, Dictionary<(string Namespace, string Name), string> defined, List<LoadError> errors)
    {
        foreach (var document in documents)
        {
            foreach (var field in index.NamesOf(document.Kind).Refs)
            {
                foreach (var (location, name) in Names(document.Body, field.Path).Where(found => found.Name != field.Fallback && !defined.ContainsKey((field.Namespace, found.Name))))
                {
                    var declared = defined.Keys.Where(key => key.Namespace == field.Namespace).Select(key => key.Name).Order(StringComparer.Ordinal);
                    errors.Add(new LoadError(document.Source, location,
                        $"'{name}' is not {Article(field.Namespace)} {field.Namespace} in the {set} (declared: {string.Join(", ", declared)})."));
                }
            }
        }
    }

    // The names a field holds, each with its location: a string is one name, a list's strings are names located at the list, a map's keys are
    // names located at the map followed by the key as written. A value of another shape (in a document that failed its schema) holds none.
    // A string token's cast cannot give null.
    private static IEnumerable<(string Location, string Name)> Names(JObject body, string path) =>
        body.SelectTokens(path).SelectMany(value => value switch
        {
            JValue { Type: JTokenType.String } text => [(value.Path, (string)text!)],
            JArray list => list.Where(item => item.Type == JTokenType.String).Select(item => (value.Path, (string)item!)),
            JObject map => map.Properties().Select(entry => ($"{map.Path}.{entry.Name}", entry.Name)),
            _ => Array.Empty<(string, string)>(),
        });

    private static string Article(string word) => "aeiou".Contains(word[0], StringComparison.Ordinal) ? "an" : "a";
}
