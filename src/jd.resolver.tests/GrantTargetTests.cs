using jd.resolver.catalog;
using Xunit;

namespace jd.resolver.tests;

// The grant target rule at load (docs/architecture/components.md section 3, security boundaries 1 and 2): a grant node sits in a class
// part or the runtime, its template is listed in grants.yaml, and its target input is exactly ${<node>.<output>} of a node of its own part
// or the runtime. The graph builder's check after policies is in GraphBuilderTests.
public class GrantTargetTests
{
    private const string CatalogFile = "kind: Catalog\nversion: \"1\"\nenvironmentTypes: { dev: { layer: environment }, stage: { layer: environment, approval: true } }\n";

    private const string SharedGroup = "kind: SharedResource\nname: shared-group\ndescription: d\nscope: shared\nnodes:\n  group: { template: t/x, config: {} }\nexports: { name: '${group.name}' }\n";

    // The account's resourceGroup export chains to another part's node.
    private const string CosmosAccount = "kind: SharedResource\nname: cosmos-account\ndescription: d\nscope: shared\nuses: [shared-group]\n"
        + "nodes:\n  cosmos: { template: t/x, config: {} }\nexports: { id: '${cosmos.id}', resourceGroup: '${shared-group.name}' }\n";

    // A grant node `access` on the target input `scope`; its other input reads what the component uses.
    private static string Grant(string target, string template = "t/g") =>
        $"access: {{ kind: grant, template: {template}, config: {{ {(target.Length == 0 ? string.Empty : $"scope: {target}, ")}accountId: '${{cosmos-account.id}}' }} }}";

    // A component alpha-db with an environment part (exports databaseId) and a class part: a node `container` and the given nodes.
    private static string Component(string classNodes, string environmentNodes = "db: { template: t/db, config: {} }") =>
        "kind: Component\nname: alpha-db\ndescription: d\nexports: [endpoint]\nuses: [cosmos-account]\nimplementations:\n  default:\n"
        + $"    environment: {{ nodes: {{ {environmentNodes} }}, exports: {{ databaseId: '${{db.id}}' }} }}\n"
        + $"    classes:\n      standard:\n        nodes: {{ container: {{ template: t/x, config: {{}} }}, {classNodes} }}\n        exports: {{ endpoint: x }}\n";

    private static string Runtime(string extraNodes = "") =>
        TestCatalog.RuntimeHeader.Replace("implementations:\n", "uses: [cosmos-account]\nimplementations:\n", StringComparison.Ordinal)
        + TestCatalog.RuntimeImplementation($"nodes:\n  runtime: {{ template: t/rt, config: {{}} }}\n{extraNodes}");

    private static Task<LoadResult<Catalog>> Parse(string component, string runtime = "", string sharedGroup = SharedGroup) =>
        CatalogParser.ParseAsync(new[]
        {
            ("catalog.yaml", CatalogFile), ("grants.yaml", TestCatalog.GrantsFile), ("shared/a.yaml", sharedGroup), ("shared/b.yaml", CosmosAccount),
            ("c.yaml", component), ("r.yaml", runtime.Length > 0 ? runtime : Runtime()),
        }.Select(f => new CatalogSource(f.Item1, f.Item2)));

    private static LoadError SingleError(LoadResult<Catalog> result)
    {
        Assert.Null(result.Value);
        return Assert.Single(result.Errors);
    }

    [Theory]
    [InlineData("'${container.scope}'")]
    [InlineData("'${ container.scope }'")]
    [InlineData("'${runtime.principalId}'")]
    public async Task A_grant_on_its_own_node_or_the_runtime_loads_and_may_read_what_the_component_uses(string target)
    {
        var result = await Parse(Component(Grant(target)));

        Assert.Empty(result.Errors);
        Assert.Equal("scope", result.Value!.GrantTargets["t/g"]);
    }

    [Theory]
    [InlineData("'${cosmos-account.id}'")]             // a used shared resource
    [InlineData("'${cosmos-account.resourceGroup}'")]  // a chained export, to another part's node
    [InlineData("'${alpha-db.databaseId}'")]           // its own component's environment part
    [InlineData("/subscriptions/s/accounts/a")]        // a literal
    [InlineData("'x-${container.scope}'")]             // text around the reference
    [InlineData("'${container.scope}-x'")]
    [InlineData("'${container.scope}${container.id}'")]
    [InlineData("\"${guid(container.scope)}\"")]       // a function
    [InlineData("'${env.region}'")]
    [InlineData("'${resource.alpha-db.endpoint}'")]
    [InlineData("'${role.reader}'")]
    [InlineData("''")]                                 // set but empty
    [InlineData("")]                                   // not set
    public async Task A_grant_target_that_is_not_one_reference_to_its_own_part_or_the_runtime_is_a_load_error(string target)
    {
        var error = SingleError(await Parse(Component(Grant(target))));

        Assert.Equal(("c.yaml", $"{TestCatalog.PartPath}.nodes.access.config.scope"), (error.File, error.Location));
        Assert.Contains("a grant's target ('scope') must be exactly ${<node>.<output>} of a node of its own part or the runtime", error.Message);
    }

    [Theory]
    [InlineData("'${runtime.id}'", null)]
    [InlineData("'${sidecar.id}'", null)]
    [InlineData("'${cosmos-account.id}'", "r.yaml")]
    public async Task A_runtime_grant_targets_a_node_of_the_runtime(string target, string? errorFile)
    {
        var runtime = Runtime($"  sidecar: {{ template: t/x, config: {{}} }}\n  {Grant(target)}\n");

        var result = await Parse(Component("other: { template: t/x, config: {} }"), runtime);

        Assert.Equal(errorFile is null ? [] : [(errorFile, "implementations.default.nodes.access.config.scope")], result.Errors.Select(e => (e.File, e.Location)));
    }

    [Fact]
    public async Task A_grant_template_not_in_grants_yaml_is_a_load_error()
    {
        var error = SingleError(await Parse(Component(Grant("'${container.scope}'", template: "t/other"))));

        Assert.Equal(("c.yaml", $"{TestCatalog.PartPath}.nodes.access.template"), (error.File, error.Location));
        Assert.Equal("'t/other' is not a grant template in the catalog's Grants targets (t/g); list it with the input that names its target.", error.Message);
    }

    [Fact]
    public async Task A_grant_node_in_a_shared_resource_is_a_load_error()
    {
        var shared = SharedGroup.Replace("nodes:\n", $"nodes:\n  {Grant("'${group.id}'").Replace("accountId: '${cosmos-account.id}'", "k: 1")}\n", StringComparison.Ordinal);

        var error = SingleError(await Parse(Component("other: { template: t/x, config: {} }"), sharedGroup: shared));

        Assert.Equal(("shared/a.yaml", "nodes.access"), (error.File, error.Location));
        Assert.Contains("a grant node belongs only in a class part or the runtime, not in a part of the shared scope", error.Message);
    }

    [Fact]
    public async Task A_grant_node_in_an_environment_part_is_a_load_error()
    {
        var error = SingleError(await Parse(Component("other: { template: t/x, config: {} }", $"db: {{ template: t/db, config: {{}} }}, {Grant("'${db.id}'")}")));

        Assert.Equal(("c.yaml", "implementations.default.environment.nodes.access"), (error.File, error.Location));
        Assert.Contains("not in a part of the environment scope", error.Message);
    }
}
