using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace jd.resolver.release;

/// <summary>
/// What the records of one label on one environment say (docs/architecture/approvals.md section 5, step 2).
/// </summary>
/// <param name="SetSha256">SHA-256 of the label's kept <c>set.yaml</c>; null when the label's set cannot be known: an unknown label (no <c>set.yaml</c>), or a <c>set.yaml</c> or label directory that cannot be read. The <paramref name="Reasons"/> tell which.</param>
/// <param name="Reasons">Why the label is not qualified, one entry per problem; empty when it is.</param>
/// <param name="Deployers">The distinct object ids of the principals that ran a <c>deploy</c> of the label, failed attempts included, from the records that passed the consistency checks.</param>
public sealed record Qualification(string? SetSha256, IReadOnlyList<string> Reasons, IReadOnlyList<string> Deployers)
{
    public bool Qualified => Reasons.Count == 0;
}

/// <summary>
/// Reads the records of a label and derives "qualified" from them now; nothing is stored or repaired. The records directory is the trust boundary
/// (approvals.md section 9): every file that is not what its place says is a reason, so a copied, renamed or edited record can only make the answer "not qualified".
/// </summary>
public static class QualificationReader
{
    private sealed record Entry(string Name, string Activity, JObject Body);

    private sealed record Expected(string Environment, string Label, string SetSha256);

    /// <summary>
    /// Judges label <paramref name="label"/> on <paramref name="environment"/> from <c>&lt;records&gt;/&lt;environment&gt;/&lt;label&gt;/</c>. Files that are not
    /// <c>&lt;time&gt;-deploy.json</c> or <c>&lt;time&gt;-qualify.json</c> (approvals, notes) are not records and are left alone. Throws <see cref="ArgumentException"/>
    /// for an environment or label that is not a single path segment.
    /// </summary>
    public static async Task<Qualification> ReadAsync(string recordsDirectory, string environment, string label, CancellationToken cancellationToken = default)
    {
        var directory = ReleaseRecordFile.LabelDirectory(recordsDirectory, environment, label);
        var setPath = Path.Combine(directory, ReleaseRecordFile.SetFileName);
        if (!File.Exists(setPath))
        {
            return new Qualification(null, [$"unknown label: '{setPath}' does not exist."], []);
        }

        string setSha256;
        string[] paths;
        try
        {
            setSha256 = ReleaseDocument.Sha256(await File.ReadAllBytesAsync(setPath, cancellationToken));
            paths = Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Qualification(null, [$"'{directory}' cannot be read: {ex.Message}"], []);
        }

        var expected = new Expected(environment, label, setSha256);
        var reasons = new List<string>();
        var records = new List<Entry>();
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (ReleaseRecordFile.SplitName(name) is not { } split)
            {
                continue;
            }

            var (body, problem) = await LoadAsync(path, name, split.Time, split.Activity, expected, cancellationToken);
            if (body is null)
            {
                reasons.Add($"{name}: {problem}");
            }
            else
            {
                records.Add(new Entry(name, split.Activity, body));
            }
        }

        // An inconsistent record is a failure by itself; judging the rest could only say less than the whole.
        if (reasons.Count == 0)
        {
            reasons.AddRange(Judge(records));
        }

        var deployers = records.Where(r => r.Activity == ReleaseRecordFile.DeployActivity).Select(r => (string)r.Body["identity"]!["objectId"]!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new Qualification(setSha256, reasons, deployers);
    }

    // The body when the file is a valid record that belongs here, else why not.
    private static async Task<(JObject? Body, string? Problem)> LoadAsync(string path, string name, string time, string activity, Expected expected, CancellationToken cancellationToken)
    {
        JObject body;
        try
        {
            using var reader = new JsonTextReader(new StringReader(await File.ReadAllTextAsync(path, cancellationToken))) { DateParseHandling = DateParseHandling.None };
            body = JObject.Load(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, $"cannot be read: {ex.Message}");
        }

        var errors = await ReleaseDocument.ValidateAsync(name, body, ReleaseDocument.RecordSchema, cancellationToken);
        var problem = errors.Count > 0
            ? $"is not a valid release record: {string.Join("; ", errors)}"
            : Misplaced(body, time, activity, expected);
        return (problem is null ? body : null, problem);
    }

    // A record copied from another label or environment, renamed to a later time, or edited to another set is caught here (approvals.md section 5).
    private static string? Misplaced(JObject body, string time, string activity, Expected expected)
    {
        if ((string?)body["activity"] is var actual && actual != activity)
        {
            return $"records activity '{actual}', not '{activity}' as its name says.";
        }

        if ((string?)body["environment"]!["name"] is var environment && environment != expected.Environment)
        {
            return $"is a record of environment '{environment}', not '{expected.Environment}'.";
        }

        if ((string?)body["release"]!["label"] is var label && label != expected.Label)
        {
            return $"is a record of label '{label}', not '{expected.Label}'.";
        }

        if ((string?)body["release"]!["setSha256"] is var pinned && pinned != expected.SetSha256)
        {
            return $"pins set {pinned}, but set.yaml hashes to {expected.SetSha256}.";
        }

        var finished = (string?)body["finishedAt"];
        return DateTimeOffset.TryParseExact(time, ReleaseRecordFile.NameTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var named)
            && named.UtcDateTime.ToString(ReleaseRecordFile.DocumentTimeFormat, CultureInfo.InvariantCulture) == finished
            ? null
            : $"finishedAt {finished} does not match the time '{time}' in its name.";
    }

    // The rule of approvals.md section 5, step 2, over records that are all consistent.
    private static IEnumerable<string> Judge(List<Entry> records)
    {
        var deploys = records.Where(r => r.Activity == ReleaseRecordFile.DeployActivity).ToList();
        if (deploys.Count == 0)
        {
            yield return "the label was never deployed here: there is no deploy record.";
            yield break;
        }

        var newest = deploys[^1];
        if ((string?)newest.Body["outcome"] != ReleaseRecordFile.SucceededOutcome)
        {
            yield return $"the newest deploy ({newest.Name}) failed.";
        }

        foreach (var workload in newest.Body["workloads"]!)
        {
            var name = (string)workload["name"]!;
            var status = (string)workload["oq"]!["status"]!;
            // An unchanged workload counts only on an earlier pass; the newest deploy itself says unchanged here, so it cannot be the pass.
            if (status != ReleaseRecordFile.PassedStatus && !(status == ReleaseRecordFile.UnchangedStatus && deploys.Any(d => PassedOq(d, name))))
            {
                yield return $"workload {name}: OQ is {status}{(status == ReleaseRecordFile.UnchangedStatus ? " and no earlier deploy passed it" : string.Empty)}.";
            }
        }

        // Every valid record carries an iq, so the newest record is also the newest one with an iq and is not older than the newest deploy.
        var last = records[^1];
        if ((string?)last.Body["iq"]!["status"] is var iq && iq != ReleaseRecordFile.PassedStatus)
        {
            yield return $"IQ of the newest record ({last.Name}) is {iq}.";
        }
    }

    private static bool PassedOq(Entry deploy, string workload) =>
        deploy.Body["workloads"]!.Any(w => (string?)w["name"] == workload && (string?)w["oq"]!["status"] == ReleaseRecordFile.PassedStatus);
}
