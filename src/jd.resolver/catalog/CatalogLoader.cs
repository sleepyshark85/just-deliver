using jd.resolver.expressions;

namespace jd.resolver.catalog;

/// <summary>
/// Loads a <see cref="Catalog"/> from a <see cref="SourceCatalog"/>, whose templates are its own part (<see cref="Templates"/>), as a composition
/// (docs/architecture/documents.md section 3): the document loader reads each file, the name rules check names across them, the binder fills the
/// concept records, and the concept rules check them. Each collection is filled from the documents whose kind binds its concept's role, so no
/// kind is dispatched on. Pure: reading the files is the client's job. Every problem in every file is collected, so a catalog can be fixed in one pass.
/// </summary>
public static class CatalogLoader
{
    // The kinds a catalog holds, in the order errors list them; any other kind is refused.
    private static readonly string[] Kinds = ["Catalog", "Component", "Runtime", "SharedResource", "Policy", "Naming", "Roles", "Grants"];

    // What the name rules call the catalog in errors about it as a whole.
    private const string SetName = "catalog";

    /// <summary>Templates are Pulumi YAML, not catalog documents: the layout keeps them in this directory of the catalog, a part of their own.</summary>
    public const string TemplatesDirectory = "templates";

    private const string TemplatesPrefix = TemplatesDirectory + "/";

    /// <summary>The templates part of <paramref name="catalog"/>: its documents under <see cref="TemplatesDirectory"/>, named relative to it, under the name the catalog's name and the directory make.</summary>
    public static SourceCatalog Templates(SourceCatalog catalog) => new(
        Path.Combine(catalog.Name, TemplatesDirectory),
        [.. catalog.Documents.Where(IsTemplate).Select(document => document with { Name = document.Name[TemplatesPrefix.Length..] })]);

    /// <summary>Loads the catalog from its documents, the templates part aside; the value is set exactly when no document has an error.</summary>
    public static async Task<LoadResult<Catalog>> LoadAsync(SourceCatalog source, CancellationToken cancellationToken = default)
    {
        var errors = new List<LoadError>();
        var documents = new List<Document>();
        // A document that failed its schema is not in here, but still counts toward the cross-file checks that only need its kind or name,
        // so one mistake is not reported a second time as a missing or undeclared item.
        var valid = new HashSet<Document>();
        var unreadable = 0;
        foreach (var file in source.Documents.Where(document => !IsTemplate(document)).OrderBy(document => document.Name, StringComparer.Ordinal))
        {
            var read = await ReadDocumentAsync(file, cancellationToken);
            errors.AddRange(read.Errors);
            if (read.Document is not { } document)
            {
                unreadable++;
                continue;
            }

            documents.Add(document);
            if (read.Valid)
            {
                valid.Add(document);
            }
        }

        // Names first (one Catalog, names unique, references declared); a name declared twice is that error, and a read of it that is not in uses is
        // judged first as a component or runtime, then as a shared resource.
        var index = await SchemaIndex.Shipped;
        index.CheckBindings();
        var set = new DocumentSet(SetName, Kinds, documents, valid, unreadable);
        errors.AddRange(NameRules.Check(index, set));
        var bound = Bind(index, set);
        errors.AddRange(Check(index, bound));
        return new LoadResult<Catalog>(errors.Count == 0 ? Build(index, bound) : null, errors);
    }

    // The concept records the valid documents hold, each collection from the kinds that bind its role.
    private static Bound Bind(SchemaIndex index, DocumentSet set)
    {
        var binder = new Binder(index);
        var documents = set.Documents;
        var valid = documents.Where(set.Valid.Contains).ToList();
        List<(Document Document, PartOwner Owner)> Owners(string role) => [.. Binding(index, valid, role)
            // A document that binds an owner role defines its name (valid documents do), so there is always a first.
            .Select(document => (document, binder.ReadOwner(document, NameRules.Defined(index, document).Select(name => name.Name).First())))];
        var components = Owners(Roles.Contract).ConvertAll(component => component.Owner);
        var shared = Owners(Roles.PartScope).ConvertAll(resource => resource.Owner);
        var runtimes = Owners(Roles.RuntimePart);
        var namingFiles = Binding(index, valid, Roles.NamingRules).ConvertAll(document => (document.Source, Rules: binder.ReadNamingRules(document)));
        // A missing or invalid catalog document is reported on its own; checking against its types too would only add noise.
        var declaring = Binding(index, documents, Roles.EnvironmentTypeLayer).FirstOrDefault();
        (string Version, IReadOnlyDictionary<string, EnvironmentType> Types)? environment =
            declaring is not null && set.Valid.Contains(declaring) ? (binder.ReadVersion(declaring), binder.ReadEnvironmentTypes(declaring)) : null;

        return new Bound(
            Binding(index, documents, Roles.Owner),
            components,
            shared,
            runtimes.ConvertAll(runtime => runtime.Owner),
            shared.DistinctBy(resource => resource.Name).ToDictionary(resource => resource.Name),
            [.. runtimes.SelectMany(runtime => runtime.Owner.Parts.Select(part => Rollout.Read(runtime.Document, runtime.Owner, part)))],
            Binding(index, valid, Roles.PolicyName).ConvertAll(binder.ReadPolicy),
            namingFiles,
            Merge(namingFiles.Select(file => file.Rules)),
            Merge(Binding(index, valid, Roles.GrantTargetInput).Select(binder.ReadGrantTargets)),
            Merge(Binding(index, valid, Roles.RoleDefinitions).Select(binder.ReadRoleDefinitions)),
            environment);
    }

    // The concept rules over the bound collections, in the order their errors list.
    private static IReadOnlyList<LoadError> Check(SchemaIndex index, Bound bound)
    {
        var layers = index.Layers;
        var types = bound.Environment?.Types;
        return
        [
            .. bound.NamingFiles.SelectMany(file => file.Rules.Values.Select(rule => rule.CheckAllowed(file.Source))).OfType<LoadError>(),
            .. NameRules.CheckReserved(index, bound.OwnerDocuments, ExpressionEvaluator.ReservedRoots),
            .. ScopeRules.CheckReadOrder(layers, bound.SharedByName),
            .. ReadRules.Check(layers, bound.Shared, bound.SharedByName, bound.Components, bound.Runtimes),
            .. ScopeRules.CheckNodeNames(layers, bound.Shared.Concat(bound.Components), bound.SharedByName),
            .. GrantRules.Check([.. bound.Shared, .. bound.Components, .. bound.Runtimes], layers, bound.GrantTargets, string.Join(", ", index.KindsBinding(Roles.GrantTargetInput))),
            .. types is null ? [] : ScopeRules.CheckImplementationKeys(bound.Components.Concat(bound.Runtimes), types, layers),
            .. PolicyRules.Check(bound.Policies),
            .. RolloutRules.Check(bound.Rollouts),
            .. ContractRules.CheckSameClasses(bound.Components),
            .. ContractRules.CheckExports(bound.Components, layers),
        ];
    }

    // The catalog the bound collections make; null when no valid document declares the environment types, which no errors rules out: exactly one does.
    private static Catalog? Build(SchemaIndex index, Bound bound) => bound.Environment is not var (version, types) ? null : new Catalog(
        version,
        index.Layers,
        types,
        bound.Components.ToDictionary(component => component.Name),
        bound.SharedByName,
        bound.Runtimes.ToDictionary(runtime => runtime.Name),
        bound.Rollouts.ToDictionary(rollout => Catalog.RolloutKey(rollout.Runtime, rollout.Part), rollout => (rollout.Probe, rollout.Release)),
        bound.GrantTargets,
        bound.Policies,
        bound.Naming,
        bound.RoleDefinitions);

    // The document and whether it passed its schema, with the errors of reading it; no document when the file has no readable kind.
    private static async Task<ReadResult> ReadDocumentAsync(SourceDocument source, CancellationToken cancellationToken)
    {
        var parsed = DocumentLoader.Parse(source.Name, source.Text(), Kinds);
        if (parsed.Value is not { } document)
        {
            return new ReadResult(null, false, parsed.Errors);
        }

        // An unknown kind is refused here: readable, so not a missing Catalog, and no kind-specific check applies to it.
        var schemaErrors = await DocumentLoader.ValidateAsync(document, Kinds, cancellationToken);
        return new ReadResult(document, schemaErrors.Count == 0, [.. parsed.Errors, .. schemaErrors]);
    }

    private static bool IsTemplate(SourceDocument document) => document.Name.StartsWith(TemplatesPrefix, StringComparison.Ordinal);

    // The documents of a kind whose schema binds the role.
    private static List<Document> Binding(SchemaIndex index, IEnumerable<Document> documents, string role)
    {
        var kinds = index.KindsBinding(role);
        return [.. documents.Where(document => kinds.Contains(document.Kind))];
    }

    // Naming, Roles and Grants files are merged; a key in two files is the define rule's error, and the first is kept.
    private static Dictionary<string, T> Merge<T>(IEnumerable<IReadOnlyDictionary<string, T>> files) =>
        files.SelectMany(file => file).DistinctBy(entry => entry.Key).ToDictionary(entry => entry.Key, entry => entry.Value);

    private sealed record ReadResult(Document? Document, bool Valid, IReadOnlyList<LoadError> Errors);

    // What the valid documents hold, one collection per concept; Environment is the Catalog document's version and environment types, when it is valid.
    private sealed record Bound(
        IReadOnlyList<Document> OwnerDocuments,
        IReadOnlyList<PartOwner> Components,
        IReadOnlyList<PartOwner> Shared,
        IReadOnlyList<PartOwner> Runtimes,
        IReadOnlyDictionary<string, PartOwner> SharedByName,
        IReadOnlyList<Rollout> Rollouts,
        IReadOnlyList<Policy> Policies,
        IReadOnlyList<(string Source, IReadOnlyDictionary<string, NamingRule> Rules)> NamingFiles,
        IReadOnlyDictionary<string, NamingRule> Naming,
        IReadOnlyDictionary<string, string> GrantTargets,
        IReadOnlyDictionary<string, string> RoleDefinitions,
        (string Version, IReadOnlyDictionary<string, EnvironmentType> Types)? Environment);
}
