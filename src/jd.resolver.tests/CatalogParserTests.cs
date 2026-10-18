using jd.resolver.catalog;
using Xunit;

namespace jd.resolver.tests;

public class CatalogParserTests
{
    private const string CatalogFile = "kind: Catalog\nversion: \"1\"\nenvironmentTypes: { dev: {}, stage: { approval: true } }\n";

    private static string ComponentFile(string name, string exports = "  endpoint: ${db.endpoint}\n", string key = "default") =>
        TestCatalog.Component(name, "[endpoint]", $"nodes:\n  db:\n    template: t/db\n    config: {{ size: 1 }}\nexports:\n{exports}", key);

    private static string PolicyFile(string name) =>
        $"kind: Policy\nname: {name}\nreason: because\nmatch: {{ kind: runtime }}\nset: {{ runtime.b: 1 }}\n";

    private static Task<CatalogLoadResult> Parse(params (string Path, string Content)[] files) =>
        CatalogParser.ParseAsync(files.Select(f => new CatalogSource(f.Path, f.Content)));

    // The environment types (docs/architecture/components.md section 2): declared in the Catalog file, named like requirement types, `default`
    // excluded (the implementations' fallback key).
    [Theory]
    [InlineData("", "catalog.yaml: environmentTypes: ")]
    [InlineData("environmentTypes: {}\n", "catalog.yaml: environmentTypes: ")]
    [InlineData("environmentTypes: { default: {} }\n", "catalog.yaml: environmentTypes.default: ")]
    [InlineData("environmentTypes: { Dev: {} }\n", "catalog.yaml: environmentTypes.Dev: ")]
    [InlineData("environmentTypes: { dev: { approval: \"yes\" } }\n", "BooleanExpected: #/environmentTypes.dev.approval")]
    public async Task Environment_types_are_required_and_well_formed(string declaration, string error)
    {
        var result = await Parse(("catalog.yaml", "kind: Catalog\nversion: \"1\"\n" + declaration));

        Assert.Null(result.Catalog);
        Assert.Contains(error, Assert.Single(result.Errors).ToString());
    }

    [Fact]
    public async Task Valid_files_build_the_model()
    {
        var result = await Parse(
            ("catalog.yaml", CatalogFile),
            ("components/a.yaml", ComponentFile("alpha-db")),
            ("runtimes/r.yaml", TestCatalog.RuntimeFile),
            ("policies/p.yaml", PolicyFile("p-one") + "add:\n  extra:\n    kind: grant\n    template: t/x\n    config: { k: v }\n"),
            ("naming.yaml", "kind: Naming\nrules:\n  thing:\n    pattern: \"{workload}-{hash}\"\n    maxLength: 20\n    allowed: \"[a-z0-9]\"\n"),
            ("roles.yaml", "kind: Roles\nroles:\n  reader: 00000000-0000-0000-0000-000000000001\n"));

        Assert.Empty(result.Errors);
        var catalog = Assert.IsType<Catalog>(result.Catalog);
        Assert.Equal("1", catalog.Version);
        Assert.Equal([("dev", false), ("stage", true)], catalog.EnvironmentTypes.Select(t => (t.Key, t.Value.Approval)).OrderBy(t => t.Key, StringComparer.Ordinal));
        var component = Assert.Single(catalog.Components).Value;
        Assert.Equal(("alpha-db", "components/a.yaml"), (component.Name, component.Source));
        Assert.Equal(["endpoint"], component.Exports);
        var part = Assert.Single(Assert.Single(component.Implementations, i => i.Key == "default").Value.Classes, c => c.Key == "standard").Value;
        Assert.Equal(NodeKind.Create, part.Nodes["db"].Kind);
        Assert.Equal(1, (int)part.Nodes["db"].Config["size"]);
        Assert.Equal("${db.endpoint}", part.Exports["endpoint"]);
        var runtime = Assert.Single(catalog.Runtimes).Value;
        var implementation = Assert.Single(runtime.Implementations, i => i.Key == "default").Value;
        Assert.Equal(("container-app", "runtimes/r.yaml", TestCatalog.Probe, TestCatalog.Release), (runtime.Name, runtime.Source, implementation.Probe, implementation.Release));
        Assert.Equal("t/runtime", implementation.Nodes["runtime"].Template);
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

    [Theory]
    [InlineData("ResourceType", "a Component file holds the type's contract (exports) beside its implementations")]
    [InlineData("Mapping", "a Component's implementations (per environment type, then class) replace a type's mappings, and a Runtime file the runtime mapping")]
    public async Task An_old_kind_is_an_error_saying_what_replaced_it(string kind, string replacement)
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("old.yaml", $"kind: {kind}\nname: alpha-db\n"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("old.yaml", "kind"), (error.File, error.Location));
        Assert.Equal($"kind '{kind}' is no longer supported: {replacement}.", error.Message);
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
        var result = await Parse(("components/a.yaml", ComponentFile("alpha-db")));

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

    [Theory]
    [InlineData("components/b.yaml")]
    [InlineData("runtimes/b.yaml")]
    public async Task Components_and_runtimes_share_one_namespace(string second)
    {
        var duplicate = second.StartsWith("runtimes/", StringComparison.Ordinal) ? TestCatalog.RuntimeFile.Replace("container-app", "alpha-db", StringComparison.Ordinal) : ComponentFile("alpha-db");
        var result = await Parse(("catalog.yaml", CatalogFile), ("components/a.yaml", ComponentFile("alpha-db")), (second, duplicate));

        var error = Assert.Single(result.Errors);
        Assert.Equal((second, "name"), (error.File, error.Location));
        Assert.Equal("'alpha-db' is already declared in components/a.yaml.", error.Message);
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
    [InlineData("default", "  endpoint: ${db.endpoint}\n  extra: ${db.extra}\n", "not in the contract: extra")]
    [InlineData("stage", "  other: ${db.endpoint}\n", "missing endpoint; not in the contract: other")]
    [InlineData("default", "  {}\n", "missing endpoint")]
    public async Task Every_class_of_every_implementation_must_export_exactly_the_contract(string key, string exports, string problem)
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("components/a.yaml", ComponentFile("alpha-db", exports, key)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("components/a.yaml", $"implementations.{key}.classes.standard.exports"), (error.File, error.Location));
        Assert.Equal($"must export exactly the contract of component 'alpha-db' (endpoint); {problem}.", error.Message);
        Assert.Null(result.Catalog);
    }

    [Theory]
    [InlineData("components/a.yaml")]
    [InlineData("runtimes/r.yaml")]
    public async Task An_implementation_keyed_by_an_undeclared_environment_type_is_an_error(string file)
    {
        var content = file.StartsWith("runtimes/", StringComparison.Ordinal) ? TestCatalog.Runtime("nodes:\n  runtime: { template: t/rt, config: {} }\n", "stagee") : ComponentFile("alpha-db", key: "stagee");
        var result = await Parse(("catalog.yaml", CatalogFile), (file, content));

        var error = Assert.Single(result.Errors);
        Assert.Equal((file, "implementations.stagee"), (error.File, error.Location));
        Assert.Equal("'stagee' is not a declared environment type (declared: dev, stage).", error.Message);
    }

    [Fact]
    public async Task Implementations_with_different_classes_are_an_error()
    {
        const string part = "{ nodes: { db: { template: t/db, config: {} } }, exports: { endpoint: x } }";
        var component = "kind: Component\nname: alpha-db\ndescription: d\nexports: [endpoint]\nimplementations:\n"
            + $"  default:\n    classes: {{ standard: {part} }}\n  stage:\n    classes: {{ standard: {part}, isolated: {part} }}\n";
        var result = await Parse(("catalog.yaml", CatalogFile), ("components/a.yaml", component));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("components/a.yaml", "implementations.stage.classes"), (error.File, error.Location));
        Assert.Equal("declares standard, isolated; every implementation must declare the classes of 'default' (standard).", error.Message);
    }

    [Fact]
    public async Task A_later_implementation_missing_a_class_of_the_first_is_an_error()
    {
        const string part = "{ nodes: { db: { template: t/db, config: {} } }, exports: { endpoint: x } }";
        var component = "kind: Component\nname: alpha-db\ndescription: d\nexports: [endpoint]\nimplementations:\n"
            + $"  default:\n    classes: {{ standard: {part}, isolated: {part} }}\n  stage:\n    classes: {{ standard: {part} }}\n";
        var result = await Parse(("catalog.yaml", CatalogFile), ("components/a.yaml", component));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("components/a.yaml", "implementations.stage.classes"), (error.File, error.Location));
        Assert.Equal("declares standard; every implementation must declare the classes of 'default' (standard, isolated).", error.Message);
    }

    [Fact]
    public async Task Policy_matching_an_undeclared_environment_type_is_an_error()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("p.yaml", "kind: Policy\nname: pol-a\nreason: r\nmatch: { template: t/x, environmentType: stagee }\nset: { a.b: 1 }\n"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("p.yaml", "match.environmentType"), (error.File, error.Location));
        Assert.Contains("'stagee' is not a declared environment type (declared: dev, stage)", error.Message);
    }

    [Fact]
    public async Task Schema_violation_is_reported_with_file_and_location()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("components/bad.yaml", TestCatalog.Component("alpha-db", "[]", "nodes:\n  db:\n    template: t\n")));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("components/bad.yaml", "implementations.default"), (error.File, error.Location));
        Assert.Contains($"#/{TestCatalog.PartPath}.nodes.db.config", error.Message);
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
    [InlineData("{ template: t/x, environmentType: dev }", "set: { a.b: 1 }\ndefault: { c: 2 }")]
    [InlineData("{ kind: runtime, runtime: container-app, environmentType: dev }", "set: { runtime.a: 1 }\ndefault: { runtime.b: 2 }\nadd:\n  n:\n    template: t/x\n    config: {}")]
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
    public async Task Node_names_in_components_runtimes_and_policy_adds_must_be_lowercase_kebab(string name)
    {
        var component = await Parse(("catalog.yaml", CatalogFile), ("c.yaml", TestCatalog.Component("alpha-db", "[]", $"nodes:\n  \"{name}\":\n    template: t/x\n    config: {{}}\n")));
        var runtime = await Parse(("catalog.yaml", CatalogFile), ("r.yaml", TestCatalog.Runtime($"nodes:\n  runtime:\n    template: t/x\n    config: {{}}\n  \"{name}\":\n    template: t/x\n    config: {{}}\n")));
        var policy = await Parse(("catalog.yaml", CatalogFile), ("p.yaml", $"kind: Policy\nname: pol-a\nreason: r\nmatch: {{ kind: runtime }}\nadd:\n  \"{name}\":\n    template: t/x\n    config: {{}}\n"));

        var componentError = Assert.Single(component.Errors);
        Assert.Equal(("c.yaml", $"{TestCatalog.PartPath}.nodes.{name}"), (componentError.File, componentError.Location));
        Assert.Contains("not a valid node name", componentError.Message);
        var runtimeError = Assert.Single(runtime.Errors);
        Assert.Equal(("r.yaml", $"implementations.default.nodes.{name}"), (runtimeError.File, runtimeError.Location));
        var policyError = Assert.Single(policy.Errors);
        Assert.Equal(("p.yaml", $"add.{name}"), (policyError.File, policyError.Location));
    }

    [Fact]
    public async Task Kebab_node_names_load()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("r.yaml", TestCatalog.Runtime("nodes:\n  runtime:\n    template: t/x\n    config: {}\n  app-insights-2:\n    template: t/x\n    config: {}\n")));

        Assert.Empty(result.Errors);
    }

    private const string RuntimeHead = "kind: Runtime\nname: container-app\ndescription: d\nimplementations:\n  default:\n";
    private const string RuntimeNode = "    nodes:\n      runtime:\n        template: t/rt\n        config: {}\n";
    private const string Probe = "    probe: { path: /health, expectedStatus: 200, timeoutSeconds: 60, intervalSeconds: 5 }\n";
    private const string Release = "    release: { suffixInput: suffix, trafficInput: traffic, trafficOutput: traffic, suffixOutput: suffix, revisionOutput: revision, fqdnOutput: fqdn, revisionKey: revisionName, latestKey: latestRevision, weightKey: weight, resourceIdOutput: appId }\n";
    private const string ComponentHead = "kind: Component\nname: alpha-db\ndescription: d\nexports: [endpoint]\nimplementations:\n  default:\n";
    private const string ClassesWith = "    classes:\n      standard:\n        exports: { endpoint: x }\n        nodes:\n          ";

    // The schema reports a violation inside an implementation at the implementation, naming the path inside it.
    [Theory]
    [InlineData(RuntimeHead + Probe + Release + "    nodes:\n      other:\n        template: t/rt\n        config: {}\n", "implementations.default.nodes")]
    [InlineData(RuntimeHead + Probe + Release + RuntimeNode + "    exports:\n      endpoint: x\n", "implementations.default.exports")]
    [InlineData(ComponentHead + Probe + ClassesWith + "db: { template: t/db, config: {} }\n", "implementations.default.probe")]
    [InlineData(RuntimeHead + Probe + RuntimeNode, "implementations.default.release")]
    [InlineData(RuntimeHead + Release + RuntimeNode, "implementations.default.probe")]
    public async Task The_schema_rules_of_runtimes_and_components_are_enforced(string file, string path)
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("f.yaml", file));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("f.yaml", "implementations.default"), (error.File, error.Location));
        Assert.Contains($"#/{path}", error.Message);
    }

    [Theory]
    [InlineData(ComponentHead + ClassesWith + "runtime: { template: t/rt, config: {} }\n", TestCatalog.PartPath + ".nodes.runtime", "'runtime' is the runtime node, declared only by a Runtime.")]
    [InlineData(RuntimeHead + "    probe: { path: /health, expectedStatus: 200, timeoutSeconds: 5, intervalSeconds: 6 }\n" + Release + RuntimeNode, "implementations.default.probe.intervalSeconds", "must not exceed timeoutSeconds.")]
    [InlineData(RuntimeHead + Probe + Release + "    nodes:\n      runtime:\n        template: t/rt\n        config: { suffix: x }\n", "implementations.default.nodes.runtime.config.suffix", "the release flow sets this input; the runtime node must not.")]
    [InlineData(RuntimeHead + Probe + Release + "    nodes:\n      runtime:\n        template: t/rt\n        config: { traffic: [] }\n", "implementations.default.nodes.runtime.config.traffic", "the release flow sets this input; the runtime node must not.")]
    public async Task The_runtime_rules_are_enforced(string file, string location, string message)
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("f.yaml", file));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("f.yaml", location), (error.File, error.Location));
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
        var result = await Parse(("catalog.yaml", CatalogFile), ("m.yaml", RuntimeHead + $"    probe: {probe}\n" + Release + RuntimeNode));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("m.yaml", "implementations.default"), (error.File, error.Location));
        Assert.Contains($"#/implementations.default.{location}", error.Message);
    }

    [Theory]
    [InlineData("suffixInput")]
    [InlineData("trafficInput")]
    [InlineData("trafficOutput")]
    [InlineData("suffixOutput")]
    [InlineData("revisionOutput")]
    [InlineData("fqdnOutput")]
    [InlineData("revisionKey")]
    [InlineData("latestKey")]
    [InlineData("weightKey")]
    [InlineData("resourceIdOutput")]
    public async Task A_release_block_missing_a_name_or_naming_it_empty_is_an_error(string name)
    {
        var without = System.Text.RegularExpressions.Regex.Replace(Release, $" {name}: [A-Za-z]+,?", string.Empty);
        var empty = System.Text.RegularExpressions.Regex.Replace(Release, $"{name}: [A-Za-z]+", $"{name}: ''");

        var missing = await Parse(("catalog.yaml", CatalogFile), ("m.yaml", RuntimeHead + Probe + without + RuntimeNode));
        var blank = await Parse(("catalog.yaml", CatalogFile), ("m.yaml", RuntimeHead + Probe + empty + RuntimeNode));

        Assert.Contains(missing.Errors, e => e.Message.Contains($"#/implementations.default.release.{name}", StringComparison.Ordinal));
        Assert.Contains($"#/implementations.default.release.{name}", Assert.Single(blank.Errors).Message);
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

        Assert.Contains("duplicate key 'version' at line 4", error.Message);
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
        var result = await Parse(("catalog.yaml", "kind: Catalog\nversion: 1\nenvironmentTypes: { dev: {}, stage: { approval: true } }\n"), ("components/a.yaml", ComponentFile("alpha-db")));

        var error = Assert.Single(result.Errors);
        Assert.Equal("catalog.yaml", error.File);
        Assert.Contains("schema violation", error.Message);
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
            ("components/a.yaml", ComponentFile("alpha-db")),
            ("components/b.yaml", ComponentFile("alpha-db")),
            ("components/c.yaml", ComponentFile("gamma-db", "  other: x\n")),
            ("odd.yaml", "kind: Widget\n"),
            ("roles.yaml", "kind: Roles\nroles:\n  reader: not-a-guid\n"));

        var files = result.Errors.Select(e => e.File).ToHashSet();
        Assert.Contains("components/b.yaml", files);
        Assert.Contains("components/c.yaml", files);
        Assert.Contains("odd.yaml", files);
        Assert.Contains("roles.yaml", files);
    }
}
