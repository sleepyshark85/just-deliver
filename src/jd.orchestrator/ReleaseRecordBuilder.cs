using System.Globalization;
using System.Text;
using jd.resolver;
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
    /// <summary>The record of <c>jd release deploy</c>: the head, each workload's run and OQ, the outcome, the deploy time and the IQ verdict.</summary>
    /// <param name="head">When, who, where and which release.</param>
    /// <param name="runs">The workloads handled; a failure ends the list, the workloads after it were not started.</param>
    /// <param name="deployElapsed">The time of the deploy walk alone; the IQ time is in <paramref name="iq"/>.</param>
    /// <param name="iq">The qualification run after the deploy; null when it did not run (a workload failed), which the record spells <c>notRun</c>.</param>
    public static string Build(ReleaseRecordHead head, IReadOnlyList<WorkloadRun> runs, TimeSpan deployElapsed, IqResult? iq)
    {
        var (set, graphs) = (head.Set, head.Graphs);
        var failed = runs.FirstOrDefault(r => !r.Run.Succeeded);
        var record = Head(ReleaseRecordFile.DeployActivity, head, name => DeployedWorkload(
            set.Workloads.Single(w => w.Name == name), graphs.Single(g => g.Workload == name), runs.FirstOrDefault(r => r.Workload == name)));
        record["outcome"] = failed is null ? ReleaseRecordFile.SucceededOutcome : ReleaseRecordFile.FailedOutcome;
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
    public static string BuildQualify(ReleaseRecordHead head, IqResult iq)
    {
        var record = Head(ReleaseRecordFile.QualifyActivity, head, name => Workload(
            head.Set.Workloads.Single(w => w.Name == name), head.Graphs.Single(g => g.Workload == name)));
        record["iq"] = Iq(iq);
        return record.ToString(Formatting.Indented) + "\n";
    }

    // The verdict and every check that ran; a qualification that did not run has no time and no checks.
    private static JObject Iq(IqResult? iq) => new()
    {
        ["status"] = iq is null ? RecordStatus.NotRun : iq.Passed ? RecordStatus.Passed : RecordStatus.Failed,
        ["elapsedSeconds"] = Seconds(iq?.Elapsed ?? TimeSpan.Zero),
        ["checks"] = new JArray((iq?.Checks ?? []).Select(c => new JObject
        {
            ["workload"] = c.Workload,
            ["kind"] = Name(c.Kind),
            ["subject"] = c.Subject,
            ["status"] = c.Passed ? RecordStatus.Passed : RecordStatus.Failed,
            ["detail"] = c.Failure,
        })),
    };

    // What every record has: when, who, where, which release, and the workloads as pinned (in deploy order) with their graphs.
    private static JObject Head(string activity, ReleaseRecordHead head, Func<string, JObject> workload) => new()
    {
        [DocumentLoader.KindField] = ReleaseRecordFile.Kind,
        ["activity"] = activity,
        ["startedAt"] = Time(head.StartedAt),
        ["finishedAt"] = Time(head.FinishedAt),
        ["identity"] = new JObject { ["tenantId"] = head.Identity.TenantId, ["clientId"] = head.Identity.ClientId, ["objectId"] = head.Identity.ObjectId },
        ["environment"] = new JObject
        {
            ["name"] = head.Environment.Name,
            ["type"] = head.Environment.Type,
            ["region"] = head.Environment.Region,
            ["catalogVersion"] = head.Graphs[0].CatalogVersion,
        },
        ["release"] = new JObject { ["label"] = head.Set.Label, ["createdAt"] = Time(head.Set.CreatedAt), ["setSha256"] = head.SetSha256 },
        ["workloads"] = new JArray(head.Set.Order.Select(workload)),
    };

    // A workload as pinned, with its graph: all a qualification records of it, as it deployed nothing.
    private static JObject Workload(ReleaseWorkload workload, ResolvedGraph graph) => new()
    {
        ["name"] = workload.Name,
        ["team"] = workload.Team,
        ["definitionSha256"] = workload.DefinitionSha256,
        ["dependsOn"] = new JArray(workload.DependsOn),
        ["image"] = workload.Image,
        ["graph"] = new JObject
        {
            ["sha256"] = ReleaseHash.Of(Encoding.UTF8.GetBytes(GraphJson.Serialize(graph))),
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

    // A deploy adds the run (when the workload started) and its OQ.
    private static JObject DeployedWorkload(ReleaseWorkload workload, ResolvedGraph graph, WorkloadRun? run)
    {
        var entry = Workload(workload, graph);
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

    // Read from the probe step of the runtime node; nothing is probed again. The contract is the runtime's probe, as resolved into the graph.
    private static JObject Oq(ResolvedGraph graph, WorkloadRun? run)
    {
        var runtime = graph.Nodes.Single(n => n.Phase == Phase.Runtime);
        var spec = runtime.Probe ?? throw new InvalidOperationException($"node '{runtime.Id}': the runtime has no probe.");
        var probe = run?.Run.Nodes.FirstOrDefault(n => n.NodeId == runtime.Id)?.Steps?.FirstOrDefault(s => s.Step == Step.Probe);
        var oq = new JObject
        {
            ["status"] = probe?.Outcome switch { StepOutcome.Done => RecordStatus.Passed, StepOutcome.Failed => RecordStatus.Failed, StepOutcome.Skipped => RecordStatus.Unchanged, _ => RecordStatus.NotRun },
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
}
