using Newtonsoft.Json.Linq;

namespace jd.resolver.catalog;

// The in-memory form of a catalog directory. Formats: schemas/catalog/*.schema.json.
// Expressions (${...}) and config values are kept exactly as written; the expression
// evaluator interprets them later. Source is the catalog-relative file a record came from.

public sealed record Catalog(
    string Version,
    IReadOnlyDictionary<string, EnvironmentType> EnvironmentTypes,
    IReadOnlyDictionary<string, Component> Components,
    IReadOnlyDictionary<string, SharedResource> SharedResources,
    IReadOnlyDictionary<string, Runtime> Runtimes,
    IReadOnlyList<Policy> Policies,
    IReadOnlyDictionary<string, NamingRule> Naming,
    IReadOnlyDictionary<string, string> Roles);

/// <summary>
/// A declared environment type; <see cref="Approval"/>: an environment of this type must carry an approval block. <see cref="Layer"/>: which
/// scope a <c>jd env up</c> of this type deploys.
/// </summary>
public sealed record EnvironmentType(bool Approval, SharedScope Layer);

/// <summary>
/// What a workload requires by name: one contract (<see cref="Exports"/>) and an implementation per environment type, keyed by the type or
/// <see cref="ImplementationKey.Default"/>. Every class of every implementation exports exactly the contract. <see cref="Uses"/>: the
/// shared resources its parts may read.
/// </summary>
public sealed record Component(
    string Source, string Name, IReadOnlyList<string> Exports, IReadOnlyList<string> Uses, IReadOnlyDictionary<string, Implementation> Implementations);

/// <summary>
/// A component's implementation for an environment type: <see cref="Environment"/>, deployed once per environment (null when none), and
/// class (a requirement's <c>class</c>) to what each requiring workload gets.
/// </summary>
public sealed record Implementation(Part? Environment, IReadOnlyDictionary<string, Part> Classes);

/// <summary>A platform-owned part deployed once per <see cref="Scope"/>, read by the parts that name it in their <c>uses</c>.</summary>
public sealed record SharedResource(string Source, string Name, SharedScope Scope, IReadOnlyList<string> Uses, Part Part);

/// <summary>Where a shared resource is deployed, and so the layer an environment type is: once for the shared layer, or once per environment.</summary>
public enum SharedScope
{
    Shared,
    Environment,
}

/// <summary>A set of nodes deployed together, and the exports evaluated from them.</summary>
public sealed record Part(IReadOnlyDictionary<string, Node> Nodes, IReadOnlyDictionary<string, string> Exports);

/// <summary>The compute every workload gets: no contract, no classes, an implementation per environment type like a <see cref="Component"/>.</summary>
public sealed record Runtime(string Source, string Name, IReadOnlyList<string> Uses, IReadOnlyDictionary<string, RuntimeImplementation> Implementations);

/// <summary>A runtime's implementation: its <see cref="Nodes"/> include the runtime node, deployed in steps by the release flow.</summary>
public sealed record RuntimeImplementation(Probe Probe, RuntimeRelease Release, IReadOnlyDictionary<string, Node> Nodes);

/// <summary>Selects the implementation a component or runtime has for an environment type: by exact key, else <see cref="Default"/>.</summary>
internal static class ImplementationKey
{
    /// <summary>The key of the implementation every environment type without its own key uses. Part of the catalog format; not a type.</summary>
    public const string Default = "default";

    /// <summary>The key of the implementation <paramref name="environmentType"/> uses, or null when there is neither its own nor a default.</summary>
    public static string? For<T>(IReadOnlyDictionary<string, T> implementations, string environmentType) =>
        implementations.ContainsKey(environmentType) ? environmentType
        : implementations.ContainsKey(Default) ? Default
        : null;
}

public enum NodeKind
{
    Create,
    Grant,
}

public sealed record Node(string Template, NodeKind Kind, IReadOnlyDictionary<string, JToken> Config);

/// <summary>A policy's criteria key (type, class, environmentType, kind, runtime, template) to the value it must equal.</summary>
public sealed record MatchCriteria(IReadOnlyDictionary<string, string> Criteria)
{
    // What a node-scope policy matches: the requirement's type (a component name) and class, and the environment's type.
    public const string TypeKey = "type";
    public const string ClassKey = "class";
    public const string EnvironmentTypeKey = "environmentType";

    // A node's template, and the runtime scope (kind: runtime) of a workload-scope policy.
    public const string TemplateKey = "template";
    public const string KindKey = "kind";
    public const string RuntimeKey = "runtime";
    public const string RuntimeKind = "runtime";
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

public sealed record NamingRule(string Pattern, int MaxLength, string Allowed);
