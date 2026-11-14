namespace jd.resolver.tests;

// The literals the guard allows, by file (relative to src/). Section 2's exceptions stay; every other group holds today's literals, named after
// the slice Appendix A expects to remove them (for a literal that several slices remove from one file, the last of them). A slice deletes its
// group, and a stale entry fails, so the list only shrinks; S24g leaves section 2's exceptions and the prose false positives.
public sealed partial class LiteralGuardTests
{
    private static IReadOnlyCollection<(string File, string Literal)> Allowlist =>
        [.. new[] { SectionTwoExceptions, ProseFalsePositives, S24b3, S24f }.SelectMany(Entries)];

    private static IEnumerable<(string File, string Literal)> Entries(Dictionary<string, string[]> group) =>
        group.SelectMany(entry => entry.Value.Select(literal => (entry.Key, literal)));

    // Documents section 2: external protocols (class b), the bootstrap field, concept and root names that equal a field, jd's identifiers and
    // node hash, and jd's evidence formats (choice 2 (b): the release record, the approval record and the graph JSON, written and read).
    private static readonly Dictionary<string, string[]> SectionTwoExceptions = new()
    {
        ["backend-providers/jd.bp.pulumi/TemplateLibrary.cs"] = ["default"], // Pulumi.yaml (choice 6)
        ["jd.azure/ArmDeployerIdentity.cs"] = ["https://management.azure.com/.default"], // ARM's token scope
        ["jd.cli/Cli.cs"] = ["release"], // a command
        ["jd.orchestrator/ConfigFiller.cs"] = ["name"], // an fn::entries item
        ["jd.orchestrator/ReleaseRecordBuilder.cs"] =
        [
            "activity", "attempts", "catalogVersion", "changes", "checks", "clientId", "contract", "createdAt", "definitionSha256", "dependsOn",
            "deploySeconds", "detail", "elapsedSeconds", "environment", "expectedStatus", "failure", "finishedAt", "graph", "hash", "id", "identity",
            "image", "iq", "kind", "label", "message", "name", "node", "nodes", "notStarted", "objectId", "oq", "outcome", "path", "phase", "region",
            "release", "run", "setSha256", "sha256", "stack", "startedAt", "status", "step", "steps", "subject", "team", "template", "tenantId",
            "type", "workload", "workloads",
        ],
        ["jd.resolver/DocumentLoader.cs"] = ["kind"], // the bootstrap field
        // the role names the engine reads (Appendix B), like the expression roots
        ["jd.resolver/Roles.cs"] =
        [
            "approval.approvers", "approval.qualified-on", "approval.required", "approval.separate", "catalog.version", "contract", "env.approval",
            "env.name", "env.offers", "env.region", "env.type", "env.values", "environment-type.layer", "layer", "naming.allowed", "naming.pattern",
            "naming.rules", "node.config", "node.kind", "node.template", "part.exports", "part.nodes", "part.scope", "policy.default", "policy.name",
            "policy.reason", "policy.require", "policy.runtime", "policy.scope", "policy.set", "probe.expected-status", "probe.interval", "probe.path",
            "probe.scheme", "probe.timeout", "requirement.class", "requirement.id", "requirement.type", "roles", "runtime.part", "workload.image",
            "workload.name", "workload.port", "workload.team", "workload.variables",
        ],
        ["jd.resolver/expansion/ConfigWalker.cs"] = ["workload.variables"], // the fn::entries source (Appendix A: concept 4)
        // the annotation keyword and the annotations it reads (each named once); JSON Schema's own keyword
        ["jd.resolver/SchemaIndex.cs"] = ["define", "fallback", "is", "match", "message", "one", "properties", "ref", "scope", "select", "x-jd"],
        ["jd.resolver/expressions/BuiltIns.cs"] = ["hash", "id", "name", "workload"], // name() and its placeholders
        ["jd.resolver/expressions/ExpressionEvaluator.cs"] = ["image", "name", "port", "region", "runtime", "team", "type", "workload"], // expression roots
        ["jd.resolver/graph/ConfigJson.cs"] = ["name"], // an fn::entries item
        ["jd.resolver/graph/GraphBuilder.cs"] = ["config", "kind", "template"], // the node hash (H47)
        ["jd.resolver/graph/GraphJson.cs"] =
        [
            "catalogVersion", "config", "configDigits", "dependsOn", "environment", "expectedStatus", "exports", "fqdnOutput", "fullWeight", "hash", "id",
            "intervalSeconds", "kind", "latestKey", "name", "nodes", "path", "phase", "prefix", "probe", "release", "resourceIdOutput", "revisionKey",
            "revisionOutput", "saltDigits", "scheme", "scope", "stack", "suffix", "suffixInput", "suffixOutput", "template", "timeoutSeconds", "trafficInput",
            "trafficOutput", "weightKey", "workload",
        ],
        ["jd.resolver/release/ApprovalRecord.cs"] = ["environment", "label", "qualifiedOn", "set"],
        ["jd.resolver/release/QualificationReader.cs"] =
        [
            "activity", "environment", "finishedAt", "identity", "iq", "label", "name", "objectId", "oq", "outcome", "release", "setSha256", "status",
            "workloads",
        ],
        ["jd.resolver/release/ReleaseRecordFile.cs"] = ["set.yaml"], // the local record store's layout (concept 11)
    };

    // Prose in a message that the guard's heuristic cannot tell apart from a field: a lone word is a literal without whitespace. Not an exception
    // of section 2; rewording the message would delete its entry.
    private static readonly Dictionary<string, string[]> ProseFalsePositives = new()
    {
        ["jd.azure/ArmCredentials.cs"] = ["is"], // "X is not set"; 'is' is also an x-jd keyword
    };

    private static readonly Dictionary<string, string[]> S24b3 = new()
    {
        ["backend-providers/jd.bp.pulumi/TemplateLibrary.cs"] = ["exports.", "template"],
    };

    private static readonly Dictionary<string, string[]> S24f = new()
    {
        ["jd.resolver/environment/DescriptorComposer.cs"] = ["approval", "name", "offers", "region", "type", "values", "values."],
        ["jd.resolver/release/ReleaseBuilder.cs"] = ["].definition", "].dependsOn[", "definition", "dependsOn", "label", "workloads", "workloads["], // the manifest
        ["jd.resolver/release/ReleaseSetFile.cs"] = ["].definition", "createdAt", "definition", "definitionSha256", "dependsOn", "label", "workloads", "workloads["],
    };
}
