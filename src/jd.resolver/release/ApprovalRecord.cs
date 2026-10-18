using System.Globalization;
using jd.core.identity;
using jd.core.records;
using jd.resolver.environment;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace jd.resolver.release;

/// <summary>
/// The approval record written by <c>jd approve</c> (schemas/approval-record.schema.json, docs/architecture/approvals.md section 4), and the reader of the
/// approval files that <c>jd approve</c> and <c>jd promote</c> share. The record is built from an allowlist: the principal comes from the identity port and the meaning is
/// one string value, so nothing the approver types can add or change a field.
/// </summary>
public static class ApprovalRecord
{
    // The record's fields, in file order, camelCase as the schema spells them: Build writes it and ReadAsync reads it (after the schema has vouched for it), so the keys are named once.
    private sealed record Stored(string Kind, string ApprovedAt, StoredEnvironment Environment, StoredRelease Release, string QualifiedOn, StoredIdentity Identity, string Meaning);

    // Type is null in a record written before environment types (it has a tier, which nothing reads).
    private sealed record StoredEnvironment(string Name, string? Type);

    private sealed record StoredRelease(string Label, string SetSha256);

    private sealed record StoredIdentity(string TenantId, string ClientId, string ObjectId);

    private sealed record Expected(string Environment, string Label, string SetSha256, string QualifiedOn);

    private static readonly JsonSerializerSettings Settings = new() { ContractResolver = new CamelCasePropertyNamesContractResolver(), DateParseHandling = DateParseHandling.None };

    private static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

    /// <summary>The record of <paramref name="identity"/> approving the set <paramref name="setSha256"/> of <paramref name="label"/> for the gated <paramref name="target"/>.</summary>
    public static string Build(EnvironmentDescriptor target, string label, string setSha256, DeployerIdentity identity, string meaning, DateTimeOffset approvedAt) =>
        JsonConvert.SerializeObject(
            new Stored(
                "ApprovalRecord",
                approvedAt.UtcDateTime.ToString(ReleaseRecordFile.DocumentTimeFormat, CultureInfo.InvariantCulture),
                new StoredEnvironment(target.Name, target.Type),
                new StoredRelease(label, setSha256),
                Policy(target).QualifiedOn,
                new StoredIdentity(identity.TenantId, identity.ClientId, identity.ObjectId),
                meaning),
            Formatting.Indented,
            Settings) + "\n";

    /// <summary>
    /// Reads the approval files of <paramref name="label"/> on <paramref name="target"/> (approvals.md section 5, step 3). A file counts only when it is valid against the
    /// schema and names this environment, this label, <paramref name="setSha256"/> and the <c>qualifiedOn</c> of <paramref name="target"/>'s policy. Any other file is
    /// ignored and reported, never a failure: an approval can only add, so ignoring one is safe (qualifying records fail closed instead). Throws
    /// <see cref="ArgumentException"/> when <paramref name="target"/> has no approval policy or the label is not a single path segment.
    /// </summary>
    public static async Task<ApprovalReading> ReadAsync(
        IRecordStore records, EnvironmentDescriptor target, string label, string setSha256, CancellationToken cancellationToken = default)
    {
        var policy = Policy(target);
        IReadOnlyList<string> names;
        try
        {
            names = await records.ListAsync(target.Name, label, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ApprovalReading([], [$"the records of label {label} on {target.Name} cannot be read: {ex.Message}"]);
        }

        var expected = new Expected(target.Name, label, setSha256, policy.QualifiedOn);
        var approvers = new List<string>();
        var ignored = new List<string>();
        foreach (var name in names.Where(n => n.EndsWith(ReleaseRecordFile.NameEnd(ReleaseRecordFile.ApproveActivity), StringComparison.Ordinal)))
        {
            var (stored, problem) = await LoadAsync(records, name, expected, cancellationToken);
            if (stored is null)
            {
                ignored.Add($"{name}: {problem}");
            }
            else
            {
                approvers.Add(stored.Identity.ObjectId);
            }
        }

        return new ApprovalReading(approvers.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ignored);
    }

    // The approval when the file is a valid one that belongs here, else why it is ignored.
    private static async Task<(Stored? Approval, string? Problem)> LoadAsync(IRecordStore records, string name, Expected expected, CancellationToken cancellationToken)
    {
        if (ReleaseRecordFile.TimeOf(name, ReleaseRecordFile.ApproveActivity) is not { } time || !ReleaseRecordFile.IsNameTime(time))
        {
            return (null, $"is not named <time>-{ReleaseRecordFile.ApproveActivity}.json with a time like 20261017T030405123Z.");
        }

        JObject body;
        try
        {
            var text = await ReleaseRecordFile.ReadTextAsync(records, expected.Environment, expected.Label, name, cancellationToken);
            using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
            body = JObject.Load(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, $"cannot be read: {ex.Message}");
        }

        var errors = await ReleaseDocument.ValidateAsync(name, body, ReleaseDocument.ApprovalSchema, cancellationToken);
        if (errors.Count > 0)
        {
            return (null, $"is not a valid approval record: {string.Join("; ", errors)}");
        }

        var stored = body.ToObject<Stored>(Serializer)!;
        foreach (var (field, actual, wanted) in new[]
        {
            ("environment", stored.Environment.Name, expected.Environment),
            ("label", stored.Release.Label, expected.Label),
            ("set", stored.Release.SetSha256, expected.SetSha256),
            ("qualifiedOn", stored.QualifiedOn, expected.QualifiedOn),
        })
        {
            if (actual != wanted)
            {
                return (null, $"approves {field} '{actual}', not '{wanted}'.");
            }
        }

        return (stored, null);
    }

    internal static ApprovalPolicy Policy(EnvironmentDescriptor target) =>
        target.Approval ?? throw new ArgumentException($"environment '{target.Name}' has no approval policy, so nothing can be approved for it.", nameof(target));
}

/// <summary>The approval files of one label on a gated environment, as <see cref="ApprovalRecord.ReadAsync"/> read them.</summary>
/// <param name="Approvers">The distinct object ids (case-insensitive) in the approvals that count as given for this set; whether each may approve is the policy's.</param>
/// <param name="Ignored">One entry per approval file that was not counted, naming the file and why.</param>
public sealed record ApprovalReading(IReadOnlyList<string> Approvers, IReadOnlyList<string> Ignored);
