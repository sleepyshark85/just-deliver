using jd.resolver.catalog;
using Xunit;

namespace jd.resolver.tests;

public class CatalogParserTests
{
    private const string CatalogFile = "kind: Catalog\nversion: \"1\"\n";

    private static string TypeFile(string name) => $"kind: ResourceType\nname: {name}\ndescription: test\nclasses: [standard]\nexports: [endpoint]\n";

    private static string MappingFile(string type) =>
        $"kind: Mapping\nmatch: {{ type: {type} }}\nnodes:\n  db:\n    template: t/db\n    config: {{ size: 1 }}\nexports:\n  endpoint: ${{db.endpoint}}\n";

    private static string PolicyFile(string name) =>
        $"kind: Policy\nname: {name}\nreason: because\nmatch: {{ kind: runtime }}\nset: {{ runtime.b: 1 }}\n";

    private static Task<CatalogLoadResult> Parse(params (string Path, string Content)[] files) =>
        CatalogParser.ParseAsync(files.Select(f => new CatalogSource(f.Path, f.Content)));

    [Fact]
    public async Task Valid_files_build_the_model()
    {
        var result = await Parse(
            ("catalog.yaml", CatalogFile),
            ("types/a.yaml", TypeFile("alpha-db")),
            ("maps/a.yaml", MappingFile("alpha-db")),
            ("policies/p.yaml", PolicyFile("p-one") + "add:\n  extra:\n    kind: grant\n    template: t/x\n    config: { k: v }\n"),
            ("naming.yaml", "kind: Naming\nrules:\n  thing:\n    pattern: \"{workload}-{hash}\"\n    maxLength: 20\n    allowed: \"[a-z0-9]\"\n"),
            ("roles.yaml", "kind: Roles\nroles:\n  reader: 00000000-0000-0000-0000-000000000001\n"));

        Assert.Empty(result.Errors);
        var catalog = Assert.IsType<Catalog>(result.Catalog);
        Assert.Equal("1", catalog.Version);
        Assert.Equal("alpha-db", Assert.Single(catalog.Types).Name);
        var mapping = Assert.Single(catalog.Mappings);
        Assert.Equal("maps/a.yaml", mapping.Source);
        Assert.Equal("alpha-db", mapping.Match.Criteria["type"]);
        Assert.Equal(NodeKind.Create, mapping.Nodes["db"].Kind);
        Assert.Equal(1, (int)mapping.Nodes["db"].Config["size"]);
        Assert.Equal("${db.endpoint}", mapping.Exports["endpoint"]);
        var policy = Assert.Single(catalog.Policies);
        Assert.Equal(NodeKind.Grant, policy.Add["extra"].Kind);
        Assert.True(policy.Set.ContainsKey("runtime.b"));
        Assert.Equal(20, catalog.Naming["thing"].MaxLength);
        Assert.Equal("00000000-0000-0000-0000-000000000001", catalog.Roles["reader"]);
    }

    [Fact]
    public async Task Unknown_kind_is_an_error_naming_the_file()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("odd.yaml", "kind: Widget\n"));

        var error = Assert.Single(result.Errors);
        Assert.Equal("odd.yaml", error.File);
        Assert.Equal("kind", error.Location);
        Assert.Contains("unknown kind 'Widget'", error.Message);
        Assert.Null(result.Catalog);
    }

    [Fact]
    public async Task Missing_kind_is_an_error()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("nokind.yaml", "name: x\n"));

        var error = Assert.Single(result.Errors);
        Assert.Equal("nokind.yaml", error.File);
        Assert.Equal("kind", error.Location);
    }

    [Fact]
    public async Task Missing_catalog_file_is_an_error()
    {
        var result = await Parse(("types/a.yaml", TypeFile("alpha-db")));

        Assert.Contains(result.Errors, e => e.Message.Contains("exactly one is required"));
    }

    [Fact]
    public async Task Duplicate_catalog_file_is_an_error_on_the_second_file()
    {
        var result = await Parse(("a.yaml", CatalogFile), ("b.yaml", CatalogFile));

        var error = Assert.Single(result.Errors);
        Assert.Equal("b.yaml", error.File);
        Assert.Contains("a.yaml", error.Message);
    }

    [Fact]
    public async Task Duplicate_type_name_is_an_error()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("types/a.yaml", TypeFile("alpha-db")), ("types/b.yaml", TypeFile("alpha-db")));

        var error = Assert.Single(result.Errors);
        Assert.Equal("types/b.yaml", error.File);
        Assert.Contains("'alpha-db' is already declared in types/a.yaml", error.Message);
    }

    [Fact]
    public async Task Duplicate_policy_name_is_an_error()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("p/a.yaml", PolicyFile("same")), ("p/b.yaml", PolicyFile("same")));

        var error = Assert.Single(result.Errors);
        Assert.Equal("p/b.yaml", error.File);
        Assert.Contains("'same' is already declared in p/a.yaml", error.Message);
    }

    [Theory]
    [InlineData("  endpoint: ${db.endpoint}\n  extra: ${db.extra}\n", "not in the type: extra")]
    [InlineData("  other: ${db.endpoint}\n", "missing endpoint")]
    public async Task Mapping_exports_must_equal_the_type_exports(string exports, string expected)
    {
        var mapping = "kind: Mapping\nmatch: { type: alpha-db }\nnodes:\n  db:\n    template: t/db\n    config: {}\nexports:\n" + exports;
        var result = await Parse(("catalog.yaml", CatalogFile), ("types/a.yaml", TypeFile("alpha-db")), ("maps/a.yaml", mapping));

        var error = Assert.Single(result.Errors);
        Assert.Equal("maps/a.yaml", error.File);
        Assert.Equal("exports", error.Location);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Mapping_for_an_undeclared_type_is_an_error()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("types/a.yaml", TypeFile("alpha-db")), ("maps/b.yaml", MappingFile("beta-db")));

        var error = Assert.Single(result.Errors);
        Assert.Equal("maps/b.yaml", error.File);
        Assert.Equal("match.type", error.Location);
        Assert.Contains("'beta-db'", error.Message);
        Assert.Contains("alpha-db", error.Message);
    }

    [Fact]
    public async Task Schema_violation_is_reported_with_file_and_location()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("maps/bad.yaml", "kind: Mapping\nmatch: { type: x }\nnodes:\n  db:\n    template: t\nexports: {}\n"));

        var error = Assert.Single(result.Errors);
        Assert.Equal("maps/bad.yaml", error.File);
        Assert.Equal("nodes.db", error.Location);
        Assert.Contains("PropertyRequired: #/nodes.db.config", error.Message);
        Assert.Contains("maps/bad.yaml", error.ToString());
    }

    [Fact]
    public async Task Policy_without_set_default_or_add_is_a_schema_violation()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("p.yaml", "kind: Policy\nname: pol-a\nreason: r\nmatch: { kind: runtime }\n"));

        Assert.Equal("p.yaml", Assert.Single(result.Errors).File);
    }

    [Theory]
    [InlineData("{ kind: grant }", "set: { a: 1 }", "match.kind")]
    [InlineData("{ runtime: container-app }", "set: { a: 1 }", "match.runtime")]
    [InlineData("{ kind: runtime, type: sqldb }", "set: { runtime.a: 1 }", "match")]
    [InlineData("{ kind: runtime, template: t/x }", "add:\n  n:\n    template: t/x\n    config: {}", "match")]
    [InlineData("{ template: t/x }", "add:\n  n:\n    template: t/x\n    config: {}", "add")]
    [InlineData("{ kind: runtime }", "set: { size: 1 }", "set.size")]
    [InlineData("{ kind: runtime }", "default: { size: 1 }", "default.size")]
    public async Task A_policy_that_fits_neither_scope_is_an_error(string match, string body, string location)
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("p.yaml", $"kind: Policy\nname: pol-a\nreason: r\nmatch: {match}\n{body}\n"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("p.yaml", location), (error.File, error.Location));
    }

    [Theory]
    [InlineData("{ template: t/x, tier: team }", "set: { a.b: 1 }\ndefault: { c: 2 }")]
    [InlineData("{ kind: runtime, runtime: container-app, tier: team }", "set: { runtime.a: 1 }\ndefault: { runtime.b: 2 }\nadd:\n  n:\n    template: t/x\n    config: {}")]
    public async Task A_policy_in_either_scope_loads(string match, string body)
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("p.yaml", $"kind: Policy\nname: pol-a\nreason: r\nmatch: {match}\n{body}\n"));

        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("set:\n  a:\n    b: 1\n  a.b: 2", "set.a")]
    [InlineData("default: { x.y: 1, x.y.z: 2 }", "default.x.y")]
    public async Task A_policy_whose_own_paths_overlap_is_an_error(string body, string location)
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("p.yaml", $"kind: Policy\nname: pol-a\nreason: r\nmatch: {{ template: t/x }}\n{body}\n"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("p.yaml", location), (error.File, error.Location));
        Assert.Contains("overlaps", error.Message);
    }

    [Fact]
    public async Task A_bare_runtime_path_is_rejected()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("p.yaml", "kind: Policy\nname: pol-a\nreason: r\nmatch: { kind: runtime }\nset: { runtime: 1 }\n"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("p.yaml", "set.runtime"), (error.File, error.Location));
    }

    [Theory]
    [InlineData("Db")]
    [InlineData("my_node")]
    [InlineData("a-")]
    [InlineData("1a")]
    [InlineData("a.b")]
    [InlineData("a@b")]
    public async Task Node_names_in_mappings_and_policy_adds_must_be_lowercase_kebab(string name)
    {
        var mapping = await Parse(("catalog.yaml", CatalogFile), ("m.yaml", $"kind: Mapping\nmatch: {{ kind: runtime }}\n{TestCatalog.ProbeAndRelease}nodes:\n  runtime:\n    template: t/x\n    config: {{}}\n  \"{name}\":\n    template: t/x\n    config: {{}}\n"));
        var policy = await Parse(("catalog.yaml", CatalogFile), ("p.yaml", $"kind: Policy\nname: pol-a\nreason: r\nmatch: {{ kind: runtime }}\nadd:\n  \"{name}\":\n    template: t/x\n    config: {{}}\n"));

        var mappingError = Assert.Single(mapping.Errors);
        Assert.Equal(("m.yaml", $"nodes.{name}"), (mappingError.File, mappingError.Location));
        Assert.Contains("not a valid node name", mappingError.Message);
        var policyError = Assert.Single(policy.Errors);
        Assert.Equal(("p.yaml", $"add.{name}"), (policyError.File, policyError.Location));
    }

    [Fact]
    public async Task Kebab_node_names_load()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("m.yaml", "kind: Mapping\nmatch: { kind: runtime }\n" + TestCatalog.ProbeAndRelease + "nodes:\n  runtime:\n    template: t/x\n    config: {}\n  app-insights-2:\n    template: t/x\n    config: {}\n"));

        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task A_mapping_must_match_a_type_or_a_kind()
    {
        const string body = "nodes:\n  db:\n    template: t/db\n    config: {}\nexports:\n  endpoint: x\n";
        var neither = await Parse(("catalog.yaml", CatalogFile), ("m.yaml", $"kind: Mapping\nmatch: {{ class: standard }}\n{body}"));
        var runtime = await Parse(("catalog.yaml", CatalogFile), ("m.yaml", "kind: Mapping\nmatch: { kind: runtime, runtime: container-app }\n" + TestCatalog.ProbeAndRelease + "nodes:\n  runtime:\n    template: t/db\n    config: {}\n"));

        var error = Assert.Single(neither.Errors);
        Assert.Equal(("m.yaml", "match"), (error.File, error.Location));
        Assert.Empty(runtime.Errors);
    }

    private const string RuntimeHead = "kind: Mapping\nmatch: { kind: runtime }\n";
    private const string RuntimeNode = "nodes:\n  runtime:\n    template: t/rt\n    config: {}\n";
    private const string Probe = "probe: { path: /health, expectedStatus: 200, timeoutSeconds: 60, intervalSeconds: 5 }\n";
    private const string Release = "release: { suffixInput: suffix, trafficInput: traffic, trafficOutput: traffic, revisionOutput: revision, fqdnOutput: fqdn, revisionKey: revisionName, latestKey: latestRevision, weightKey: weight }\n";

    [Fact]
    public async Task A_runtime_mapping_loads_with_its_probe_and_release_and_no_exports()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("m.yaml", RuntimeHead + Probe + Release + RuntimeNode));

        Assert.Empty(result.Errors);
        var mapping = Assert.Single(result.Catalog!.Mappings);
        Assert.True(mapping.IsRuntime);
        Assert.Equal((TestCatalog.Probe, TestCatalog.Release), (mapping.Probe, mapping.Release));
    }

    [Theory]
    [InlineData(RuntimeHead + Probe + Release + "nodes:\n  other:\n    template: t/rt\n    config: {}\n", "nodes", "must declare a node named 'runtime'")]
    [InlineData(RuntimeHead + Probe + Release + RuntimeNode + "exports:\n  endpoint: x\n", "exports", "has no exports")]
    [InlineData("kind: Mapping\nmatch: { type: alpha-db }\nnodes:\n  runtime:\n    template: t/rt\n    config: {}\nexports:\n  endpoint: x\n", "nodes.runtime", "declared only by a runtime mapping")]
    [InlineData("kind: Mapping\nmatch: { type: alpha-db }\n" + Probe + "nodes:\n  db:\n    template: t/db\n    config: {}\nexports:\n  endpoint: x\n", "probe", "only a runtime mapping has a probe")]
    [InlineData("kind: Mapping\nmatch: { type: alpha-db }\n" + Release + "nodes:\n  db:\n    template: t/db\n    config: {}\nexports:\n  endpoint: x\n", "release", "only a runtime mapping has a release block")]
    [InlineData(RuntimeHead + Probe + RuntimeNode, "release", "must have a release block")]
    [InlineData(RuntimeHead + Release + RuntimeNode, "probe", "must have a probe")]
    [InlineData(RuntimeHead + "probe: { path: /health, expectedStatus: 200, timeoutSeconds: 5, intervalSeconds: 6 }\n" + Release + RuntimeNode, "probe.intervalSeconds", "must not exceed timeoutSeconds")]
    [InlineData(RuntimeHead + Probe + Release + "nodes:\n  runtime:\n    template: t/rt\n    config: { suffix: x }\n", "nodes.runtime.config.suffix", "the release flow sets this input")]
    [InlineData(RuntimeHead + Probe + Release + "nodes:\n  runtime:\n    template: t/rt\n    config: { traffic: [] }\n", "nodes.runtime.config.traffic", "the release flow sets this input")]
    public async Task The_runtime_mapping_rules_are_enforced(string mapping, string location, string message)
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("types/a.yaml", TypeFile("alpha-db")), ("m.yaml", mapping));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("m.yaml", location), (error.File, error.Location));
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("{ path: health, expectedStatus: 200, timeoutSeconds: 60, intervalSeconds: 5 }", "probe.path")]
    [InlineData("{ path: /health, expectedStatus: 99, timeoutSeconds: 60, intervalSeconds: 5 }", "probe.expectedStatus")]
    [InlineData("{ path: /health, expectedStatus: 600, timeoutSeconds: 60, intervalSeconds: 5 }", "probe.expectedStatus")]
    [InlineData("{ path: /health, expectedStatus: ok, timeoutSeconds: 60, intervalSeconds: 5 }", "probe.expectedStatus")]
    [InlineData("{ path: /health, timeoutSeconds: 60, intervalSeconds: 5 }", "probe.expectedStatus")]
    [InlineData("{ expectedStatus: 200, timeoutSeconds: 60, intervalSeconds: 5 }", "probe.path")]
    [InlineData("{ path: /health, expectedStatus: 200, intervalSeconds: 5 }", "probe.timeoutSeconds")]
    [InlineData("{ path: /health, expectedStatus: 200, timeoutSeconds: 60 }", "probe.intervalSeconds")]
    [InlineData("{ path: /health, expectedStatus: 200, timeoutSeconds: 0, intervalSeconds: 5 }", "probe.timeoutSeconds")]
    [InlineData("{ path: /health, expectedStatus: 200, timeoutSeconds: 60, intervalSeconds: 0 }", "probe.intervalSeconds")]
    [InlineData("{ path: /health, expectedStatus: 200, timeoutSeconds: 60, intervalSeconds: 5, method: POST }", "probe.method")]
    public async Task A_bad_probe_is_an_error(string probe, string location)
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("m.yaml", RuntimeHead + $"probe: {probe}\n" + Release + RuntimeNode));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("m.yaml", location), (error.File, error.Location));
    }

    [Theory]
    [InlineData("suffixInput")]
    [InlineData("trafficInput")]
    [InlineData("trafficOutput")]
    [InlineData("revisionOutput")]
    [InlineData("fqdnOutput")]
    [InlineData("revisionKey")]
    [InlineData("latestKey")]
    [InlineData("weightKey")]
    public async Task A_release_block_missing_a_name_or_naming_it_empty_is_an_error(string name)
    {
        var without = Release.Replace($"{name}: ", $"x{name}: ");
        var empty = System.Text.RegularExpressions.Regex.Replace(Release, $"{name}: [A-Za-z]+", $"{name}: ''");

        var missing = await Parse(("catalog.yaml", CatalogFile), ("m.yaml", RuntimeHead + Probe + without + RuntimeNode));
        var blank = await Parse(("catalog.yaml", CatalogFile), ("m.yaml", RuntimeHead + Probe + empty + RuntimeNode));

        Assert.Contains(missing.Errors, e => e.Location == $"release.{name}" || e.Location == $"release.x{name}");
        Assert.Equal($"release.{name}", Assert.Single(blank.Errors).Location);
    }

    [Fact]
    public async Task A_policy_cannot_add_a_node_named_runtime()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("p.yaml", "kind: Policy\nname: pol-a\nreason: r\nmatch: { kind: runtime }\nadd:\n  runtime:\n    template: t/x\n    config: {}\n"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("p.yaml", "add.runtime"), (error.File, error.Location));
    }

    [Fact]
    public async Task Invalid_yaml_is_an_error()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("broken.yaml", "kind: [unclosed\n"));

        var error = Assert.Single(result.Errors);
        Assert.Equal("broken.yaml", error.File);
        Assert.Contains("not valid YAML", error.Message);
    }

    [Fact]
    public async Task Multi_document_file_is_an_error()
    {
        var result = await Parse(("catalog.yaml", CatalogFile + "---\n" + CatalogFile));

        var error = Assert.Single(result.Errors);
        Assert.Equal("catalog.yaml", error.File);
        Assert.Contains("one YAML document per file", error.Message);
    }

    [Fact]
    public async Task Repeated_key_is_an_error()
    {
        var result = await Parse(("catalog.yaml", CatalogFile + "version: \"2\"\n"));

        var error = Assert.Single(result.Errors);
        Assert.Equal("catalog.yaml", error.File);

        Assert.Contains("duplicate key 'version' at line 3", error.Message);
    }

    [Fact]
    public async Task Repeated_key_after_an_alias_is_located_correctly()
    {
        var result = await Parse(("catalog.yaml", "kind: Catalog\nversion: &v \"1\"\nref: *v\nx: 1\nx: 2\n"));

        Assert.Contains("duplicate key 'x' at line 5", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task Scanner_failure_is_an_error_not_an_exception()
    {
        var result = await Parse(("catalog.yaml", "kind: Naming\nrules: [x\nother: 1\n"));

        Assert.Contains("not valid YAML", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task Missing_catalog_is_reported_beside_an_unknown_kind()
    {
        var result = await Parse(("odd.yaml", "kind: Widget\n"));

        Assert.Contains(result.Errors, e => e.File == "odd.yaml" && e.Message.Contains("unknown kind"));
        Assert.Contains(result.Errors, e => e.Message.Contains("exactly one is required"));
    }

    [Fact]
    public async Task Missing_catalog_is_not_reported_when_a_file_is_unreadable()
    {
        var result = await Parse(("broken.yaml", "kind: [unclosed\n"));

        Assert.DoesNotContain(result.Errors, e => e.Message.Contains("exactly one is required"));
    }

    [Theory]
    [InlineData("[z-a]", "not a valid regex")]
    [InlineData("[a-z-]", "rejects 0123456789")]
    [InlineData("[a-z0-9]]", "rejects")]
    public async Task Naming_allowed_must_be_a_class_that_accepts_the_hash_digits(string allowed, string message)
    {
        var naming = $"kind: Naming\nrules:\n  thing:\n    pattern: \"{{id}}\"\n    maxLength: 20\n    allowed: \"{allowed}\"\n";
        var result = await Parse(("catalog.yaml", CatalogFile), ("naming.yaml", naming));

        var error = Assert.Single(result.Errors);
        Assert.Equal("naming.yaml", error.File);
        Assert.Equal("rules.thing.allowed", error.Location);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task Schema_invalid_catalog_file_still_counts_as_the_catalog()
    {
        var result = await Parse(("catalog.yaml", "kind: Catalog\nversion: 1\n"), ("types/a.yaml", TypeFile("alpha-db")));

        var error = Assert.Single(result.Errors);
        Assert.Equal("catalog.yaml", error.File);
        Assert.Contains("schema violation", error.Message);
    }

    [Fact]
    public async Task Schema_invalid_type_file_still_declares_its_name()
    {
        // The type fails its schema (no description) but its name is known, so the mapping is not blamed.
        var result = await Parse(
            ("catalog.yaml", CatalogFile),
            ("types/a.yaml", "kind: ResourceType\nname: alpha-db\nclasses: [standard]\nexports: [endpoint]\n"),
            ("maps/a.yaml", MappingFile("alpha-db")));

        var error = Assert.Single(result.Errors);
        Assert.Equal("types/a.yaml", error.File);
    }

    [Fact]
    public async Task Same_naming_rule_or_role_in_two_files_is_an_error()
    {
        const string role = "kind: Roles\nroles:\n  reader: 00000000-0000-0000-0000-000000000001\n";
        var result = await Parse(("catalog.yaml", CatalogFile), ("r1.yaml", role), ("r2.yaml", role));

        var error = Assert.Single(result.Errors);
        Assert.Equal("r2.yaml", error.File);
        Assert.Equal("roles.reader", error.Location);
    }

    [Fact]
    public async Task All_errors_are_reported_together()
    {
        var result = await Parse(
            ("types/a.yaml", TypeFile("alpha-db")),
            ("types/b.yaml", TypeFile("alpha-db")),
            ("maps/c.yaml", MappingFile("gamma-db")),
            ("odd.yaml", "kind: Widget\n"),
            ("roles.yaml", "kind: Roles\nroles:\n  reader: not-a-guid\n"));

        var files = result.Errors.Select(e => e.File).ToHashSet();
        Assert.Contains("types/b.yaml", files);
        Assert.Contains("maps/c.yaml", files);
        Assert.Contains("odd.yaml", files);
        Assert.Contains("roles.yaml", files);
    }
}
