using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace jd.resolver.release;

/// <summary>
/// What the records of one label on one environment say (docs/architecture/approvals.md section 5, step 2).
/// </summary>
/// <param name="SetSha256">SHA-256 of the label's kept <c>set.yaml</c>; null when there is none to read (an unknown label).</param>
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
    private const string Deploy = "deploy";
    private const string RecordTimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private static readonly string[] Activities = [Deploy, "qualify"];

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
        try
        {
            setSha256 = ReleaseDocument.Sha256(await File.ReadAllBytesAsync(setPath, cancellationToken));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Qualification(null, [$"'{setPath}' cannot be read: {ex.Message}"], []);
        }

        var expected = new Expected(environment, label, setSha256);
        var reasons = new List<string>();
        var records = new List<Entry>();
        foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            var activity = Activities.FirstOrDefault(a => name.EndsWith($"-{a}.json", StringComparison.Ordinal));
            if (activity is null)
            {
                continue;
            }

            var (body, problem) = await LoadAsync(path, name, activity, expected, cancellationToken);
            if (body is null)
            {
                reasons.Add($"{name}: {problem}");
            }
            else
            {
                records.Add(new Entry(name, activity, body));
            }
        }

        // An inconsistent record is a failure by itself; judging the rest could only say less than the whole.
        if (reasons.Count == 0)
        {
            reasons.AddRange(Judge(records));
        }

        var deployers = records.Where(r => r.Activity == Deploy).Select(r => (string)r.Body["identity"]!["objectId"]!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new Qualification(setSha256, reasons, deployers);
    }

    // The body when the file is a valid record that belongs here, else why not. The time of a name is the part before "-<activity>.json".
    private static async Task<(JObject? Body, string? Problem)> LoadAsync(string path, string name, string activity, Expected expected, CancellationToken cancellationToken)
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

        var errors = await ReleaseDocument.ValidateAsync(path, body, ReleaseDocument.RecordSchema, cancellationToken);
        var problem = errors.Count > 0
            ? $"is not a valid release record: {string.Join("; ", errors)}"
            : Misplaced(body, name[..^(activity.Length + "-.json".Length)], activity, expected);
        return (problem is null ? body : null, problem);
    }

    // A record copied from another label or environment, renamed to a later time, or edited to another set is caught here (approvals.md section 5).
    private static string? Misplaced(JObject body, string time, string activity, Expected expected)
    {
        if ((string?)body["activity"] != activity)
        {
            return $"records activity '{(string?)body["activity"]}', not '{activity}' as its name says.";
        }

        if ((string?)body["environment"]!["name"] != expected.Environment)
        {
            return $"is a record of environment '{(string?)body["environment"]!["name"]}', not '{expected.Environment}'.";
        }

        if ((string?)body["release"]!["label"] != expected.Label)
        {
            return $"is a record of label '{(string?)body["release"]!["label"]}', not '{expected.Label}'.";
        }

        if ((string?)body["release"]!["setSha256"] != expected.SetSha256)
        {
            return $"pins set {(string?)body["release"]!["setSha256"]}, but set.yaml hashes to {expected.SetSha256}.";
        }

        var finished = (string?)body["finishedAt"];
        return DateTimeOffset.TryParseExact(time, ReleaseRecordFile.TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var named)
            && named.UtcDateTime.ToString(RecordTimeFormat, CultureInfo.InvariantCulture) == finished
            ? null
            : $"finishedAt {finished} does not match the time '{time}' in its name.";
    }

    // The rule of approvals.md section 5, step 2, over records that are all consistent.
    private static IEnumerable<string> Judge(List<Entry> records)
    {
        var deploys = records.Where(r => r.Activity == Deploy).ToList();
        if (deploys.Count == 0)
        {
            yield return "the label was never deployed here: there is no deploy record.";
            yield break;
        }

        var newest = deploys[^1];
        if ((string?)newest.Body["outcome"] != "succeeded")
        {
            yield return $"the newest deploy ({newest.Name}) failed.";
        }

        foreach (var workload in newest.Body["workloads"]!)
        {
            var name = (string)workload["name"]!;
            var status = (string)workload["oq"]!["status"]!;
            // An unchanged workload counts only on an earlier pass; the newest deploy itself says unchanged here, so it cannot be the pass.
            if (status != "passed" && !(status == "unchanged" && deploys.Any(d => PassedOq(d, name))))
            {
                yield return $"workload {name}: OQ is {status}{(status == "unchanged" ? " and no earlier deploy passed it" : string.Empty)}.";
            }
        }

        // Every valid record carries an iq, so the newest record is also the newest one with an iq and is not older than the newest deploy.
        var last = records[^1];
        if ((string?)last.Body["iq"]!["status"] != "passed")
        {
            yield return $"IQ of the newest record ({last.Name}) is {(string?)last.Body["iq"]!["status"]}.";
        }
    }

    private static bool PassedOq(Entry deploy, string workload) =>
        deploy.Body["workloads"]!.Any(w => (string?)w["name"] == workload && (string?)w["oq"]!["status"] == "passed");
}
