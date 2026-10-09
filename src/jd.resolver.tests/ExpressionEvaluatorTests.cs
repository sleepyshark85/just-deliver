using jd.definitionvalidator;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expressions;
using Xunit;

namespace jd.resolver.tests;

public class ExpressionEvaluatorTests
{
    private static readonly Dictionary<string, NamingRule> Naming = new()
    {
        ["short"] = new("{workload}-{id}-{env}", 255, "[a-z0-9-]"),
        ["tiny"] = new("{workload}-{id}", 12, "[a-z0-9-]"),
        ["hashed"] = new("x-{hash}", 255, "[a-z0-9-]"),
    };

    private static readonly Dictionary<string, string> Roles = new() { ["reader"] = "00000000-0000-0000-0000-000000000001" };

    private static readonly EnvironmentDescriptor Env = new(
        "dev",
        "testregion",
        "team",
        new Dictionary<string, string>
        {
            ["resourceGroup"] = "rg-test",
            ["cosmos.accountName"] = "acct",
            ["cosmos.accountId"] = "/acct/id",
            ["cosmos.endpoint"] = "https://acct.example",
            ["logAnalytics.id"] = "/law/id",
        },
        []);

    private static ExpressionContext Context(
        string workload = "shop",
        string id = "db",
        Dictionary<Reference, string>? known = null,
        IReadOnlyDictionary<string, NamingRule>? naming = null,
        IReadOnlyDictionary<string, string>? roles = null) =>
        new(roles ?? Roles, naming ?? Naming, Env, workload, "team-a", id, new HashSet<string> { "database", "container" }, known ?? []);

    private static (EvalResult? Result, List<LoadError> Errors) Run(string text, ExpressionContext? context = null)
    {
        var errors = new List<LoadError>();
        var result = new ExpressionEvaluator(context ?? Context()).Evaluate(text, "file.yaml", "nodes.x.config.y", errors);
        return (result, errors);
    }

    private static string Resolve(string text, ExpressionContext? context = null)
    {
        var (result, errors) = Run(text, context);
        Assert.Empty(errors);
        return Assert.IsType<Resolved>(result).Value;
    }

    private static Pending Await(string text, ExpressionContext? context = null)
    {
        var (result, errors) = Run(text, context);
        Assert.Empty(errors);
        return Assert.IsType<Pending>(result);
    }

    private static LoadError Fails(string text, ExpressionContext? context = null)
    {
        var (result, errors) = Run(text, context);
        Assert.Null(result);
        var error = Assert.Single(errors);
        Assert.Equal("file.yaml", error.File);
        Assert.Equal("nodes.x.config.y", error.Location);
        return error;
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("")]
    [InlineData("/id")]
    [InlineData("a $ b { c }")]
    public void Text_without_expressions_is_unchanged(string text) => Assert.Equal(text, Resolve(text));

    [Fact]
    public void Text_and_several_expressions_are_joined()
    {
        Assert.Equal("/acct/id/sqlRoleDefinitions/00000000-0000-0000-0000-000000000001", Resolve("${env.cosmos.accountId}/sqlRoleDefinitions/${role.reader}"));
        Assert.Equal("shop by team-a in dev", Resolve("${workload.name} by ${workload.team} in ${env.name}"));
    }

    [Fact]
    public void Env_workload_and_role_namespaces_resolve()
    {
        Assert.Equal("rg-test", Resolve("${env.resourceGroup}"));
        Assert.Equal("testregion", Resolve("${env.region}"));
        Assert.Equal("shop", Resolve("${workload.name}"));
        Assert.Equal("00000000-0000-0000-0000-000000000001", Resolve("${role.reader}"));
    }

    [Fact]
    public void Node_runtime_and_resource_namespaces_are_pending_with_their_references()
    {
        Assert.Equal([new Reference(ReferenceKind.Node, "database", "databaseName")], Await("${database.databaseName}").References);
        Assert.Equal([new Reference(ReferenceKind.Node, "runtime", "principalId")], Await("${runtime.principalId}").References);
        var pending = Await("${resource.cosmos-sql.endpoint}");
        Assert.Equal([new Reference(ReferenceKind.Resource, "cosmos-sql", "endpoint")], pending.References);
        Assert.Equal("${resource.cosmos-sql.endpoint}", pending.Original);
    }

    [Fact]
    public void References_of_every_expression_in_a_string_are_collected_once()
    {
        var pending = Await("${database.databaseName}/${container.containerName}/${database.databaseName}/${env.name}");

        Assert.Equal(2, pending.References.Count);
        Assert.Contains(new Reference(ReferenceKind.Node, "container", "containerName"), pending.References);
    }

    [Fact]
    public void Pending_becomes_resolved_when_reevaluated_with_known_outputs()
    {
        var text = "db=${database.databaseName};id=${runtime.principalId}";
        var known = new Dictionary<Reference, string> { [new(ReferenceKind.Node, "database", "databaseName")] = "shop-db" };

        Assert.Equal([new Reference(ReferenceKind.Node, "runtime", "principalId")], Await(text, Context(known: known)).References);

        known[new(ReferenceKind.Node, "runtime", "principalId")] = "p1";
        Assert.Equal("db=shop-db;id=p1", Resolve(text, Context(known: known)));
    }

    [Fact]
    public void Resource_references_resolve_from_known_outputs()
    {
        var known = new Dictionary<Reference, string> { [new(ReferenceKind.Resource, "cosmos-sql", "endpoint")] = "https://x" };

        Assert.Equal("https://x", Resolve("${resource.cosmos-sql.endpoint}", Context(known: known)));
    }

    [Theory]
    [InlineData("${env.nope}", "'env.nope' is not a value of environment 'dev'")]
    [InlineData("${env.cosmos}", "'env.cosmos' is not a value")]
    [InlineData("${env}", "'env' is not a value")]
    [InlineData("${role.nope}", "'role.nope' is not a role in the catalog")]
    [InlineData("${workload.region}", "not a workload field")]
    [InlineData("${nope.output}", "unknown node 'nope'")]
    [InlineData("${database}", "<node>.<output>")]
    [InlineData("${database.a.b}", "<node>.<output>")]
    [InlineData("${resource.id}", "resource.<id>.<output>")]
    [InlineData("${name('nope')}", "no naming rule for kind 'nope'")]
    [InlineData("${nope('x')}", "unknown function 'nope'")]
    [InlineData("${name()}", "takes exactly 1 argument(s) but got 0")]
    [InlineData("${name('short', 'tiny')}", "takes exactly 1 argument(s) but got 2")]
    [InlineData("${guid()}", "takes at least 1 argument(s) but got 0")]
    [InlineData("${a b}", "neither a dotted path nor a single-quoted literal")]
    [InlineData("${name('short'}", "not a valid function call")]
    [InlineData("${'abc'}", "literals are only valid as function arguments")]
    [InlineData("${env.name", "unterminated expression")]
    public void Invalid_expressions_are_errors_with_location(string text, string message) => Assert.Contains(message, Fails(text).Message);

    [Fact]
    public void Every_invalid_expression_in_a_string_is_reported()
    {
        var (result, errors) = Run("${env.nope} ${role.nope} ${nope('x')}");

        Assert.Null(result);
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void Name_substitutes_lowercases_and_filters_characters()
    {
        Assert.Equal("shop-db-dev", Resolve("${name('short')}"));
        Assert.Equal("shop-mydb-dev", Resolve("${name('short')}", Context(id: "My_DB")));
    }

    [Fact]
    public void Name_over_max_length_keeps_prefix_and_appends_hash()
    {
        var name = Resolve("${name('tiny')}", Context(workload: "verylongworkload"));

        // Hash: first 6 hex characters of SHA-256("verylongworkload|dev|db|tiny"), computed independently of the C# code.
        Assert.Equal("verylo084fc9", name);
    }

    [Fact]
    public void Name_is_deterministic_and_varies_with_each_input()
    {
        var a = Resolve("${name('hashed')}");

        Assert.Equal(a, Resolve("${name('hashed')}"));
        // Hash: first 6 hex characters of SHA-256("shop|dev|db|hashed"); pins the input order.
        Assert.Equal("x-9889ea", a);
        Assert.NotEqual(a, Resolve("${name('hashed')}", Context(workload: "other")));
        Assert.NotEqual(a, Resolve("${name('hashed')}", Context(id: "other")));
    }

    [Fact]
    public void Hash_depends_on_the_kind()
    {
        var naming = new Dictionary<string, NamingRule>(Naming) { ["other"] = Naming["hashed"] };

        Assert.NotEqual(Resolve("${name('hashed')}", Context(naming: naming)), Resolve("${name('other')}", Context(naming: naming)));
    }

    [Fact]
    public void Name_with_invalid_allowed_class_is_an_error()
    {
        var naming = new Dictionary<string, NamingRule> { ["bad"] = new("{id}", 10, "[a-") };

        Assert.Contains("invalid 'allowed'", Fails("${name('bad')}", Context(naming: naming)).Message);
    }

    [Fact]
    public void Uuid5_matches_the_rfc_4122_vector()
    {
        var dns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

        Assert.Equal(new Guid("2ed6657d-e927-568b-95e1-2665a8aea6a2"), BuiltIns.Uuid5(dns, "www.example.com"));
    }

    [Fact]
    public void Guid_is_deterministic_and_depends_on_every_argument()
    {
        var a = Resolve("${guid(env.cosmos.accountId, 'x')}");

        Assert.Equal(a, Resolve("${guid(env.cosmos.accountId, 'x')}"));
        // Pinned: UUIDv5 of "/acct/id|x" in the documented namespace, computed independently of the C# code.
        Assert.Equal("a81fb372-e883-5703-9b28-092d973ce2a0", a);
        Assert.NotEqual(a, Resolve("${guid(env.cosmos.accountId, 'y')}"));
    }

    [Fact]
    public void Guid_with_a_pending_argument_is_pending_and_resolves_once_known()
    {
        var text = "${guid(env.cosmos.accountId, runtime.principalId, 'x')}";
        Assert.Equal([new Reference(ReferenceKind.Node, "runtime", "principalId")], Await(text).References);

        var known = new Dictionary<Reference, string> { [new(ReferenceKind.Node, "runtime", "principalId")] = "p1" };
        Assert.Equal(BuiltIns.Uuid5(BuiltIns.GuidNamespace, "/acct/id|p1|x").ToString(), Resolve(text, Context(known: known)));
    }

    [Fact]
    public async Task Seed_catalog_and_workload_strings_evaluate()
    {
        var catalog = (await CatalogDirectory.LoadAsync(Path.Combine(AppContext.BaseDirectory, "catalog"))).Catalog!;
        var workload = YamlSchemaValidator.ParseYaml(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "workload.yaml")));
        var sampleEndpoint = Assert.IsType<string>((string?)workload["container"]?["variables"]?["COSMOS_ENDPOINT"]);
        var mapping = Assert.Single(catalog.Mappings);
        var policy = Assert.Single(catalog.Policies);
        var nodeNames = mapping.Nodes.Keys.Concat(policy.Add.Keys).ToHashSet();
        var strings = mapping.Nodes.Values.Concat(policy.Add.Values).SelectMany(n => n.Config.Values)
            .Concat(policy.Set.Values).Select(t => t.ToString())
            .Concat(mapping.Exports.Values)
            .Append(sampleEndpoint)
            .ToList();

        var evaluator = new ExpressionEvaluator(new ExpressionContext(catalog.Roles, catalog.Naming, Env, "shop", "team-a", "db", nodeNames, new Dictionary<Reference, string>()));
        var errors = new List<LoadError>();
        var results = strings.Select(s => evaluator.Evaluate(s, "seed", "", errors)).ToList();

        Assert.Empty(errors);
        Assert.All(results, r => Assert.NotNull(r));
        Assert.Contains(new Resolved("shop-db-dev"), results);
        Assert.Contains(new Resolved("/acct/id/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"), results);
        Assert.Contains(results, r => r is Pending p && p.Original.StartsWith("${guid(env.cosmos.accountId, runtime.principalId", StringComparison.Ordinal));
        Assert.Contains(results, r => r is Pending p && p.References.Contains(new Reference(ReferenceKind.Resource, "cosmos-sql", "endpoint")));
    }
}
