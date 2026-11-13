using jd.resolver.expressions;
using Newtonsoft.Json.Linq;

namespace jd.resolver.environment;

/// <summary>
/// Loads an environment definition or descriptor into an <see cref="EnvironmentDescriptor"/> through the binder. A descriptor is a document whose
/// kind binds <see cref="Roles.EnvironmentValues"/>; a definition, one whose kind binds the environment's name but no values. The catalog's rules
/// are checked when the environment is resolved (<see cref="EnvironmentTypeCheck"/>). Every problem is collected, so a document can be fixed in one pass.
/// </summary>
public static class EnvironmentParser
{
    /// <summary>Loads the definition <paramref name="definition"/> holds; its errors name the document as it is named. The value is set exactly when there are no errors.</summary>
    public static async Task<LoadResult<EnvironmentDescriptor>> LoadDefinitionAsync(SourceDocument definition, CancellationToken cancellationToken)
    {
        var index = await SchemaIndex.Shipped;
        var definitions = index.KindsBinding(Roles.EnvironmentName).Except(DescriptorKinds(index)).ToList();
        return Build(index, await DocumentLoader.LoadAsync(definition.Name, definition.Text(), definitions, cancellationToken));
    }

    /// <summary>Loads the descriptor <paramref name="environment"/> holds; its errors name the document as it is named. The value is set exactly when there are no errors.</summary>
    public static Task<LoadResult<EnvironmentDescriptor>> LoadAsync(SourceDocument environment, CancellationToken cancellationToken) =>
        ParseAsync(environment.Name, environment.Text(), cancellationToken);

    /// <summary>Builds a descriptor from its text; <paramref name="source"/> names it in errors.</summary>
    public static async Task<LoadResult<EnvironmentDescriptor>> ParseAsync(string source, string content, CancellationToken cancellationToken = default)
    {
        var index = await SchemaIndex.Shipped;
        return Build(index, await DocumentLoader.LoadAsync(source, content, DescriptorKinds(index), cancellationToken));
    }

    /// <summary>The kinds of environment descriptor: the kinds whose schema binds <see cref="Roles.EnvironmentValues"/>, which an environment definition has none of.</summary>
    internal static IReadOnlyList<string> DescriptorKinds(SchemaIndex index) => index.KindsBinding(Roles.EnvironmentValues);

    private static LoadResult<EnvironmentDescriptor> Build(SchemaIndex index, LoadResult<Document> loaded)
    {
        if (loaded.Value is not { } document)
        {
            return new LoadResult<EnvironmentDescriptor>(null, loaded.Errors);
        }

        var binder = new Binder(index);
        var environment = binder.ReadEnvironment(document);
        List<LoadError> errors = [.. CheckReserved(binder.ReadValue(document, Roles.EnvironmentValues), document.Source), .. CheckApproval(environment, document.Source)];
        return new LoadResult<EnvironmentDescriptor>(errors.Count == 0 ? environment : null, errors);
    }

    // A top-level value may not take the name of a field env.* reads (ExpressionEvaluator.EnvFields); each is located at the value.
    private static IEnumerable<LoadError> CheckReserved(JToken? values, string source) =>
        from field in values is JObject mapping ? ExpressionEvaluator.EnvFields.Select(field => mapping.Property(field, StringComparison.Ordinal)) : []
        where field is not null
        select new LoadError(source, field.Value.Path, $"'{field.Name}' is reserved for the top-level field and cannot be a value.");

    // What the schema cannot say: the policy must refer to another environment and be satisfiable (concept 11).
    private static IEnumerable<LoadError> CheckApproval(EnvironmentDescriptor environment, string source)
    {
        if (environment.Approval is not { } approval)
        {
            yield break;
        }

        if (approval.QualifiedOn == environment.Name)
        {
            yield return new LoadError(source, environment.Location(Roles.ApprovalQualifiedOn), $"'{environment.Name}' cannot be qualified on itself; name the environment whose records must show the set qualified.");
        }

        if (approval.Required > approval.Approvers.Count)
        {
            yield return new LoadError(source, environment.Location(Roles.ApprovalRequired), $"{approval.Required} approvers are required but only {approval.Approvers.Count} are listed; the policy could never be met.");
        }
    }
}
