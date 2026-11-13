namespace jd.resolver.tests;

// The literals the guard allows, by file (relative to src/). Section 2's exceptions stay; every other group holds today's literals, named after
// the slice Appendix A expects to remove them (for a literal that several slices remove from one file, the last of them). A slice deletes its
// group, and a stale entry fails, so the list only shrinks; S24g leaves section 2's exceptions and the prose false positives.
public sealed partial class LiteralGuardTests
{
    private static IReadOnlyCollection<(string File, string Literal)> Allowlist =>
        [.. new[] { SectionTwoExceptions, ProseFalsePositives, S24b3, S24b4, S24c1, S24c2, S24d, S24e, S24f }.SelectMany(Entries)];

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
            "catalog.version", "contract", "env.type", "environment-type.layer", "layer", "naming.allowed", "naming.pattern", "naming.rules", "node.config",
            "node.kind", "node.template", "part.exports", "part.nodes", "part.scope", "requirement.class", "roles", "runtime.part",
        ],
        ["jd.resolver/expansion/ConfigWalker.cs"] = ["workload.variables"], // the fn::entries source (Appendix A: concept 4)
        // the annotation keyword and the annotations it reads (each named once); JSON Schema's own keyword
        ["jd.resolver/SchemaIndex.cs"] = ["define", "fallback", "is", "message", "one", "properties", "ref", "scope", "select", "x-jd"],
        ["jd.resolver/expressions/BuiltIns.cs"] = ["hash", "id", "name", "workload"], // name() and its placeholders
        ["jd.resolver/expressions/ExpressionEvaluator.cs"] = ["image", "name", "port", "runtime", "team", "workload"], // expression roots
        ["jd.resolver/graph/ConfigJson.cs"] = ["name"], // an fn::entries item
        ["jd.resolver/graph/GraphBuilder.cs"] = ["config", "kind", "template"], // the node hash (H47)
        ["jd.resolver/graph/GraphJson.cs"] =
        [
            "catalogVersion", "config", "dependsOn", "environment", "expectedStatus", "exports", "fqdnOutput", "hash", "id", "intervalSeconds",
            "kind", "latestKey", "name", "nodes", "path", "phase", "probe", "release", "resourceIdOutput", "revisionKey", "revisionOutput", "scope",
            "stack", "suffixInput", "suffixOutput", "template", "timeoutSeconds", "trafficInput", "trafficOutput", "weightKey", "workload",
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

    private static readonly Dictionary<string, string[]> S24b4 = new()
    {
        ["jd.resolver/graph/ExportValues.cs"] = ["exports."],
        ["jd.resolver/graph/GraphBuilder.cs"] = ["exports."],
    };

    private static readonly Dictionary<string, string[]> S24c1 = new()
    {
        ["jd.resolver/environment/EnvironmentDefinition.cs"] = ["approval", "name", "offers", "region", "type"],
        ["jd.resolver/environment/EnvironmentDescriptor.cs"] = ["name", "region", "type"],
        ["jd.resolver/environment/EnvironmentParser.cs"] =
        [
            "approval", "approval.qualifiedOn", "approval.required", "name", "offers", "region", "type", "values", "values.",
        ],
        ["jd.resolver/environment/EnvironmentTypeCheck.cs"] = ["approval", "type"],
        ["jd.resolver/expansion/Expander.cs"] = ["offers", "offers["],
    };

    private static readonly Dictionary<string, string[]> S24c2 = new()
    {
        ["jd.definitionvalidator/WorkloadRules.cs"] = ["container", "container.variables.", "id", "requires", "requires[", "type", "variables"],
        ["jd.resolver/expansion/ConfigWalker.cs"] = ["container.variables"],
        ["jd.resolver/expansion/Expander.cs"] =
        [
            "class", "container", "id", "image", "metadata", "name", "port", "ports", "requires", "requires[", "team", "type", "variables",
        ],
        ["jd.resolver/policies/PolicyApplier.cs"] = ["container.variables", "requires"],
        ["jd.resolver/release/ReleaseBuilder.cs"] =
        [
            ".container.image", "].definition", "].dependsOn[", "container", "container.image", "definition", "dependsOn", "image", "label",
            "metadata", "name", "team", "workloads", "workloads[",
        ],
    };

    private static readonly Dictionary<string, string[]> S24d = new()
    {
        ["jd.resolver/catalog/MatchCriteria.cs"] = ["class", "environmentType", "kind", "runtime", "template", "type"],
        ["jd.resolver/catalog/CatalogParser.cs"] = ["default", "match", "match.kind", "match.runtime", "name", "reason", "require", "set"],
        ["jd.resolver/policies/PolicyApplier.cs"] = ["default", "set"],
    };

    private static readonly Dictionary<string, string[]> S24e = new()
    {
        ["jd.resolver/catalog/CatalogParser.cs"] =
        [
            "expectedStatus", "fqdnOutput", "intervalSeconds", "latestKey", "path", "probe", "release", "resourceIdOutput", "revisionKey",
            "revisionOutput", "suffixInput", "suffixOutput", "timeoutSeconds", "trafficInput", "trafficOutput", "weightKey",
        ],
    };

    private static readonly Dictionary<string, string[]> S24f = new()
    {
        ["jd.resolver/environment/DescriptorComposer.cs"] = ["approval", "name", "offers", "region", "type", "values", "values."],
        ["jd.resolver/release/ReleaseSetFile.cs"] = ["].definition", "createdAt", "definition", "definitionSha256", "dependsOn", "label", "workloads", "workloads["],
    };
}
