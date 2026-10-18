using jd.resolver.catalog;
using Xunit;

namespace jd.resolver.tests;

// Shared resources, `uses` and what a part may read (docs/architecture/components.md section 2, "Checked at load"): each check is a load
// error at the file and location, and the catalog does not load.
public class CatalogUsesTests
{
    private const string CatalogFile = "kind: Catalog\nversion: \"1\"\nenvironmentTypes: { dev: { layer: environment }, stage: { layer: environment, approval: true } }\n";

    // A shared resource with one node (default: the resource's name, prefixed `n-`, so nodes of one scope do not collide), configured
    // `config`, exporting `exports` (default: the node's id).
    private static string Shared(string name, string scope = "shared", string uses = "[]", string? node = null, string config = "{ size: 1 }", string? exports = null)
    {
        node ??= $"n-{name}";
        return $"kind: SharedResource\nname: {name}\ndescription: d\nscope: {scope}\nuses: {uses}\nnodes:\n  {node}:\n    template: t/x\n    config: {config}\nexports: {exports ?? $"{{ id: '${{{node}.id}}' }}"}\n";
    }

    // A component alpha-db whose class part has one node `node` (config `config`), optionally with an environment part.
    private static string Component(string uses = "[]", string node = "node", string config = "{ size: 1 }", string? environment = null, string name = "alpha-db") =>
        $"kind: Component\nname: {name}\ndescription: d\nexports: [endpoint]\nuses: {uses}\nimplementations:\n  default:\n"
        + (environment is null ? string.Empty : $"    environment: {environment}\n")
        + $"    classes:\n      standard:\n        nodes:\n          {node}: {{ template: t/x, config: {config} }}\n        exports: {{ endpoint: x }}\n";

    private static string Runtime(string uses, string config) =>
        TestCatalog.RuntimeHeader.Replace("implementations:\n", $"uses: {uses}\nimplementations:\n", StringComparison.Ordinal)
        + TestCatalog.RuntimeImplementation($"nodes:\n  runtime:\n    template: t/rt\n    config: {config}\n");

    private static Task<CatalogLoadResult> Parse(params (string Path, string Content)[] files) =>
        CatalogParser.ParseAsync(files.Prepend(("catalog.yaml", CatalogFile)).Select(f => new CatalogSource(f.Item1, f.Item2)));

    private static LoadError SingleError(CatalogLoadResult result)
    {
        Assert.Null(result.Catalog);
        return Assert.Single(result.Errors);
    }

    [Fact]
    public async Task Shared_resources_and_uses_build_the_model()
    {
        var result = await Parse(
            ("shared/a.yaml", Shared("shared-group", exports: "{ name: x }")),
            ("shared/b.yaml", Shared("cosmos-account", uses: "[shared-group]", node: "cosmos", config: "{ group: '${shared-group.name}' }", exports: "{ id: '${cosmos.id}', resourceGroup: '${shared-group.name}' }")),
            ("c.yaml", Component(uses: "[cosmos-account]", environment: "{ nodes: { db: { template: t/db, config: { group: '${cosmos-account.resourceGroup}' } } }, exports: { databaseName: '${db.name}' } }",
                config: "{ account: '${cosmos-account.id}', database: '${alpha-db.databaseName}' }")),
            ("r.yaml", Runtime("[shared-group]", "{ group: '${shared-group.name}' }")));

        Assert.Empty(result.Errors);
        var catalog = Assert.IsType<Catalog>(result.Catalog);
        var account = catalog.SharedResources["cosmos-account"];
        Assert.Equal(("shared/b.yaml", SharedScope.Shared), (account.Source, account.Scope));
        Assert.Equal(["shared-group"], account.Uses);
        Assert.Equal("${shared-group.name}", account.Part.Exports["resourceGroup"]);
        var component = catalog.Components["alpha-db"];
        Assert.Equal(["cosmos-account"], component.Uses);
        var environment = Assert.IsType<Part>(component.Implementations["default"].Environment);
        Assert.Equal("${db.name}", environment.Exports["databaseName"]);
        Assert.Equal(["shared-group"], catalog.Runtimes["container-app"].Uses);
    }

    [Theory]
    [InlineData("components/z.yaml")]
    [InlineData("runtimes/z.yaml")]
    public async Task A_shared_resource_shares_the_namespace_of_components_and_runtimes(string second)
    {
        var duplicate = second.StartsWith("runtimes/", StringComparison.Ordinal) ? Runtime("[]", "{}").Replace("container-app", "alpha-db", StringComparison.Ordinal) : Component();
        var error = SingleError(await Parse(("a.yaml", Shared("alpha-db")), (second, duplicate)));

        Assert.Equal((second, "name"), (error.File, error.Location));
        Assert.Equal("'alpha-db' is already declared in a.yaml.", error.Message);
    }

    [Theory]
    [InlineData("env")]
    [InlineData("role")]
    [InlineData("workload")]
    [InlineData("resource")]
    [InlineData("runtime")]
    public async Task A_reserved_root_is_not_a_name(string name)
    {
        var shared = SingleError(await Parse(("s.yaml", Shared(name))));
        var component = SingleError(await Parse(("c.yaml", Component(name: name))));

        Assert.Equal(("s.yaml", "name"), (shared.File, shared.Location));
        Assert.Equal($"'{name}' is a reserved root of expressions (env, resource, role, runtime, workload); choose another name.", shared.Message);
        Assert.Equal(("c.yaml", "name"), (component.File, component.Location));
    }

    [Theory]
    [InlineData("c.yaml")]
    [InlineData("r.yaml")]
    [InlineData("e.yaml")]
    public async Task Uses_must_name_a_declared_shared_resource(string file)
    {
        string Uses(string f) => f == file ? "[cosmos-acount]" : "[]";
        var error = SingleError(await Parse(
            ("s.yaml", Shared("shared-group")), ("e.yaml", Shared("env-group", scope: "environment", uses: Uses("e.yaml"))), ("c.yaml", Component(uses: Uses("c.yaml"))),
            ("r.yaml", Runtime(Uses("r.yaml"), "{}"))));

        Assert.Equal((file, "uses"), (error.File, error.Location));
        Assert.Equal("'cosmos-acount' is not a shared resource in the catalog (shared resources: env-group, shared-group).", error.Message);
    }

    // The second declaration is the one error: the checks that read shared resources by name use the first.
    [Fact]
    public async Task A_shared_resource_declared_twice_is_only_the_duplicate_name_error()
    {
        var error = SingleError(await Parse(
            ("a.yaml", Shared("shared-group")), ("b.yaml", Shared("shared-group")), ("c.yaml", Component(uses: "[shared-group]", config: "{ group: '${shared-group.id}' }"))));

        Assert.Equal(("b.yaml", "name", "'shared-group' is already declared in a.yaml."), (error.File, error.Location, error.Message));
    }

    [Theory]
    [InlineData("alpha-db")]
    [InlineData("container-app")]
    public async Task Uses_cannot_name_a_component_or_runtime(string used)
    {
        var error = SingleError(await Parse(("c.yaml", Component()), ("r.yaml", Runtime("[]", "{}")), ("s.yaml", Shared("shared-group", uses: $"[{used}]"))));

        Assert.Equal(("s.yaml", "uses"), (error.File, error.Location));
        Assert.Equal($"'{used}' is a component or runtime; uses lists shared resources only.", error.Message);
    }

    [Fact]
    public async Task A_shared_scope_resource_cannot_use_an_environment_scope_one()
    {
        var error = SingleError(await Parse(("e.yaml", Shared("env-group", scope: "environment")), ("s.yaml", Shared("shared-group", uses: "[env-group]"))));

        Assert.Equal(("s.yaml", "uses"), (error.File, error.Location));
        Assert.Equal("'env-group' is environment scope; a shared-scope resource uses only shared-scope ones, since the shared layer is deployed first.", error.Message);
    }

    // Allowed: environment -> shared, environment; component and runtime -> any.
    [Fact]
    public async Task The_allowed_scope_directions_load()
    {
        var result = await Parse(
            ("s.yaml", Shared("shared-group")),
            ("e.yaml", Shared("env-group", scope: "environment", uses: "[shared-group]", node: "group")),
            ("f.yaml", Shared("apps-env", scope: "environment", uses: "[env-group, shared-group]", node: "apps")),
            ("c.yaml", Component(uses: "[shared-group, apps-env]")),
            ("r.yaml", Runtime("[env-group, shared-group]", "{}")));

        Assert.Empty(result.Errors);
    }

    // One error per cycle, at its first member; res-0 only leads into the cycle, so it is not in it.
    [Fact]
    public async Task A_uses_cycle_is_one_error_naming_the_cycle()
    {
        var two = SingleError(await Parse(("a.yaml", Shared("res-a", uses: "[res-b]")), ("b.yaml", Shared("res-b", uses: "[res-a]", node: "other"))));
        var three = SingleError(await Parse(
            ("a.yaml", Shared("res-a", uses: "[res-b]")), ("b.yaml", Shared("res-b", uses: "[res-c]", node: "nb")), ("c.yaml", Shared("res-c", uses: "[res-a]", node: "nc")),
            ("d.yaml", Shared("res-0", uses: "[res-a]", node: "nd"))));
        var self = SingleError(await Parse(("a.yaml", Shared("res-a", uses: "[res-a]"))));

        Assert.Equal(("a.yaml", "uses", "uses cycle: res-a -> res-b -> res-a."), (two.File, two.Location, two.Message));
        Assert.Equal(("a.yaml", "uses", "uses cycle: res-a -> res-b -> res-c -> res-a."), (three.File, three.Location, three.Message));
        Assert.Equal(("a.yaml", "uses", "uses cycle: res-a -> res-a."), (self.File, self.Location, self.Message));
    }

    [Fact]
    public async Task A_root_that_is_no_node_name_in_uses_own_component_or_reserved_root_is_an_error()
    {
        var error = SingleError(await Parse(("c.yaml", Component(config: "{ account: '${cosmos.id}' }"))));

        Assert.Equal(("c.yaml", $"{TestCatalog.PartPath}.nodes.node.config.account"), (error.File, error.Location));
        Assert.Equal("'cosmos.id' reads unknown root 'cosmos'; expected a node of this part (node), a name in uses, 'alpha-db', or one of env, resource, role, runtime, workload.", error.Message);
    }

    // Boundary 4: env.* reads only the environment's own fields, so it cannot read a part past uses; every part kind is checked.
    [Theory]
    [InlineData("s.yaml", "nodes.n-cosmos-account.config.group", "{ group: '${env.shared-group.name}' }", null, null)]
    [InlineData("c.yaml", TestCatalog.PartPath + ".nodes.node.config.account", null, "{ account: \"${guid(env.cosmos-account.id)}\" }", null)]
    [InlineData("c.yaml", TestCatalog.PartPath + ".nodes.node.config.account", null, "{ account: '${env.region.name}' }", null)]
    [InlineData("r.yaml", "implementations.default.nodes.runtime.config.group", null, null, "{ group: '${env.apps-env.id}' }")]
    public async Task Env_other_than_name_region_and_type_is_a_load_error(string file, string location, string? shared, string? component, string? runtime)
    {
        var error = SingleError(await Parse(
            ("s.yaml", Shared("cosmos-account", config: shared ?? "{ v: '${env.name}-${env.region}-${env.type}' }")),
            ("c.yaml", Component(config: component ?? "{}")),
            ("r.yaml", Runtime("[]", runtime ?? "{}"))));

        Assert.Equal((file, location), (error.File, error.Location));
        Assert.Contains("env.* reads only env.name, env.region, env.type; read a shared resource's export as ${<name>.<export>} through uses.", error.Message);
    }

    // Every part kind is checked: a shared resource, an environment part, a class part (config and exports, inside a function call) and the runtime.
    [Theory]
    [InlineData("s.yaml", "nodes.n-cosmos-account.config.group", "{ group: '${shared-group.name}' }", null, null, null)]
    [InlineData("c.yaml", "implementations.default.environment.nodes.db.config.group", null, "{ nodes: { db: { template: t/db, config: { group: '${shared-group.name}' } } } }", null, null)]
    [InlineData("c.yaml", TestCatalog.PartPath + ".nodes.node.config.tags.group", null, null, "{ tags: { group: \"${guid('a', shared-group.name)}\" } }", null)]
    [InlineData("r.yaml", "implementations.default.nodes.runtime.config.group", null, null, null, "{ group: '${shared-group.name}' }")]
    public async Task A_shared_resource_not_in_uses_cannot_be_read(string file, string location, string? shared, string? environment, string? component, string? runtime)
    {
        var error = SingleError(await Parse(
            ("g.yaml", Shared("shared-group", exports: "{ name: x }")),
            ("s.yaml", Shared("cosmos-account", config: shared ?? "{}")),
            ("c.yaml", Component(environment: environment, config: component ?? "{}")),
            ("r.yaml", Runtime("[]", runtime ?? "{}"))));

        Assert.Equal((file, location), (error.File, error.Location));
        Assert.Equal("'shared-group.name' reads 'shared-group', which is not in uses; add it to the file's uses.", error.Message);
    }

    // Boundary 5: a class part reads its own component's environment part, never another component's.
    [Fact]
    public async Task A_class_part_cannot_read_another_component()
    {
        var other = Component(name: "beta-db", environment: "{ nodes: { db: { template: t/db, config: {} } }, exports: { databaseName: x } }");
        var error = SingleError(await Parse(("b.yaml", other), ("c.yaml", Component(config: "{ database: '${beta-db.databaseName}' }"))));

        Assert.Equal(("c.yaml", $"{TestCatalog.PartPath}.nodes.node.config.database"), (error.File, error.Location));
        Assert.Equal("'beta-db.databaseName' reads another component or runtime; a part reads only its own nodes, what it uses and, in a class, its own component.", error.Message);
    }

    [Fact]
    public async Task An_environment_part_cannot_read_its_own_component()
    {
        var environment = "{ nodes: { db: { template: t/db, config: { a: '${alpha-db.databaseName}' } } }, exports: { databaseName: x } }";
        var error = SingleError(await Parse(("c.yaml", Component(environment: environment))));

        Assert.Equal(("c.yaml", "implementations.default.environment.nodes.db.config.a"), (error.File, error.Location));
        Assert.Equal("'alpha-db.databaseName': an environment part cannot read its own component.", error.Message);
    }

    [Theory]
    [InlineData("{ group: '${shared-group.nmae}' }", null, "'shared-group.nmae': 'shared-group' exports name, location.")]
    [InlineData("{ group: '${shared-group.name.first}' }", null, "'shared-group.name.first': 'shared-group' exports name, location.")]
    [InlineData("{ database: '${alpha-db.databaseId}' }", "{ nodes: { db: { template: t/db, config: {} } }, exports: { databaseName: x } }", "'alpha-db.databaseId': 'alpha-db' exports databaseName.")]
    [InlineData("{ database: '${alpha-db.databaseName}' }", null, "'alpha-db.databaseName': component 'alpha-db' has no environment part in this implementation.")]
    public async Task A_read_export_must_be_declared(string config, string? environment, string message)
    {
        var error = SingleError(await Parse(("g.yaml", Shared("shared-group", exports: "{ name: x, location: y }")), ("c.yaml", Component(uses: "[shared-group]", config: config, environment: environment))));

        Assert.Equal(("c.yaml", $"{TestCatalog.PartPath}.nodes.node.config.{config[2..config.IndexOf(':')]}"), (error.File, error.Location));
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public async Task An_export_read_in_an_export_must_be_declared()
    {
        var error = SingleError(await Parse(("g.yaml", Shared("shared-group", exports: "{ name: x }")), ("s.yaml", Shared("cosmos-account", uses: "[shared-group]", exports: "{ resourceGroup: '${shared-group.group}' }"))));

        Assert.Equal(("s.yaml", "exports.resourceGroup"), (error.File, error.Location));
        Assert.Equal("'shared-group.group': 'shared-group' exports name.", error.Message);
    }

    [Theory]
    [InlineData("shared-group", TestCatalog.PartPath + ".nodes.shared-group")]
    [InlineData("alpha-db", TestCatalog.PartPath + ".nodes.alpha-db")]
    [InlineData("env", TestCatalog.PartPath + ".nodes.env")]
    [InlineData("role", TestCatalog.PartPath + ".nodes.role")]
    public async Task A_node_cannot_take_a_name_its_part_reads_as_a_root(string node, string location)
    {
        var error = SingleError(await Parse(("g.yaml", Shared("shared-group")), ("c.yaml", Component(uses: "[shared-group]", node: node))));

        Assert.Equal(("c.yaml", location), (error.File, error.Location));
        Assert.Equal($"'{node}' is a name this part reads as a root; rename the node.", error.Message);
    }

    [Fact]
    public async Task A_shared_resource_node_cannot_take_a_used_name_or_the_runtime_node()
    {
        var used = SingleError(await Parse(("g.yaml", Shared("shared-group")), ("s.yaml", Shared("cosmos-account", uses: "[shared-group]", node: "shared-group", exports: "{}"))));
        var runtime = SingleError(await Parse(("s.yaml", Shared("cosmos-account", node: "runtime", exports: "{}"))));

        Assert.Equal(("s.yaml", "nodes.shared-group"), (used.File, used.Location));
        Assert.Equal(("s.yaml", "nodes.runtime", "'runtime' is the runtime node, declared only by a Runtime."), (runtime.File, runtime.Location, runtime.Message));
    }

    // The kebab rule covers every part kind; class parts and runtimes are covered in CatalogParserTests.
    [Fact]
    public async Task Shared_resource_and_environment_part_node_names_must_be_lowercase_kebab()
    {
        var shared = SingleError(await Parse(("s.yaml", Shared("shared-group", node: "Bad_Name", exports: "{}"))));
        var environment = SingleError(await Parse(("c.yaml", Component(environment: "{ nodes: { Bad_Name: { template: t/db, config: {} } }, exports: {} }"))));

        Assert.Equal(("s.yaml", "nodes.Bad_Name"), (shared.File, shared.Location));
        Assert.StartsWith("'Bad_Name' is not a valid node name;", shared.Message);
        Assert.Equal(("c.yaml", "implementations.default.environment.nodes.Bad_Name"), (environment.File, environment.Location));
    }

    [Theory]
    [InlineData("shared", "shared")]
    [InlineData("environment", "environment")]
    public async Task Node_names_are_unique_across_the_parts_of_one_scope(string scope, string named)
    {
        var error = SingleError(await Parse(("a.yaml", Shared("res-a", scope: scope, node: "group")), ("b.yaml", Shared("res-b", scope: scope, node: "group"))));

        Assert.Equal(("b.yaml", "nodes.group"), (error.File, error.Location));
        Assert.Equal($"node 'group' is also declared by 'res-a' (a.yaml); node names are unique across the parts of one {named} scope.", error.Message);
    }

    // One evaluator holds every part of a run and looks up nodes before parts, so a node named like a shared resource would replace it.
    [Fact]
    public async Task An_environment_part_node_may_not_take_a_shared_resource_name()
    {
        var environment = "{ nodes: { env-group: { template: t/db, config: {} } }, exports: { databaseName: x } }";
        var error = SingleError(await Parse(("a.yaml", Shared("env-group", scope: "environment")), ("c.yaml", Component(environment: environment))));

        Assert.Equal(("c.yaml", "implementations.default.environment.nodes.env-group"), (error.File, error.Location));
        Assert.Equal("node 'env-group' takes the name of shared resource 'env-group', which a part of the same run reads as ${env-group.<export>}.", error.Message);
    }

    [Fact]
    public async Task A_shared_resource_node_may_not_take_another_shared_resource_name()
    {
        var error = SingleError(await Parse(("a.yaml", Shared("res-a")), ("b.yaml", Shared("res-b", node: "res-a"))));

        Assert.Equal(("b.yaml", "nodes.res-a"), (error.File, error.Location));
        Assert.Contains("takes the name of shared resource 'res-a'", error.Message);
    }

    [Fact]
    public async Task An_environment_part_shares_the_environment_scope()
    {
        var environment = "{ nodes: { group: { template: t/db, config: {} } }, exports: { databaseName: x } }";
        var error = SingleError(await Parse(("a.yaml", Shared("env-group", scope: "environment", node: "group")), ("c.yaml", Component(environment: environment))));

        Assert.Equal(("c.yaml", "implementations.default.environment.nodes.group"), (error.File, error.Location));
        Assert.Contains("is also declared by 'env-group' (a.yaml)", error.Message);
    }

    // Different scopes never meet in one run, a component's implementations are selected one at a time, and workload parts have their own scopes.
    [Fact]
    public async Task A_node_name_may_repeat_across_scopes_implementations_and_workload_parts()
    {
        const string environment = "{ nodes: { database: { template: t/db, config: {} } }, exports: { databaseName: x } }";
        var component = Component(node: "group", environment: environment)
            + $"  dev:\n    environment: {environment}\n    classes:\n      standard:\n        nodes:\n          group: {{ template: t/x, config: {{}} }}\n        exports: {{ endpoint: x }}\n";
        var result = await Parse(
            ("a.yaml", Shared("shared-group", node: "group")), ("b.yaml", Shared("env-group", scope: "environment", node: "group")), ("c.yaml", component), ("d.yaml", Component(name: "beta-db", node: "group")));

        Assert.Empty(result.Errors);
    }

    // A used resource whose file failed its schema is reported once, there, not again at every read of it.
    [Fact]
    public async Task A_read_of_a_used_resource_whose_file_is_invalid_reports_only_that_file()
    {
        var invalid = Shared("shared-group").Replace("description: d\n", string.Empty, StringComparison.Ordinal);
        var result = await Parse(("g.yaml", invalid), ("c.yaml", Component(uses: "[shared-group]", config: "{ group: '${shared-group.name}' }")));

        Assert.Equal("g.yaml", SingleError(result).File);
    }
}
