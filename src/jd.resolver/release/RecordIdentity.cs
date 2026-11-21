using jd.core.identity;
using Newtonsoft.Json.Linq;

namespace jd.resolver.release;

/// <summary>
/// The <c>identity</c> of release and approval records (ADR 0022): written as <c>{directory, principal, name}</c>, read in that shape or in
/// the shape written before it, <c>{tenantId, clientId, objectId}</c>, which the record schemas keep as read, never written.
/// </summary>
public static class RecordIdentity
{
    // jd's JSON evidence, kept as typed code beside its schemas (docs/architecture/documents.md section 2); the keys are named once, here.
    private const string DirectoryKey = "directory";
    private const string PrincipalKey = "principal";
    private const string NameKey = "name";
    private const string LegacyDirectoryKey = "tenantId";
    private const string LegacyPrincipalKey = "objectId";
    private const string LegacyNameKey = "clientId";

    /// <summary>The identity block a record writes: always the neutral shape.</summary>
    public static JObject Of(DeployerIdentity identity) =>
        new() { [DirectoryKey] = identity.Directory, [PrincipalKey] = identity.Principal, [NameKey] = identity.Name };

    /// <summary>
    /// The identity in a record's <c>identity</c> block, mapping the old shape to the neutral one (tenant to directory, object id to principal,
    /// client id to name, as <c>ArmDeployerIdentity</c> fills them). The record schema has vouched for the block before: exactly one shape, every
    /// field a non-empty string, so each '!' below holds.
    /// </summary>
    public static DeployerIdentity Read(JToken identity) =>
        identity[PrincipalKey] is null
            ? new((string)identity[LegacyDirectoryKey]!, (string)identity[LegacyPrincipalKey]!, (string)identity[LegacyNameKey]!)
            : new((string)identity[DirectoryKey]!, (string)identity[PrincipalKey]!, (string)identity[NameKey]!);
}
