using System.Globalization;
using jd.core.identity;
using jd.resolver.environment;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace jd.resolver.release;

/// <summary>
/// The approval record written by <c>jd approve</c> (schemas/approval-record.schema.json, docs/architecture/approvals.md section 4), and the minimal
/// look at earlier ones that command needs. The record is built from an allowlist: the principal comes from the identity port and the meaning is
/// one string value, so nothing the approver types can add or change a field.
/// </summary>
public static class ApprovalRecord
{
    // The record's fields, in file order, camelCase as the schema spells them: Build writes it and ApproversAsync reads it, so the keys are named once.
    // Members are nullable because a file placed by hand may lack any of them.
    private sealed record Stored(string? Kind, string? ApprovedAt, StoredEnvironment? Environment, StoredRelease? Release, string? QualifiedOn, StoredIdentity? Identity, string? Meaning);

    private sealed record StoredEnvironment(string? Name, string? Tier);

    private sealed record StoredRelease(string? Label, string? SetSha256);

    private sealed record StoredIdentity(string? TenantId, string? ClientId, string? ObjectId);

    private static readonly JsonSerializerSettings Settings = new() { ContractResolver = new CamelCasePropertyNamesContractResolver(), DateParseHandling = DateParseHandling.None };

    /// <summary>The record of <paramref name="identity"/> approving the set <paramref name="setSha256"/> of <paramref name="label"/> for the gated <paramref name="target"/>.</summary>
    public static string Build(EnvironmentDescriptor target, string label, string setSha256, DeployerIdentity identity, string meaning, DateTimeOffset approvedAt) =>
        JsonConvert.SerializeObject(
            new Stored(
                "ApprovalRecord",
                approvedAt.UtcDateTime.ToString(ReleaseRecordFile.DocumentTimeFormat, CultureInfo.InvariantCulture),
                new StoredEnvironment(target.Name, target.Tier),
                new StoredRelease(label, setSha256),
                Policy(target).QualifiedOn,
                new StoredIdentity(identity.TenantId, identity.ClientId, identity.ObjectId),
                meaning),
            Formatting.Indented,
            Settings) + "\n";

    /// <summary>
    /// The distinct object ids in the approval files of <paramref name="label"/> on <paramref name="target"/> that name the same environment, the same set and the same
    /// <c>qualifiedOn</c> as <paramref name="target"/>'s policy. Not the valid-approval filter of approvals.md section 5 (S19): it only lets <c>jd approve</c> refuse a
    /// repeat and count, so a file that is not JSON, lacks a field or holds a field of another type is skipped.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ApproversAsync(
        string recordsDirectory, EnvironmentDescriptor target, string label, string setSha256, CancellationToken cancellationToken = default)
    {
        var policy = Policy(target);
        var directory = ReleaseRecordFile.LabelDirectory(recordsDirectory, target.Name, label);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var approvers = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*" + ReleaseRecordFile.NameEnd(ReleaseRecordFile.ApproveActivity)))
        {
            try
            {
                var stored = JsonConvert.DeserializeObject<Stored>(await File.ReadAllTextAsync(path, cancellationToken), Settings);
                if (stored is { Environment.Name: { } name, Release.SetSha256: { } sha256, QualifiedOn: { } qualifiedOn, Identity.ObjectId: { } approver }
                    && name == target.Name && sha256 == setSha256 && qualifiedOn == policy.QualifiedOn)
                {
                    approvers.Add(approver);
                }
            }
            catch (JsonException)
            {
                // Not an approval this command wrote; S19 reports such files.
            }
        }

        return approvers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static ApprovalPolicy Policy(EnvironmentDescriptor target) =>
        target.Approval ?? throw new ArgumentException($"environment '{target.Name}' has no approval policy, so nothing can be approved for it.", nameof(target));
}
