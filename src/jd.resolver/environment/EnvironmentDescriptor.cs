using System.Diagnostics.CodeAnalysis;

namespace jd.resolver.environment;

/// <summary>
/// What an environment publishes to the workloads deployed on it. Format: schemas/environment.schema.json. <see cref="Offers"/> names the
/// components and the runtime a workload may get there. <see cref="Values"/> are the deployed parts' exports, keyed by the flattened dot path
/// <c>&lt;part&gt;.&lt;export&gt;</c>, read as <c>${&lt;part&gt;.&lt;export&gt;}</c>. A descriptor with an <see cref="Approval"/> policy is gated:
/// it takes deploys only through <c>jd promote</c> (docs/architecture/approvals.md section 2).
/// </summary>
public sealed record EnvironmentDescriptor(
    string Name,
    string Region,
    string Type,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> Offers,
    ApprovalPolicy? Approval)
{
    // Top-level fields addressed as env.<key>; values may not shadow them.
    private const string NameKey = "name";
    private const string RegionKey = "region";
    private const string TypeKey = "type";

    public static readonly IReadOnlyList<string> ReservedKeys = [NameKey, RegionKey, TypeKey];

    /// <summary>Resolves <c>env.&lt;field&gt;</c>: only the reserved keys, from the top-level fields.</summary>
    public bool TryGet(string field, [NotNullWhen(true)] out string? value)
    {
        value = field switch
        {
            NameKey => Name,
            RegionKey => Region,
            TypeKey => Type,
            _ => null,
        };
        return value is not null;
    }
}
