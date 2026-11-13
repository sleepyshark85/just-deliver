using Xunit;

namespace jd.cli.tests;

// The start release deploy and release qualify share (docs/architecture/application.md section 8): the gate before the identity, the identity before
// set.yaml is kept, set.yaml before the backend is built; and every refusal of the start, met by a qualification, word for word.
public sealed partial class ReleaseDeployCliTests
{
    [Theory]
    [InlineData("release deploy")]
    [InlineData("release qualify")]
    public async Task The_gate_refuses_before_the_identity_is_built(string command)
    {
        var set = await CreateSetAsync();

        var (code, _, _) = await RunAsync(new RecordingBackend(), Command(command, set, await GatedEnvironmentAsync()));

        Assert.Equal(1, code);
        Assert.Equal(["Records", "Time"], Invoked);
        Assert.Equal(0, _identity.Calls);
    }

    [Theory]
    [InlineData("release deploy")]
    [InlineData("release qualify")]
    public async Task A_refused_identity_keeps_no_set_yaml_and_builds_no_backend(string command)
    {
        var set = await CreateSetAsync();
        _identity.Failure = new InvalidOperationException("the credential was refused.");

        var (code, _, _) = await RunAsync(new RecordingBackend { Deployed = true }, Command(command, set, Environment));

        Assert.Equal(1, code);
        Assert.Equal(["Records", "Time", "Identity"], Invoked);
        Assert.Empty(Directory.GetFileSystemEntries(_records));
    }

    [Theory]
    [InlineData("release deploy")]
    [InlineData("release qualify")]
    public async Task Set_yaml_is_kept_before_the_backend_is_built(string command)
    {
        var set = await CreateSetAsync();
        var backend = new RecordingBackend { Deployed = true };
        bool? keptFirst = null;
        var connections = Connections(backend) with
        {
            Backend = () =>
            {
                keptFirst = File.Exists(Path.Combine(LabelDirectory, "set.yaml"));
                return backend;
            },
        };

        await RunWithAsync(connections, Command(command, set, Environment));

        Assert.True(keptFirst);
    }

    // A qualification refused at the start: exit 1, nothing on stdout, exactly the expected stderr, and no backend call or live read.
    private async Task AssertQualifyRefusedAsync(Func<Task<(int Code, string Out, string Err)>> run, string expectedStderr)
    {
        var result = await run();

        Assert.Equal((1, string.Empty, expectedStderr), result);
        Assert.Empty(_live.Calls);
    }

    [Fact]
    public async Task A_qualification_of_a_set_that_cannot_be_loaded_prints_its_error()
    {
        var set = Path.Combine(_temp, "set.yaml");
        await File.WriteAllTextAsync(set, "kind: Nothing\n");
        var backend = new RecordingBackend();

        await AssertQualifyRefusedAsync(() => RunAsync(backend, Qualify(set)), $"{set}: kind: unknown kind 'Nothing'; expected one of ReleaseSet.\n");
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task A_qualification_with_a_catalog_error_prints_it_with_its_path_under_the_catalog()
    {
        var set = await CreateSetAsync();
        var catalog = Path.Combine(_temp, "catalog");
        CliTests.CopyDirectory(Catalog, catalog);
        await File.WriteAllTextAsync(Path.Combine(catalog, "components", "oops.yaml"), "kind: Component\nname: oops\ndescription: d\n");
        var backend = new RecordingBackend();

        await AssertQualifyRefusedAsync(() => RunAsync(backend, ["release", "qualify", set, "--env", Environment, "--catalog", catalog]),
            $"{Path.Combine(catalog, "components/oops.yaml")}: exports: schema violation: PropertyRequired: #/exports\n"
            + $"{Path.Combine(catalog, "components/oops.yaml")}: implementations: schema violation: PropertyRequired: #/implementations\n");
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task A_qualification_on_an_environment_of_an_undeclared_type_prints_the_type_error()
    {
        var set = await CreateSetAsync();
        var path = Path.Combine(_temp, "typed.env.yaml");
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(Environment)).Replace("type: dev", "type: gold", StringComparison.Ordinal));
        var backend = new RecordingBackend();

        await AssertQualifyRefusedAsync(() => RunAsync(backend, Command("release qualify", set, path)), $"{path}: type: 'gold' is not an environment type of the catalog (declared: dev, prod, shared, stage).\n");
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task A_qualification_on_a_gated_environment_is_refused_naming_qualify()
    {
        var set = await CreateSetAsync();
        var backend = new RecordingBackend();

        await AssertQualifyRefusedAsync(
            async () => await RunAsync(backend, Command("release qualify", set, await GatedEnvironmentAsync())),
            "jd: release qualify: environment 'dev' is gated by an approval policy: use `jd promote`.\n");
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task A_qualification_of_a_set_that_does_not_resolve_prints_the_resolution_error()
    {
        var set = await CreateSetAsync(app => app.Replace("- type: database", "- type: no-such-type\n    id: database"));
        var backend = new RecordingBackend();

        await AssertQualifyRefusedAsync(() => RunAsync(backend, Qualify(set)),
            $"{set}#{App}: requires[0]: requirement 'database': 'no-such-type' is not a component in the catalog.\n"
            + string.Concat(new[] { "CONTAINER", "DATABASE", "ENDPOINT" }.Select(variable =>
                $"{set}#{App}: container.variables.COSMOS_{variable}: node '{App}/dev/@workload/runtime': field 'variables.COSMOS_{variable}' references "
                + $"resource.database.{variable.ToLowerInvariant()}, which is not an export of a requirement of this workload.\n")));
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task A_qualification_without_a_template_library_prints_its_one_error()
    {
        var set = await CreateSetAsync();
        var catalog = Path.Combine(_temp, "catalog");
        CliTests.CopyDirectory(Catalog, catalog);
        Directory.Delete(Path.Combine(catalog, "templates"), recursive: true);
        var backend = new RecordingBackend();

        await AssertQualifyRefusedAsync(
            () => RunAsync(backend, ["release", "qualify", set, "--env", Environment, "--catalog", catalog]),
            $"{Path.Combine(catalog, "templates")}: template directory not found.\n" + string.Concat(
                from workload in new[] { Worker, App }
                from node in new (string Node, string Template)[] { ("database/container", "cosmos-sql-container"), ("monitoring/appinsights", "application-insights"), ("@workload/runtime", "container-app"), ("database/access", "cosmos-sql-role-assignment"), ("monitoring/access", "role-assignment") }
                select $"{workload}/dev/{node.Node}: template: template 'azure/{node.Template}' is not in the template library.\n"));
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task A_qualification_whose_principal_cannot_be_named_prints_why()
    {
        var set = await CreateSetAsync();
        _identity.Failure = new InvalidOperationException("the credential was refused.");
        var backend = new RecordingBackend { Deployed = true };

        await AssertQualifyRefusedAsync(() => RunAsync(backend, Qualify(set)), "jd: the credential was refused.\n");
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task A_qualification_of_another_set_under_a_kept_label_prints_the_conflict()
    {
        var set = await CreateSetAsync();
        await RunAsync(new RecordingBackend(), Deploy(set));
        var other = await CreateSetAsync(app => app.Replace("LOG_LEVEL: info", "LOG_LEVEL: debug"), "other.yaml");
        var backend = new RecordingBackend { Deployed = true };
        _live.Calls.Clear(); // the deploy above qualified itself

        await AssertQualifyRefusedAsync(
            () => RunAsync(backend, Qualify(other)),
            $"jd: the kept set.yaml holds a different release set for label 1.0.0 on dev (sha256 {Sha256(set)}, this one {Sha256(other)}); a label names one set, so use a new label.\n");
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task A_qualification_of_a_label_the_store_cannot_use_prints_why()
    {
        var set = await CreateSetAsync(label: "1.0.0-rc..1");
        var backend = new RecordingBackend { Deployed = true };

        await AssertQualifyRefusedAsync(
            () => RunAsync(backend, Qualify(set)), "jd: '1.0.0-rc..1' cannot name a directory of the release records: it is empty or contains '..', '/' or '\\'.\n");
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task A_qualification_whose_reader_cannot_be_built_prints_why()
    {
        var set = await CreateSetAsync();
        var backend = new RecordingBackend { Deployed = true };

        await AssertQualifyRefusedAsync(
            () => RunWithAsync(Connections(backend) with { LiveRevisions = () => throw new InvalidOperationException("the reader cannot be built.") }, Qualify(set)),
            "jd: the reader cannot be built.\n");
        Assert.Empty(backend.Calls);
    }
}
