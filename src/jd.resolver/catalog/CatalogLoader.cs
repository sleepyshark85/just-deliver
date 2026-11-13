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
            if (await ReadDocumentAsync(file, errors, cancellationToken) is not (var document, var isValid))
            {
                unreadable++;
                continue;
            }

            documents.Add(document);
            if (isValid)
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
        var catalog = BindAndCheck(index, set, errors);
        return new LoadResult<Catalog>(errors.Count == 0 ? catalog : null, errors);
    }

    // The concept records the valid documents hold, each collection from the kinds that bind its role, checked by the concept rules into errors;
    // null when no valid document declares the environment types. No errors means exactly one did.
    private static Catalog? BindAndCheck(SchemaIndex index, DocumentSet set, List<LoadError> errors)
    {
        var binder = new Binder(index);
        var documents = set.Documents;
        var bound = documents.Where(set.Valid.Contains).ToList();
        List<(Document Document, PartOwner Owner)> Owners(string role) => [.. Binding(index, bound, role)
            .Select(document => (document, binder.ReadOwner(document, NameRules.Defined(index, document).Select(name => name.Name).FirstOrDefault() ?? string.Empty)))];
        var components = Owners(Roles.Contract).ConvertAll(component => component.Owner);
        var shared = Owners(Roles.PartScope).ConvertAll(resource => resource.Owner);
        var boundRuntimes = Owners(Roles.RuntimePart);
        var runtimes = boundRuntimes.ConvertAll(runtime => runtime.Owner);
        var rollouts = boundRuntimes.SelectMany(runtime => runtime.Owner.Parts.Select(part => Rollout.Read(runtime.Document, runtime.Owner, part))).ToList();
        var policies = Binding(index, bound, Roles.PolicyName).ConvertAll(Policy.Read);
        var namingFiles = Binding(index, bound, Roles.NamingRules).ConvertAll(document => (document.Source, Rules: binder.ReadNamingRules(document)));
        var grantTargets = Merge(Binding(index, bound, Roles.GrantTargetInput).Select(binder.ReadGrantTargets));
        // A missing or invalid catalog document is reported on its own; checking against its types too would only add noise.
        var declaring = Binding(index, documents, Roles.EnvironmentTypeLayer).FirstOrDefault();
        var environmentTypes = declaring is not null && set.Valid.Contains(declaring) ? binder.ReadEnvironmentTypes(declaring) : null;

        var layers = index.Layers;
        var sharedByName = shared.DistinctBy(resource => resource.Name).ToDictionary(resource => resource.Name);
        errors.AddRange(namingFiles.SelectMany(file => file.Rules.Values.Select(rule => rule.CheckAllowed(file.Source))).OfType<LoadError>());
        errors.AddRange(NameRules.CheckReserved(index, Binding(index, documents, Roles.Owner), ExpressionEvaluator.ReservedRoots));
        errors.AddRange(ScopeRules.CheckReadOrder(layers, sharedByName));
        errors.AddRange(ReadRules.Check(layers, shared, sharedByName, components, runtimes));
        errors.AddRange(ScopeRules.CheckNodeNames(layers, shared.Concat(components), sharedByName));
        errors.AddRange(GrantRules.Check([.. shared, .. components, .. runtimes], layers, grantTargets, string.Join(", ", index.KindsBinding(Roles.GrantTargetInput))));
        errors.AddRange(environmentTypes is null ? [] : ScopeRules.CheckImplementationKeys(components.Concat(runtimes), environmentTypes, layers));
        errors.AddRange(PolicyRules.Check(policies));
        errors.AddRange(RolloutRules.Check(rollouts));
        errors.AddRange(ContractRules.CheckSameClasses(components));
        errors.AddRange(ContractRules.CheckExports(components, layers));

        if (errors.Count > 0 || declaring is null || environmentTypes is null)
        {
            return null;
        }

        return new Catalog(
            binder.ReadVersion(declaring),
            layers,
            environmentTypes,
            components.ToDictionary(component => component.Name),
            sharedByName,
            runtimes.ToDictionary(runtime => runtime.Name),
            rollouts.ToDictionary(rollout => Catalog.RolloutKey(rollout.Runtime, rollout.Part), rollout => (rollout.Probe, rollout.Release)),
            grantTargets,
            policies,
            Merge(namingFiles.Select(file => file.Rules)),
            Merge(Binding(index, bound, Roles.RoleDefinitions).Select(binder.ReadRoleDefinitions)));
    }

    // The document and whether it passed its schema; null when the file has no readable kind.
    private static async Task<(Document Document, bool Valid)?> ReadDocumentAsync(SourceDocument source, List<LoadError> errors, CancellationToken cancellationToken)
    {
        var parsed = DocumentLoader.Parse(source.Name, source.Text(), Kinds);
        errors.AddRange(parsed.Errors);
        if (parsed.Value is not { } document)
        {
            return null;
        }

        // An unknown kind is refused here: readable, so not a missing Catalog, and no kind-specific check applies to it.
        var schemaErrors = await DocumentLoader.ValidateAsync(document, Kinds, cancellationToken);
        errors.AddRange(schemaErrors);
        return (document, schemaErrors.Count == 0);
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
}
