using Newtonsoft.Json.Linq;

namespace jd.resolver.catalog;

/// <summary>
/// The in-memory form of a catalog directory: everything the resolver resolves workloads and environments against. Formats:
/// schemas/catalog/*.schema.json. Expressions (${...}) and config values are kept exactly as written; the expression evaluator interprets
/// them later. Source is the catalog-relative file a record came from. <see cref="Layers"/>: the layers in deploy order, from the schemas.
/// <see cref="Rollouts"/>: each runtime part's probe and release block, by the runtime's name and the part's selection key.
/// <see cref="GrantTargets"/>: grant template to the input naming the resource the grant is on (catalog/grants.yaml).
/// </summary>
public sealed record Catalog(
    string Version,
    IReadOnlyList<string> Layers,
    IReadOnlyDictionary<string, EnvironmentType> EnvironmentTypes,
    IReadOnlyDictionary<string, PartOwner> Components,
    IReadOnlyDictionary<string, PartOwner> SharedResources,
    IReadOnlyDictionary<string, PartOwner> Runtimes,
    IReadOnlyDictionary<(string Runtime, string Key), (Probe Probe, RuntimeRelease Release)> Rollouts,
    IReadOnlyDictionary<string, string> GrantTargets,
    IReadOnlyList<Policy> Policies,
    IReadOnlyDictionary<string, NamingRule> Naming,
    IReadOnlyDictionary<string, string> Roles)
{
    /// <summary>The key of <see cref="Rollouts"/> for <paramref name="part"/> of <paramref name="runtime"/>: the runtime's name and the part's one selection key.</summary>
    public static (string Runtime, string Key) RolloutKey(PartOwner runtime, Part part) => (runtime.Name, part.Keys.Single().Key);
}

/// <summary>
/// A declared environment type; <see cref="Approval"/>: an environment of this type must carry an approval block. <see cref="Layer"/>: which
/// layer a <c>jd env up</c> of this type deploys.
/// </summary>
public sealed record EnvironmentType(bool Approval, string Layer);

/// <summary>What a node's template does, which decides how the graph treats it.</summary>
public enum NodeKind
{
    /// <summary>Deploys a resource of its own; the default kind. The graph checks no grant target on it, and the runtime node is one.</summary>
    Create,

    /// <summary>Assigns a role on a resource; the catalog's grant targets name the input that holds the resource.</summary>
    Grant,
}

/// <summary>
/// A catalog node as written: the template it deploys, its kind and its config, expressions not yet evaluated. <see cref="Location"/> and
/// <see cref="ConfigLocation"/>: where the node and its config are in their file.
/// </summary>
public sealed record Node(string Template, NodeKind Kind, IReadOnlyDictionary<string, JToken> Config, string Location, string ConfigLocation);

/// <summary>A policy's criteria key (type, class, environmentType, kind, runtime, template) to the value it must equal.</summary>
public sealed record MatchCriteria(IReadOnlyDictionary<string, string> Criteria)
{
    // What a node-scope policy matches: the requirement's type (a component name) and class, and the environment's type.
    internal const string TypeKey = "type";
    internal const string ClassKey = "class";
    internal const string EnvironmentTypeKey = "environmentType";

    // A node's template, and the runtime scope (kind: runtime) of a workload-scope policy.
    internal const string TemplateKey = "template";
    internal const string KindKey = "kind";
    internal const string RuntimeKey = "runtime";
    internal const string RuntimeKind = "runtime";
}

/// <summary>
/// How the platform checks a deployed runtime is healthy: GET <see cref="Path"/> must answer <see cref="ExpectedStatus"/>, tried every
/// <see cref="IntervalSeconds"/> for at most <see cref="TimeoutSeconds"/>. Data only; the release flow executes it.
/// </summary>
public sealed record Probe(string Path, int ExpectedStatus, int TimeoutSeconds, int IntervalSeconds);

/// <summary>
/// What the release flow needs to know about the runtime node's template, so no template names are in code. Inputs the engine sets
/// per step: <see cref="SuffixInput"/>, <see cref="TrafficInput"/>. Outputs it reads: <see cref="TrafficOutput"/> (the traffic last
/// deployed, naming the live revision), <see cref="SuffixOutput"/> (the suffix last deployed), <see cref="RevisionOutput"/> (the revision this deploy created), <see cref="FqdnOutput"/> (its
/// address, the probe target), <see cref="ResourceIdOutput"/> (the runtime resource's id, which qualification looks its live revisions up by). Traffic items: <see cref="RevisionKey"/> names a revision, <see cref="LatestKey"/> means the latest one.
/// </summary>
public sealed record RuntimeRelease(
    string SuffixInput,
    string TrafficInput,
    string TrafficOutput,
    string SuffixOutput,
    string RevisionOutput,
    string FqdnOutput,
    string RevisionKey,
    string LatestKey,
    string WeightKey,
    string ResourceIdOutput);

/// <summary>An IT Ops rule: <see cref="Set"/> and <see cref="Default"/> config fields of the nodes it matches; <see cref="Require"/>: a component every workload it matches must require (null when none).</summary>
public sealed record Policy(
    string Source,
    string Name,
    string Reason,
    MatchCriteria Match,
    IReadOnlyDictionary<string, JToken> Set,
    IReadOnlyDictionary<string, JToken> Default,
    string? Require);

/// <summary>How a resource kind's names are built: <see cref="Pattern"/>'s placeholders filled, characters outside the <see cref="Allowed"/> pattern dropped, and cut to <see cref="MaxLength"/> keeping a hash.</summary>
public sealed record NamingRule(string Pattern, int MaxLength, string Allowed);
