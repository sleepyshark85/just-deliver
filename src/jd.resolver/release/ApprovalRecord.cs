using System.Globalization;
using jd.core.identity;
using jd.resolver.environment;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace jd.resolver.release;

/// <summary>
/// The approval record written by <c>jd approve</c> (schemas/approval-record.schema.json, docs/architecture/approvals.md section 4), and the minimal
/// look at earlier ones that command needs. The record is built from an allowlist: the principal comes from the identity port and the meaning is
/// one string value, so nothing the approver types can add or change a field.
/// </summary>
public static class ApprovalRecord
{
    /// <summary>The record of <paramref name="identity"/> approving the set <paramref name="setSha256"/> of <paramref name="label"/> for the gated <paramref name="target"/>.</summary>
    public static string Build(EnvironmentDescriptor target, string label, string setSha256, DeployerIdentity identity, string meaning, DateTimeOffset approvedAt) =>
        new JObject
        {
            ["kind"] = "ApprovalRecord",
            ["approvedAt"] = approvedAt.UtcDateTime.ToString(ReleaseRecordFile.DocumentTimeFormat, CultureInfo.InvariantCulture),
            ["environment"] = new JObject { ["name"] = target.Name, ["tier"] = target.Tier },
            ["release"] = new JObject { ["label"] = label, ["setSha256"] = setSha256 },
            ["qualifiedOn"] = Policy(target).QualifiedOn,
            ["identity"] = new JObject { ["tenantId"] = identity.TenantId, ["clientId"] = identity.ClientId, ["objectId"] = identity.ObjectId },
            ["meaning"] = meaning,
        }.ToString(Formatting.Indented) + "\n";

    /// <summary>
    /// The distinct object ids in the approval files of <paramref name="label"/> on <paramref name="target"/> that name the same environment, the same set and the same
    /// <c>qualifiedOn</c> as <paramref name="target"/>'s policy. Not the valid-approval filter of approvals.md section 5 (S19): it only lets <c>jd approve</c> refuse a
    /// repeat and count, so a file that is not JSON, or lacks a field, is skipped.
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
                var body = JObject.Parse(await File.ReadAllTextAsync(path, cancellationToken));
                if ((string?)body.SelectToken("environment.name") == target.Name && (string?)body.SelectToken("release.setSha256") == setSha256
                    && (string?)body.SelectToken("qualifiedOn") == policy.QualifiedOn && (string?)body.SelectToken("identity.objectId") is { } approver)
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
