using System.Text;
using jd.resolver.catalog;
using Xunit;

namespace jd.resolver.tests;

public sealed class CatalogLoaderTests
{
    private const string CatalogFile = "kind: Catalog\nversion: \"1\"\nenvironmentTypes: { dev: { layer: environment }, stage: { layer: environment, approval: true } }\n";

    private static string ComponentFile(string name, string exports = "  endpoint: ${db.endpoint}\n", string key = "default") =>
        TestCatalog.Component(name, "[endpoint]", $"nodes:\n  db:\n    template: t/db\n    config: {{ size: 1 }}\nexports:\n{exports}", key);

    private static string PolicyFile(string name) =>
        $"kind: Policy\nname: {name}\nreason: because\nmatch: {{ kind: runtime }}\nset: {{ runtime.b: 1 }}\n";

    private static Task<LoadResult<Catalog>> Parse(params (string Path, string Content)[] files) =>
        CatalogLoader.LoadAsync(new SourceCatalog("catalog", [.. files.Select(f => new SourceDocument(f.Path, Encoding.UTF8.GetBytes(f.Content)))]));

    // The environment types (docs/architecture/components.md section 2): declared in the Catalog file, named like requirement types, `default`
    // excluded (the implementations' fallback key).
    [Theory]
    [InlineData("", "catalog.yaml: environmentTypes: ")]
    [InlineData("environmentTypes: {}\n", "catalog.yaml: environmentTypes: ")]
    [InlineData("environmentTypes: { default: {} }\n", "catalog.yaml: environmentTypes.default: ")]
    [InlineData("environmentTypes: { Dev: {} }\n", "catalog.yaml: environmentTypes.Dev: ")]
    [InlineData("environmentTypes: { dev: { approval: \"yes\" } }\n", "BooleanExpected: #/environmentTypes.dev.approval")]
    [InlineData("environmentTypes: { dev: { layer: tenant } }\n", "NotInEnumeration: #/environmentTypes.dev.layer")]
    public async Task Environment_types_are_required_and_well_formed(string declaration, string error)
    {
        var result = await Parse(("catalog.yaml", "kind: Catalog\nversion: \"1\"\n" + declaration));

        Assert.Null(result.Value);
        Assert.Contains(error, Assert.Single(result.Errors).ToString());
    }

    [Fact]
    public async Task Valid_files_build_the_model()
    {
        var result = await Parse(
            ("catalog.yaml", CatalogFile),
            ("components/a.yaml", ComponentFile("alpha-db")),
            ("runtimes/r.yaml", TestCatalog.RuntimeFile),
            ("policies/p.yaml", PolicyFile("p-one") + "require: alpha-db\n"),
            ("naming.yaml", "kind: Naming\nrules:\n  thing:\n    pattern: \"{workload}-{hash}\"\n    maxLength: 20\n    allowed: \"[a-z0-9]\"\n"),
            ("roles.yaml", "kind: Roles\nroles:\n  reader: 00000000-0000-0000-0000-000000000001\n"));

        Assert.Empty(result.Errors);
        var catalog = Assert.IsType<Catalog>(result.Value);
        Assert.Equal("1", catalog.Version);
        Assert.Equal([("dev", false), ("stage", true)], catalog.EnvironmentTypes.Select(t => (t.Key, t.Value.Approval)).OrderBy(t => t.Key, StringComparer.Ordinal));
        var component = Assert.Single(catalog.Components).Value;
        Assert.Equal(("alpha-db", "components/a.yaml"), (component.Name, component.Source));
        Assert.Equal(["endpoint"], component.Contract);
        var part = TestCatalog.PartAt(component, "implementations.default.classes.standard");
        Assert.Equal(NodeKind.Create, part.Nodes["db"].Kind);
        Assert.Equal(1, (int)part.Nodes["db"].Config["size"]);
        Assert.Equal("${db.endpoint}", part.Exports["endpoint"]);
        var runtime = Assert.Single(catalog.Runtimes).Value;
        var implementation = TestCatalog.PartAt(runtime, "implementations.default");
        Assert.Equal(("container-app", "runtimes/r.yaml", (TestCatalog.Probe, TestCatalog.Release)), (runtime.Name, runtime.Source, catalog.Rollouts[("container-app", "default")]));
        Assert.Equal("t/runtime", implementation.Nodes["runtime"].Template);
        var policy = Assert.Single(catalog.Policies);
        Assert.Equal("alpha-db", policy.Require);
        Assert.True(policy.Set.ContainsKey("runtime.b"));
        Assert.Equal(20, catalog.Naming["thing"].MaxLength);
        Assert.Equal("00000000-0000-0000-0000-000000000001", catalog.Roles["reader"]);
    }

    // Each runtime's rollout is read from its own file.
    [Fact]
    public async Task Each_runtime_has_the_probe_its_own_file_declares()
    {
        const string nodes = "nodes:\n  runtime: { template: t/rt, config: {} }\n";
        var other = TestCatalog.Runtime(nodes).Replace("name: container-app", "name: job-runner", StringComparison.Ordinal).Replace("path: /health", "path: /ready", StringComparison.Ordinal);
        var result = await Parse(("catalog.yaml", CatalogFile), ("runtimes/a.yaml", TestCatalog.Runtime(nodes)), ("runtimes/b.yaml", other));

        Assert.Empty(result.Errors);
        var rollouts = Assert.IsType<Catalog>(result.Value).Rollouts;
        Assert.Equal(("/health", "/ready"), (rollouts[("container-app", "default")].Probe.Path, rollouts[("job-runner", "default")].Probe.Path));
    }

    [Fact]
    public async Task Unknown_kind_is_an_error_naming_the_file()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("odd.yaml", "kind: Widget\n"));

        var error = Assert.Single(result.Errors);
        Assert.Equal("odd.yaml", error.File);
        Assert.Equal("kind", error.Location);
        Assert.Contains("unknown kind 'Widget'", error.Message);
        Assert.Null(result.Value);
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

        Assert.Equal([new LoadError("(catalog)", string.Empty, "no file of kind Catalog; exactly one is required.")], result.Errors);
    }

    // The second Catalog is one mistake: its environment types are not read as names declared a second time.
    [Fact]
    public async Task Duplicate_catalog_file_is_an_error_on_the_second_file()
    {
        var result = await Parse(("a.yaml", CatalogFile), ("b.yaml", CatalogFile));

        Assert.Equal([new LoadError("b.yaml", "kind", "a second Catalog file; exactly one is allowed (first: a.yaml).")], result.Errors);
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
        Assert.Null(result.Value);
    }

    // Locations are the document's JSON paths: a key that is not a plain name is written in brackets.
    [Fact]
    public async Task A_location_under_a_key_with_a_dot_writes_the_key_in_brackets()
    {
        var result = await Parse(("catalog.yaml", CatalogFile),
            ("components/a.yaml", TestCatalog.Component("alpha-db", "[endpoint]", "nodes:\n  db:\n    template: t/db\n    config: {}\nexports: {}\n", className: "big.one")));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("components/a.yaml", "implementations.default.classes['big.one'].exports"), (error.File, error.Location));
        Assert.Equal("must export exactly the contract of component 'alpha-db' (endpoint); missing endpoint.", error.Message);
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
        Assert.Equal("'stagee' is not an environment type in the catalog (declared: dev, stage).", error.Message);
    }

    [Theory]
    [InlineData("components/a.yaml")]
    [InlineData("runtimes/r.yaml")]
    public async Task An_implementation_keyed_by_a_shared_layer_type_is_an_error(string file)
    {
        var content = file.StartsWith("runtimes/", StringComparison.Ordinal) ? TestCatalog.Runtime("nodes:\n  runtime: { template: t/rt, config: {} }\n", "shared") : ComponentFile("alpha-db", key: "shared");
        var result = await Parse(("catalog.yaml", CatalogFile.Replace("environmentTypes: {", "environmentTypes: { shared: { layer: shared },", StringComparison.Ordinal)), (file, content));

        var error = Assert.Single(result.Errors);
        Assert.Equal((file, "implementations.shared"), (error.File, error.Location));
        Assert.Equal("'shared' is a shared-layer environment type; implementations are keyed by environment-layer types or 'default'.", error.Message);
    }

    // A catalog file that failed its schema is reported on its own: its types are not read, so no key is judged against them.
    [Fact]
    public async Task A_catalog_file_that_fails_its_schema_is_not_checked_against_its_types()
    {
        var catalog = CatalogFile.Replace("environmentTypes: {", "environmentTypes: { shared: { layer: shared },", StringComparison.Ordinal).Replace("version: \"1\"\n", string.Empty, StringComparison.Ordinal);
        var result = await Parse(("catalog.yaml", catalog), ("components/a.yaml", ComponentFile("alpha-db", key: "shared")));

        var error = Assert.Single(result.Errors);
        Assert.Equal("catalog.yaml", error.File);
        Assert.StartsWith("schema violation: PropertyRequired: #/version", error.Message, StringComparison.Ordinal);
    }

    // The parts of one implementation share its key: the key is reported once, not once per class.
    [Fact]
    public async Task An_implementation_keyed_by_a_shared_layer_type_with_two_classes_is_one_error()
    {
        const string part = "{ nodes: { db: { template: t/db, config: {} } }, exports: { endpoint: x } }";
        var component = $"kind: Component\nname: alpha-db\ndescription: d\nexports: [endpoint]\nimplementations:\n  shared:\n    classes: {{ standard: {part}, isolated: {part} }}\n";
        var result = await Parse(("catalog.yaml", CatalogFile.Replace("environmentTypes: {", "environmentTypes: { shared: { layer: shared },", StringComparison.Ordinal)), ("components/a.yaml", component));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("components/a.yaml", "implementations.shared"), (error.File, error.Location));
        Assert.Equal("'shared' is a shared-layer environment type; implementations are keyed by environment-layer types or 'default'.", error.Message);
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
        Assert.Equal("'stagee' is not an environment type in the catalog (declared: dev, stage).", error.Message);
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
    [InlineData("{ kind: runtime, template: t/x }", "set: { runtime.a: 1 }", "match")]
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
    [InlineData("{ kind: runtime, runtime: container-app, environmentType: dev }", "set: { runtime.a: 1 }\ndefault: { runtime.b: 2 }\nrequire: alpha-db")]
    public async Task A_policy_in_either_scope_loads(string match, string body)
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("c.yaml", ComponentFile("alpha-db")), ("p.yaml", $"kind: Policy\nname: pol-a\nreason: r\nmatch: {match}\n{body}\n"));

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

    // The part schema's message (shared by every part kind), located at the node: a name with a dot in the validator's bracket form.
    [Theory]
    [InlineData("Db", ".Db")]
    [InlineData("my_node", ".my_node")]
    [InlineData("a-", ".a-")]
    [InlineData("1a", ".1a")]
    [InlineData("a.b", "['a.b']")]
    [InlineData("a@b", ".a@b")]
    public async Task Node_names_in_components_and_runtimes_must_be_lowercase_kebab(string name, string located)
    {
        var component = await Parse(("catalog.yaml", CatalogFile), ("c.yaml", TestCatalog.Component("alpha-db", "[]", $"nodes:\n  \"{name}\":\n    template: t/x\n    config: {{}}\n")));
        var runtime = await Parse(("catalog.yaml", CatalogFile), ("r.yaml", TestCatalog.Runtime($"nodes:\n  runtime:\n    template: t/x\n    config: {{}}\n  \"{name}\":\n    template: t/x\n    config: {{}}\n")));

        var componentError = Assert.Single(component.Errors);
        Assert.Equal(("c.yaml", $"{TestCatalog.PartPath}.nodes{located}", NodeNameMessage), (componentError.File, componentError.Location, componentError.Message));
        var runtimeError = Assert.Single(runtime.Errors);
        Assert.Equal(("r.yaml", $"implementations.default.nodes{located}", NodeNameMessage), (runtimeError.File, runtimeError.Location, runtimeError.Message));
    }

    internal const string NodeNameMessage = "not a valid node name; use lowercase letters, digits and hyphens, starting with a letter and not ending in a hyphen.";

    [Fact]
    public async Task Kebab_node_names_load()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("r.yaml", TestCatalog.Runtime("nodes:\n  runtime:\n    template: t/x\n    config: {}\n  app-insights-2:\n    template: t/x\n    config: {}\n")));

        Assert.Empty(result.Errors);
    }

    // A refused node name does not hide the part's other problems (documents.md section 6): each is reported where it is, the one without a
    // message in the validator's text.
    [Theory]
    [InlineData("nodes:\n  db:\n    config: {}\n", "nodes.db", "AdditionalPropertiesNotValid: #/implementations.default.classes.standard.nodes.db { PropertyRequired: #/implementations.default.classes.standard.nodes.db.template }")]
    [InlineData("nodes:\n  db:\n    template: t/x\n    config: {}\nprobe: {}\n", "probe", "NoAdditionalPropertiesAllowed: #/implementations.default.classes.standard.probe")]
    public async Task A_bad_node_name_is_reported_beside_the_parts_other_problems(string part, string location, string violation)
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("c.yaml", TestCatalog.Component("alpha-db", "[]", part.Replace("nodes:\n", "nodes:\n  Bad:\n    template: t/x\n    config: {}\n", StringComparison.Ordinal))));

        Assert.Equal(
            [new LoadError("c.yaml", $"{TestCatalog.PartPath}.nodes.Bad", NodeNameMessage), new LoadError("c.yaml", $"{TestCatalog.PartPath}.{location}", $"schema violation: {violation}")],
            result.Errors.OrderBy(error => error.Location, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_part_without_nodes_is_a_schema_violation()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("c.yaml", TestCatalog.Component("alpha-db", "[]", "nodes: {}\nexports: {}\n")));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("c.yaml", "implementations.default"), (error.File, error.Location));
        Assert.Contains($"TooFewProperties: #/{TestCatalog.PartPath}.nodes", error.Message);
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
    [InlineData(ComponentHead + ClassesWith + "runtime: { template: t/rt, config: {} }\n", TestCatalog.PartPath + ".nodes.runtime", "'runtime' is the runtime node, declared only by a runtime.")]
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

    [Fact]
    public async Task A_probe_whose_interval_equals_its_timeout_loads()
    {
        var probe = "    probe: { path: /health, expectedStatus: 200, timeoutSeconds: 5, intervalSeconds: 5 }\n";

        var result = await Parse(("catalog.yaml", CatalogFile), ("f.yaml", RuntimeHead + probe + Release + RuntimeNode));

        Assert.Empty(result.Errors);
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

    // A policy requires, it never adds (docs/architecture/components.md section 3): the schema refuses an earlier policy's `add` like any field it
    // does not declare, and the policy does not load.
    [Fact]
    public async Task A_policy_with_add_is_refused_by_the_schema()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("p.yaml", PolicyFile("pol-a") + "add:\n  extra:\n    template: t/x\n    config: {}\n"));

        Assert.Null(result.Value);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("p.yaml", "add", "schema violation: NoAdditionalPropertiesAllowed: #/add"), (error.File, error.Location, error.Message));
    }

    [Fact]
    public async Task A_policy_requiring_a_name_that_is_not_a_component_is_an_error()
    {
        var undeclared = await Parse(("catalog.yaml", CatalogFile), ("c.yaml", ComponentFile("alpha-db")), ("p.yaml", PolicyFile("pol-a") + "require: beta-db\n"));
        var runtime = await Parse(("catalog.yaml", CatalogFile), ("c.yaml", ComponentFile("alpha-db")), ("r.yaml", TestCatalog.RuntimeFile), ("p.yaml", PolicyFile("pol-a") + "require: container-app\n"));

        var error = Assert.Single(undeclared.Errors);
        Assert.Equal(("p.yaml", "require"), (error.File, error.Location));
        Assert.Equal("'beta-db' is not a component in the catalog (declared: alpha-db).", error.Message);
        var notComponent = Assert.Single(runtime.Errors);
        Assert.Equal(("p.yaml", "require", "'container-app' is not a component in the catalog (declared: alpha-db)."), (notComponent.File, notComponent.Location, notComponent.Message));
    }

    [Fact]
    public async Task A_policy_requiring_a_component_whose_file_is_invalid_reports_only_the_component()
    {
        var invalid = ComponentFile("alpha-db").Replace("description: d\n", string.Empty, StringComparison.Ordinal);

        var result = await Parse(("catalog.yaml", CatalogFile), ("c.yaml", invalid), ("p.yaml", PolicyFile("pol-a") + "require: alpha-db\n"));

        Assert.Equal("c.yaml", Assert.Single(result.Errors).File);
    }

    [Fact]
    public async Task A_node_scope_policy_cannot_require_a_component()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("c.yaml", ComponentFile("alpha-db")), ("p.yaml", "kind: Policy\nname: pol-a\nreason: r\nmatch: { template: t/x }\nrequire: alpha-db\n"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("p.yaml", "require"), (error.File, error.Location));
        Assert.Contains("a node-scope policy cannot require a component", error.Message);
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

    // The location is the document path of the value, so a kind whose key holds a JSONPath special character is bracket-quoted.
    [Fact]
    public async Task Naming_allowed_of_a_kind_with_a_special_character_is_located_with_a_bracketed_key()
    {
        const string naming = "kind: Naming\nrules:\n  a/b:\n    pattern: \"{id}\"\n    maxLength: 20\n    allowed: \"[a-z]\"\n";
        var result = await Parse(("catalog.yaml", CatalogFile), ("naming.yaml", naming));

        Assert.Equal(
            [new LoadError("naming.yaml", "rules['a/b'].allowed", "'[a-z]' must allow every hash digit 0-9a-f; it rejects 0123456789.")],
            result.Errors);
    }

    // The define rule keeps the first file's rule; the second's allowed characters are still checked, in its own file.
    [Fact]
    public async Task Naming_allowed_is_checked_in_a_second_file_that_rules_the_same_kind()
    {
        const string naming = "kind: Naming\nrules:\n  thing:\n    pattern: \"{id}\"\n    maxLength: 20\n    allowed: \"{0}\"\n";
        var result = await Parse(("catalog.yaml", CatalogFile), ("n1.yaml", naming.Replace("{0}", "[a-z0-9]", StringComparison.Ordinal)), ("n2.yaml", naming.Replace("{0}", "[a-z]", StringComparison.Ordinal)));

        Assert.Equal(
            [
                new LoadError("n2.yaml", "rules.thing", "'thing' is already declared in n1.yaml."),
                new LoadError("n2.yaml", "rules.thing.allowed", "'[a-z]' must allow every hash digit 0-9a-f; it rejects 0123456789."),
            ],
            result.Errors);
    }

    [Fact]
    public async Task Schema_invalid_catalog_file_still_counts_as_the_catalog()
    {
        var result = await Parse(("catalog.yaml", "kind: Catalog\nversion: 1\nenvironmentTypes: { dev: { layer: environment }, stage: { layer: environment, approval: true } }\n"), ("components/a.yaml", ComponentFile("alpha-db")));

        var error = Assert.Single(result.Errors);
        Assert.Equal("catalog.yaml", error.File);
        Assert.Contains("schema violation", error.Message);
    }

    // Names that are a map's keys: YAML refuses a key twice in one file (Repeated_key_is_an_error), so a second definition is in another file.
    [Theory]
    [InlineData("kind: Roles\nroles:\n  reader: 00000000-0000-0000-0000-000000000001\n", "roles.reader", "reader")]
    [InlineData("kind: Naming\nrules:\n  thing:\n    pattern: \"{id}\"\n    maxLength: 20\n    allowed: \"[a-z0-9]\"\n", "rules.thing", "thing")]
    [InlineData("kind: Grants\ntargets: { t/g: scope }\n", "targets.t/g", "t/g")]
    public async Task Same_naming_rule_role_or_grant_template_in_two_files_is_an_error(string file, string location, string name)
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("r1.yaml", file), ("r2.yaml", file));

        Assert.Equal([new LoadError("r2.yaml", location, $"'{name}' is already declared in r1.yaml.")], result.Errors);
    }

    // A policy that failed its schema is reported there; the component it requires is not reported as undeclared as well.
    [Fact]
    public async Task A_reference_in_a_file_that_failed_its_schema_is_not_checked()
    {
        var result = await Parse(("catalog.yaml", CatalogFile), ("p.yaml", PolicyFile("pol-a").Replace("reason: because\n", string.Empty, StringComparison.Ordinal) + "require: beta-db\n"));

        Assert.Equal(("p.yaml", "reason"), (Assert.Single(result.Errors).File, Assert.Single(result.Errors).Location));
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
