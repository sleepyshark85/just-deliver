using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace jd.resolver.environment;

/// <summary>
/// Who may approve a release set for a gated environment and how many must (docs/architecture/approvals.md section 3). Literal data:
/// the <c>approval</c> block of an environment definition and descriptor (schemas/environment.schema.json). The two rules are pure;
/// they take the principals' object ids, which whoever reads the records supplies. Ids compare without regard to case.
/// </summary>
/// <param name="QualifiedOn">The environment whose records must show the set qualified.</param>
/// <param name="Required">How many distinct approvers are needed.</param>
/// <param name="Approvers">Entra object ids that may approve.</param>
/// <param name="SeparateFromDeployer">An approver may not be a principal that deployed the label on <paramref name="QualifiedOn"/>.</param>
public sealed record ApprovalPolicy(string QualifiedOn, int Required, IReadOnlyList<string> Approvers, bool SeparateFromDeployer)
{
    private static readonly JsonSerializer CamelCase = JsonSerializer.Create(new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() });

    /// <summary>The policy of a block that passed the schema.</summary>
    internal static ApprovalPolicy FromJson(JToken block) => block.ToObject<ApprovalPolicy>(CamelCase)!;

    /// <summary>The block as the schema writes it.</summary>
    internal JObject ToJson() => JObject.FromObject(this, CamelCase);

    /// <summary>Whether <paramref name="approver"/>'s approval may count: listed in <see cref="Approvers"/>, and not among <paramref name="deployers"/> when they must be separate.</summary>
    public bool Counts(string approver, IReadOnlyCollection<string> deployers) =>
        Approvers.Contains(approver, StringComparer.OrdinalIgnoreCase) && !(SeparateFromDeployer && deployers.Contains(approver, StringComparer.OrdinalIgnoreCase));

    /// <summary>Whether enough distinct principals among <paramref name="approvals"/> (the object id of each approval) count.</summary>
    public bool Evaluate(IEnumerable<string> approvals, IReadOnlyCollection<string> deployers) =>
        approvals.Where(a => Counts(a, deployers)).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= Required;
}
