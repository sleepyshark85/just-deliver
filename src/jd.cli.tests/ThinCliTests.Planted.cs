using System.Reflection.Metadata;
using jd.core.records;
using jd.orchestrator.usecases;
using jd.resolver;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.graph;
using jd.resolver.release;
using Xunit;

namespace jd.cli.tests
{
    // Each rule over planted types of this assembly (jd.cli.tests.planted, never called), so the test proves the rule catches what it states.
    public sealed partial class ThinCliTests
    {
        private static readonly IlScan Planted = Scan(typeof(ThinCliTests).Assembly);

        private static Func<string, bool> Only(params Type[] types) => name => types.Any(t => t.FullName == name);

        // Compiled C# rarely holds the 2-byte forms (more than 255 locals), so the sizes are pinned here; the planted scans cover the rest.
        [Theory]
        [InlineData(ILOpCode.Nop, 0)]
        [InlineData(ILOpCode.Br_s, 1)]
        [InlineData(ILOpCode.Ldloc_s, 1)]
        [InlineData(ILOpCode.Ldloc, 2)]
        [InlineData(ILOpCode.Starg, 2)]
        [InlineData(ILOpCode.Br, 4)]
        [InlineData(ILOpCode.Ldstr, 4)]
        [InlineData(ILOpCode.Ldc_r8, 8)]
        public void The_scan_skips_an_operand_by_its_size(ILOpCode opCode, int size) => Assert.Equal(size, IlScan.OperandSize(opCode));

        [Fact]
        public void R1_names_the_command_and_member_that_run_a_rule_or_read_the_connections()
        {
            var findings = R1(Planted, Only(typeof(planted.Command)), UseCaseSurface([typeof(planted.UseCase)])).ToList();

            Assert.Equal(
                [
                    "jd.cli.tests.planted.Command+Nested.Resolves reaches jd.resolver.Resolver.Resolve",
                    "jd.cli.tests.planted.Command.CallsAPortInALambda reaches jd.core.records.IRecordStore.ListAsync",
                    "jd.cli.tests.planted.Command.HoldsAPortAcrossAnAwait reaches jd.core.records.IRecordStore.ListAsync",
                    "jd.cli.tests.planted.Command.HoldsAPortAcrossAnAwait reaches jd.orchestrator.usecases.Connections.get_Records",
                    "jd.cli.tests.planted.Command.HoldsAPortInAnAsyncLocalFunction reaches jd.core.records.IRecordStore.ListAsync",
                    "jd.cli.tests.planted.Command.HoldsAPortInAnAsyncLocalFunction reaches jd.orchestrator.usecases.Connections.get_Records",
                    "jd.cli.tests.planted.Command.ReadsTheRecords reaches jd.orchestrator.usecases.Connections.get_Records",
                    "jd.cli.tests.planted.Command.Resolves reaches jd.resolver.Resolver.Resolve",
                    "jd.cli.tests.planted.Command.ResolvesInALocalFunction reaches jd.resolver.Resolver.Resolve",
                ],
                findings.Select(f => f.Message).Order(StringComparer.Ordinal));
            Assert.Equal("jd.cli.tests.planted.Command -> jd.resolver.Resolver.Resolve", findings.Single(f => f.Message.StartsWith("jd.cli.tests.planted.Command+Nested.", StringComparison.Ordinal)).Key); // a nested type's key is its top-level type's
        }

        [Fact]
        public void A_breach_is_each_unlisted_finding_and_each_stale_entry_in_full()
        {
            var findings = new[] { new Finding("A -> B.c", "A.Run reaches B.c"), new Finding("A -> B.d", "A.Run reaches B.d"), new Finding("A -> B.d", "A.Other reaches B.d") };

            Assert.Equal(["A.Other reaches B.d", "A.Run reaches B.d", "stale allowlist entry: A -> B.e"], Breaches(findings, ["A -> B.c", "A -> B.e"]));
            Assert.Empty(Breaches(findings, ["A -> B.c", "A -> B.d"]));
            Assert.Equal("A.Other reaches B.d\nA.Run reaches B.d\nstale allowlist entry: A -> B.e", Assert.ThrowsAny<Exception>(() => AssertOnly(findings, ["A -> B.c", "A -> B.e"])).Message);
        }

        [Fact]
        public void A_use_case_is_a_type_with_a_static_run_and_its_surface_is_its_inputs_constructors_and_the_getters_of_its_signature_without_the_connections()
        {
            var useCases = UseCases(typeof(planted.UseCase)).ToList();
            var surface = UseCaseSurface(useCases);

            Assert.Equal([typeof(planted.UseCase)], useCases);

            Assert.Equal(
                new HashSet<(string, string)>
                {
                    ("jd.resolver.graph.GraphJson", "Serialize"),
                    ("jd.cli.tests.planted.UseCase", "RunAsync"),
                    ("jd.cli.tests.planted.Input", ".ctor"),
                    ("jd.cli.tests.planted.Piece", ".ctor"),
                    ("jd.cli.tests.planted.Result", "get_Problems"),
                    ("jd.cli.tests.planted.Problem", "get_Message"),
                    ("jd.cli.tests.planted.MissingFile", "get_Path"),
                },
                surface.ToHashSet());
        }

        // jd's own Validate: its command code may build the document and read the errors, nothing else of the document.
        [Fact]
        public void R1_names_the_validate_command_and_member_that_reach_past_the_validate_use_case()
        {
            var findings = R1(Planted, Only(typeof(planted.ValidateCommand)), UseCaseSurface(UseCases(typeof(Connections))));

            Assert.Equal(["jd.cli.tests.planted.ValidateCommand.DecodesTheDocument reaches jd.resolver.SourceDocument.Text"], findings.Select(f => f.Message));
        }

        // jd's own Preview and Deploy: their command code may build the inputs and read the results, the progress and the problem, nothing else.
        [Fact]
        public void R1_names_the_workload_command_and_member_that_reach_past_the_preview_and_deploy_use_cases()
        {
            var findings = R1(Planted, Only(typeof(planted.WorkloadCommand)), UseCaseSurface(UseCases(typeof(Connections))));

            Assert.Equal(
                [
                    "jd.cli.tests.planted.WorkloadCommand.BuildsTheTemplates reaches jd.orchestrator.usecases.Connections.get_Templates",
                    "jd.cli.tests.planted.WorkloadCommand.ChecksTheGate reaches jd.orchestrator.usecases.Checks.Gate",
                    "jd.cli.tests.planted.WorkloadCommand.ReadsTheCatalog reaches jd.resolver.SourceCatalog.get_Documents",
                ],
                findings.Select(f => f.Message).Order(StringComparer.Ordinal));
        }

        // jd's own EnvUp: its command code may build the input and read the result and the progress; loading, checking, resolving and composing are
        // the use case's.
        [Fact]
        public void R1_names_the_env_command_and_member_that_reach_past_the_env_up_use_case()
        {
            var findings = R1(Planted, Only(typeof(planted.EnvCommand)), UseCaseSurface(UseCases(typeof(Connections))));

            Assert.Equal(
                [
                    "jd.cli.tests.planted.EnvCommand.ChecksTheType reaches jd.orchestrator.usecases.Checks.EnvironmentType",
                    "jd.cli.tests.planted.EnvCommand.ComposesTheDescriptor reaches jd.resolver.environment.DescriptorComposer..ctor",
                    "jd.cli.tests.planted.EnvCommand.LoadsTheDefinition reaches jd.resolver.environment.EnvironmentDefinitionFile.LoadAsync",
                    "jd.cli.tests.planted.EnvCommand.LocatesTheCatalogErrors reaches jd.resolver.SourceCatalog.Located",
                ],
                findings.Select(f => f.Message).Order(StringComparer.Ordinal));
        }

        // jd's own CreateRelease and ShowRelease: their command code may build the inputs and read the results; building, serializing and the clock
        // are the use cases'.
        [Fact]
        public void R1_names_the_release_command_and_member_that_reach_past_the_create_and_show_use_cases()
        {
            var findings = R1(Planted, Only(typeof(planted.ReleaseCommand)), UseCaseSurface(UseCases(typeof(Connections))));

            Assert.Equal(
                [
                    "jd.cli.tests.planted.ReleaseCommand.BuildsTheSet reaches jd.resolver.release.ReleaseBuilder.BuildAsync",
                    "jd.cli.tests.planted.ReleaseCommand.ReadsTheClock reaches jd.orchestrator.usecases.Connections.get_Time",
                    "jd.cli.tests.planted.ReleaseCommand.SerializesTheSet reaches jd.resolver.release.ReleaseSetFile.Serialize",
                ],
                findings.Select(f => f.Message).Order(StringComparer.Ordinal));
        }

        [Fact]
        public void R2_names_the_type_and_member_that_name_a_port_outside_the_composition_and_the_composition_that_bypasses_one()
        {
            var findings = R2(Planted, Only(typeof(planted.Command), typeof(planted.Store), typeof(planted.Composition)), Only(typeof(planted.Composition))).Select(f => f.Message).Distinct().Order(StringComparer.Ordinal).ToList();

            Assert.Equal(
                [
                    "jd.cli.tests.planted.Command+Nested.Holds names the port jd.core.records.IRecordStore",
                    "jd.cli.tests.planted.Command.CallsAPortInALambda names the port jd.core.records.IRecordStore",
                    "jd.cli.tests.planted.Command.HoldsAPortAcrossAnAwait names the port jd.core.records.IRecordStore",
                    "jd.cli.tests.planted.Command.HoldsAPortInAnAsyncLocalFunction names the port jd.core.records.IRecordStore",
                    "jd.cli.tests.planted.Command.ReadsTheRecords names the port jd.core.records.IRecordStore",
                    "jd.cli.tests.planted.Command.TakesAPortFromACall names the port jd.core.records.IRecordStore",
                    "jd.cli.tests.planted.Composition.Connect reaches jd.resolver.Resolver.Resolve, which is not a port",
                    "jd.cli.tests.planted.Store.(its interfaces) names the port jd.core.records.IRecordStore",
                ],
                findings);
        }

        [Fact]
        public void R3_names_the_type_and_member_that_touch_a_file_the_environment_or_the_console_and_nothing_else()
        {
            var findings = R3(Planted, Only(typeof(planted.Inner))).Select(f => f.Message).Order(StringComparer.Ordinal).ToList();

            Assert.Equal(
                [
                    "jd.cli.tests.planted.Inner.Touches reaches System.Console.WriteLine",
                    "jd.cli.tests.planted.Inner.Touches reaches System.Environment.GetEnvironmentVariable",
                    "jd.cli.tests.planted.Inner.Touches reaches System.IO.File.Exists",
                    "jd.cli.tests.planted.Inner.Touches reaches System.IO.Path.GetFullPath",
                    "jd.cli.tests.planted.Inner.Touches reaches System.IO.StreamReader..ctor",
                    "jd.cli.tests.planted.Inner.TouchesAsync reaches System.IO.Directory.Exists",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.Environment.GetEnvironmentVariables",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.Environment.GetFolderPath",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.Environment.get_CurrentDirectory",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.Environment.set_CurrentDirectory",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.IO.DirectoryInfo..ctor",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.IO.FileInfo..ctor",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.IO.FileStream..ctor",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.IO.FileSystemInfo.get_Exists",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.IO.StreamWriter..ctor",
                ],
                findings);
        }
    }
}

// Planted references for the rule tests: never called, only scanned.
namespace jd.cli.tests.planted
{
    internal sealed record Piece(string Text);

    internal sealed record Input(string Name, Piece Piece);

    internal record Problem(string Message);

    internal sealed record MissingFile(string Message, string Path) : Problem(Message);

    internal sealed record Result(IReadOnlyList<Problem> Problems);

    internal static class UseCase
    {
        public static Task<Result> RunAsync(Input input, Connections connections, CancellationToken cancellationToken) =>
            Task.FromResult(new Result([new Problem($"{input.Name} {connections} {cancellationToken.IsCancellationRequested}")]));
    }

    internal static class Command
    {
        public static async Task<string> RunsTheUseCaseAsync(Connections connections) =>
            (await UseCase.RunAsync(new Input("x", new Piece("y")), connections, CancellationToken.None)).Problems[0].Message + GraphJson.Serialize(null!);

        public static object Resolves() => Resolver.Resolve(null!, null!, null!, null!);

        public static IRecordStore ReadsTheRecords(Connections connections) => connections.Records();

        public static Func<Task> CallsAPortInALambda(IRecordStore records) => () => records.ListAsync("dev", "1.0.0", CancellationToken.None);

        public static void TakesAPortFromACall(Connections connections) => _ = Composition.Records(connections);

        public static async Task HoldsAPortAcrossAnAwait(Connections connections)
        {
            var store = connections.Records(); // hoisted into a field of the state machine
            await Task.Yield();
            await store.ListAsync("dev", "1.0.0", CancellationToken.None);
        }

        public static object ResolvesInALocalFunction()
        {
            return Resolve();

            static object Resolve() => Resolver.Resolve(null!, null!, null!, null!);
        }

        public static Task HoldsAPortInAnAsyncLocalFunction(Connections connections)
        {
            return HoldAsync(connections);

            static async Task HoldAsync(Connections connections)
            {
                var store = connections.Records();
                await Task.Yield();
                await store.ListAsync("dev", "1.0.0", CancellationToken.None);
            }
        }

        internal static class Nested
        {
            public static object Resolves() => Resolver.Resolve(null!, null!, null!, null!);

            public static void Holds(IRecordStore records) => _ = records;
        }
    }

    internal static class ValidateCommand
    {
        public static async Task<string> ValidatesAsync() =>
            string.Join('\n', (await Validate.RunAsync(new ValidateInput(new SourceDocument("x", "kind: Workload"u8.ToArray())), CancellationToken.None)).Errors.Select(e => e.File + e.Location + e.Message));

        public static string DecodesTheDocument() => new SourceDocument("x", "kind: Workload"u8.ToArray()).Text();
    }

    internal static class WorkloadCommand
    {
        private static readonly SourceDocument Document = new("x", "kind: Workload"u8.ToArray());

        public static async Task<string> DeploysAsync(Connections connections)
        {
            var lines = new List<string>();
            var result = await Deploy.RunAsync(
                new DeployInput(Document, Document, new SourceCatalog("c", [Document]), false), connections, p => lines.Add(p is NodeFinished f ? f.Report.NodeId : string.Empty), CancellationToken.None);
            var graph = (await Preview.RunAsync(new PreviewInput(Document, Document, new SourceCatalog("c", [Document])), CancellationToken.None)).Graph;
            return $"{result.Run?.Nodes.Count}{(result.Problem as Gated)?.Environment}{graph?.Workload}{string.Concat(lines)}";
        }

        public static object? ChecksTheGate(EnvironmentDescriptor environment) => Checks.Gate(environment);

        public static int ReadsTheCatalog() => new SourceCatalog("c", [Document]).Documents.Count;

        public static object BuildsTheTemplates(Connections connections) => connections.Templates(new SourceCatalog("c", []));
    }

    internal static class EnvCommand
    {
        private static readonly SourceDocument Document = new("x", "kind: EnvironmentDefinition"u8.ToArray());

        public static async Task<string> UpsAsync(Connections connections)
        {
            var lines = new List<string>();
            var result = await EnvUp.RunAsync(
                new EnvUpInput(Document, new SourceCatalog("c", [Document]), null), connections, p => lines.Add(p is NodeFinished f ? f.Report.NodeId : string.Empty), CancellationToken.None);
            return $"{result.Run?.Succeeded}{result.Environment?.Name}{result.Environment?.Region}{result.Descriptor?.Length}{string.Concat(lines)}";
        }

        public static object LoadsTheDefinition() => EnvironmentDefinitionFile.LoadAsync(Document, CancellationToken.None);

        public static object ChecksTheType(Catalog catalog, EnvironmentDescriptor environment) => Checks.EnvironmentType(catalog, environment, "x");

        public static object LocatesTheCatalogErrors() => new SourceCatalog("c", [Document]).Located([]);

        public static object ComposesTheDescriptor(EnvironmentDefinition definition, Catalog catalog, EnvironmentDescriptor environment, ResolvedGraph graph) =>
            new DescriptorComposer(definition, "x", catalog, environment, graph);
    }

    internal static class ReleaseCommand
    {
        private static readonly SourceDocument Document = new("x", "kind: ReleaseManifest"u8.ToArray());

        private static readonly DocumentReader Nothing = (reference, _) => Task.FromResult((reference, (ReadOnlyMemory<byte>?)null));

        public static async Task<string> CreatesAndShowsAsync(Connections connections)
        {
            var created = await CreateRelease.RunAsync(new CreateReleaseInput(Document, Nothing), connections, CancellationToken.None);
            var shown = await ShowRelease.RunAsync(new ShowReleaseInput(Document), CancellationToken.None);
            return $"{created.Set?.Label}{created.Document?.Length}{created.Errors.Count}{shown.CreatedAt}{shown.Set?.Workloads[0].Image}";
        }

        public static object BuildsTheSet() => ReleaseBuilder.BuildAsync(Document, Nothing, DateTimeOffset.UnixEpoch, CancellationToken.None);

        public static object SerializesTheSet(ReleaseSet set) => ReleaseSetFile.Serialize(set);

        public static object ReadsTheClock(Connections connections) => connections.Time();
    }

    internal sealed class Store : IRecordStore
    {
        public Task<string> CreateAsync(string environment, string label, string name, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListAsync(string environment, string label, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<byte[]> ReadAsync(string environment, string label, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    internal static class Composition
    {
        public static object Connect() => Resolver.Resolve(null!, null!, null!, null!);

        public static IRecordStore Records(Connections connections) => connections.Records();
    }

    internal static class Inner
    {
        public static bool Touches(Stream stream)
        {
            using var fromPath = new StreamReader("x");
            using var fromStream = new StreamReader(stream);
            Console.WriteLine();

            // 8-byte operands: read as code, the first would be a call to no token.
            return stream.Length * 0x7F7F_7F7F_7F7F_7F28L > 0.5 * stream.Position
                && File.Exists("x") && Environment.GetEnvironmentVariable("X") is null && Path.GetFullPath("x").Length > Path.Combine("a", "b").Length;
        }

        public static async Task TouchesAsync()
        {
            await Task.Yield();
            _ = Directory.Exists("x");
        }

        // One reference to each remaining arm of R3, and the StreamWriter overload on a stream, which is not one.
        public static void TouchesMore(Stream stream)
        {
            FileSystemInfo info = new FileInfo("x");
            _ = new DirectoryInfo("x");
            _ = info.Exists;
            _ = new FileStream("x", FileMode.Open);
            _ = new StreamWriter("x");
            _ = new StreamWriter(stream);
            _ = Environment.GetEnvironmentVariables();
            _ = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Environment.CurrentDirectory = Environment.CurrentDirectory;
        }
    }
}

// A RunAsync outside the use cases' namespace: not a use case.
namespace jd.cli.tests.planted.elsewhere
{
    internal static class NotAUseCase
    {
        public static Task RunAsync() => Task.CompletedTask;
    }
}
