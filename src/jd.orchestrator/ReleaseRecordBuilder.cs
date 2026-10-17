using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using jd.core.identity;
using jd.resolver.environment;
using jd.resolver.graph;
using jd.resolver.release;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace jd.orchestrator;

/// <summary>
/// Builds the JSON of a release record (schemas/release-record.schema.json, docs/architecture/release-record.md) from what a deploy
/// produced. An allowlist: it reads only the fields named below and never <see cref="NodeReport.Outputs"/> or <see cref="NodeReport.Changes"/>
/// (change counts come from <see cref="NodeReport.Summary"/>), so a secret value cannot reach the record. Free text is limited to the
/// reports' messages and step details.
/// </summary>
public static class ReleaseRecordBuilder
{
    /// <param name="set">The set that was deployed; its workloads come out in deploy order.</param>
    /// <param name="setSha256">SHA-256 of the kept <c>set.yaml</c>.</param>
    /// <param name="environment">The environment deployed to.</param>
    /// <param name="graphs">The resolved graph of each workload.</param>
    /// <param name="runs">The workloads handled; a failure ends the list, the workloads after it were not started.</param>
    /// <param name="identity">The principal the run authenticated with, from the identity port.</param>
    /// <param name="startedAt">When the activity started.</param>
    /// <param name="finishedAt">When it finished.</param>
    /// <param name="deployElapsed">The time of the deploy walk alone; the IQ time is in <paramref name="iq"/>.</param>
    /// <param name="iq">The qualification run after the deploy; null when it did not run (a workload failed), which the record spells <c>notRun</c>.</param>
    public static string Build(
        ReleaseSet set, string setSha256, EnvironmentDescriptor environment, IReadOnlyList<ResolvedGraph> graphs, IReadOnlyList<WorkloadRun> runs,
        DeployerIdentity identity, DateTimeOffset startedAt, DateTimeOffset finishedAt, TimeSpan deployElapsed, IqResult? iq)
    {
        var failed = runs.FirstOrDefault(r => !r.Run.Succeeded);
        var record = Head(ReleaseRecordFile.DeployActivity, set, setSha256, environment, graphs, identity, startedAt, finishedAt, name => Workload(
            set.Workloads.Single(w => w.Name == name), graphs.Single(g => g.Workload == name), runs.FirstOrDefault(r => r.Workload == name), deploy: true));
        record["outcome"] = failed is null ? ReleaseRecordFile.SucceededOutcome : "failed";
        record["deploySeconds"] = Seconds(deployElapsed);
        record["iq"] = Iq(iq);
        record["notStarted"] = new JArray(set.Order.Where(name => runs.All(r => r.Workload != name)));
        if (failed is not null)
        {
            record["failure"] = new JObject { ["workload"] = failed.Workload, ["node"] = failed.Run.Nodes[^1].NodeId };
        }

        return record.ToString(Formatting.Indented) + "\n";
    }

    /// <summary>The record of <c>jd release qualify</c>: the pinned release and the IQ verdict; nothing was deployed, so there are no runs, no OQ and no deploy time. <c>iq</c> is what the qualification found.</summary>
    public static string BuildQualify(
        ReleaseSet set, string setSha256, EnvironmentDescriptor environment, IReadOnlyList<ResolvedGraph> graphs, DeployerIdentity identity,
        DateTimeOffset startedAt, DateTimeOffset finishedAt, IqResult iq)
    {
        var record = Head(ReleaseRecordFile.QualifyActivity, set, setSha256, environment, graphs, identity, startedAt, finishedAt, name => Workload(
            set.Workloads.Single(w => w.Name == name), graphs.Single(g => g.Workload == name), null, deploy: false));
        record["iq"] = Iq(iq);
        return record.ToString(Formatting.Indented) + "\n";
    }

    // The verdict and every check that ran; a qualification that did not run has no time and no checks.
    private static JObject Iq(IqResult? iq) => new()
    {
        ["status"] = iq is null ? "notRun" : iq.Passed ? ReleaseRecordFile.PassedStatus : "failed",
        ["elapsedSeconds"] = Seconds(iq?.Elapsed ?? TimeSpan.Zero),
        ["checks"] = new JArray((iq?.Checks ?? []).Select(c => new JObject
        {
            ["workload"] = c.Workload,
            ["kind"] = Name(c.Kind),
            ["subject"] = c.Subject,
            ["status"] = c.Passed ? ReleaseRecordFile.PassedStatus : "failed",
            ["detail"] = c.Failure,
        })),
    };

    // What every record has: when, who, where, which release, and the workloads as pinned (in deploy order) with their graphs.
    private static JObject Head(
        string activity, ReleaseSet set, string setSha256, EnvironmentDescriptor environment, IReadOnlyList<ResolvedGraph> graphs, DeployerIdentity identity,
        DateTimeOffset startedAt, DateTimeOffset finishedAt, Func<string, JObject> workload) => new()
        {
            ["kind"] = "ReleaseRecord",
            ["activity"] = activity,
            ["startedAt"] = Time(startedAt),
            ["finishedAt"] = Time(finishedAt),
            ["identity"] = new JObject { ["tenantId"] = identity.TenantId, ["clientId"] = identity.ClientId, ["objectId"] = identity.ObjectId },
            ["environment"] = new JObject
            {
                ["name"] = environment.Name,
                ["tier"] = environment.Tier,
                ["region"] = environment.Region,
                ["catalogVersion"] = graphs[0].CatalogVersion,
            },
            ["release"] = new JObject { ["label"] = set.Label, ["createdAt"] = Time(set.CreatedAt), ["setSha256"] = setSha256 },
            ["workloads"] = new JArray(set.Order.Select(workload)),
        };

    // A deploy adds the run (when the workload started) and its OQ; a qualification deployed nothing, so it has neither.
    private static JObject Workload(ReleaseWorkload workload, ResolvedGraph graph, WorkloadRun? run, bool deploy)
    {
        var entry = new JObject
        {
            ["name"] = workload.Name,
            ["team"] = workload.Team,
            ["definitionSha256"] = workload.DefinitionSha256,
            ["dependsOn"] = new JArray(workload.DependsOn),
            ["image"] = workload.Image,
            ["graph"] = new JObject
            {
                ["sha256"] = Hash(GraphJson.Serialize(graph)),
                ["nodes"] = new JArray(graph.Nodes.Select(n => new JObject
                {
                    ["id"] = n.Id,
                    ["template"] = n.Template,
                    ["stack"] = n.Stack,
                    ["phase"] = Name(n.Phase),
                    ["hash"] = n.Hash,
                })),
            },
        };
        if (!deploy)
        {
            return entry;
        }

        if (run is not null)
        {
            entry["run"] = new JObject { ["elapsedSeconds"] = Seconds(run.Elapsed), ["nodes"] = new JArray(run.Run.Nodes.Select(Node)) };
        }

        entry["oq"] = Oq(graph, run);
        return entry;
    }

    private static JObject Node(NodeReport node)
    {
        var report = new JObject
        {
            ["id"] = node.NodeId,
            ["outcome"] = Name(node.Outcome),
            ["changes"] = new JObject(node.Summary.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new JProperty(c.Key, c.Value))),
            ["message"] = node.Message,
            ["elapsedSeconds"] = Seconds(node.Elapsed),
        };
        if (node.Steps is { } steps)
        {
            report["steps"] = new JArray(steps.Select(s => new JObject
            {
                ["step"] = Name(s.Step),
                ["outcome"] = Name(s.Outcome),
                ["detail"] = s.Detail,
                ["elapsedSeconds"] = Seconds(s.Elapsed),
                ["attempts"] = s.Attempts,
            }));
        }

        return report;
    }

    // Read from the probe step of the runtime node; nothing is probed again. The contract is the mapping's probe, as resolved into the graph.
    private static JObject Oq(ResolvedGraph graph, WorkloadRun? run)
    {
        var runtime = graph.Nodes.Single(n => n.Phase == Phase.Runtime);
        var spec = runtime.Probe ?? throw new InvalidOperationException($"node '{runtime.Id}': the runtime mapping has no probe.");
        var probe = run?.Run.Nodes.FirstOrDefault(n => n.NodeId == runtime.Id)?.Steps?.FirstOrDefault(s => s.Step == Step.Probe);
        var oq = new JObject
        {
            ["status"] = probe?.Outcome switch { StepOutcome.Done => ReleaseRecordFile.PassedStatus, StepOutcome.Failed => "failed", StepOutcome.Skipped => ReleaseRecordFile.UnchangedStatus, _ => "notRun" },
            ["contract"] = new JObject { ["path"] = spec.Path, ["expectedStatus"] = spec.ExpectedStatus },
        };
        if (probe is { Outcome: StepOutcome.Done or StepOutcome.Failed })
        {
            oq["detail"] = probe.Detail;
            oq["attempts"] = probe.Attempts;
            oq["elapsedSeconds"] = Seconds(probe.Elapsed);
        }

        return oq;
    }

    private static string Time(DateTimeOffset time) => time.UtcDateTime.ToString(ReleaseRecordFile.DocumentTimeFormat, CultureInfo.InvariantCulture);

    private static double Seconds(TimeSpan elapsed) => Math.Round(elapsed.TotalSeconds, 3);

    // The enums' names in camel case: the record's spelling of every state.
    private static string Name<T>(T value)
        where T : Enum => $"{char.ToLowerInvariant(value.ToString()[0])}{value.ToString().AsSpan(1)}";

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
