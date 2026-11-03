using System.Reflection.Metadata;
using jd.core.records;
using jd.orchestrator.usecases;
using jd.resolver;
using jd.resolver.graph;
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
            var findings = R1(Planted, Only(typeof(planted.Command)), UseCaseSurface([typeof(planted.UseCase)])).Select(f => f.Message).ToList();

            Assert.Equal(
                [
                    "jd.cli.tests.planted.Command.Resolves reaches jd.resolver.Resolver.Resolve",
                    "jd.cli.tests.planted.Command.ReadsTheRecords reaches jd.orchestrator.usecases.Connections.get_Records",
                    "jd.cli.tests.planted.Command.CallsAPortInALambda reaches jd.core.records.IRecordStore.ListAsync",
                ],
                findings);
        }

        [Fact]
        public void A_use_case_is_a_type_with_a_static_run_and_its_surface_is_its_input_and_the_getters_of_its_signature_without_the_connections()
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
                    ("jd.cli.tests.planted.Result", "get_Problems"),
                    ("jd.cli.tests.planted.Problem", "get_Message"),
                    ("jd.cli.tests.planted.MissingFile", "get_Path"),
                },
                surface.ToHashSet());
        }

        [Fact]
        public void R2_names_the_type_and_member_that_name_a_port_outside_the_composition_and_the_composition_that_bypasses_one()
        {
            var findings = R2(Planted, Only(typeof(planted.Command), typeof(planted.Store), typeof(planted.Composition)), Only(typeof(planted.Composition))).Select(f => f.Message).Distinct().ToList();

            Assert.Equal(
                [
                    "jd.cli.tests.planted.Command.ReadsTheRecords names the port jd.core.records.IRecordStore",
                    "jd.cli.tests.planted.Command.CallsAPortInALambda names the port jd.core.records.IRecordStore",
                    "jd.cli.tests.planted.Command.TakesAPortFromACall names the port jd.core.records.IRecordStore",
                    "jd.cli.tests.planted.Store.(its interfaces) names the port jd.core.records.IRecordStore",
                    "jd.cli.tests.planted.Composition.Connect reaches jd.resolver.Resolver.Resolve, which is not a port",
                ],
                findings);
        }

        [Fact]
        public void R3_names_the_type_and_member_that_touch_a_file_the_environment_or_the_console_and_nothing_else()
        {
            var findings = R3(Planted, Only(typeof(planted.Inner))).Select(f => f.Message).ToList();

            Assert.Equal(
                [
                    "jd.cli.tests.planted.Inner.Touches reaches System.IO.StreamReader..ctor",
                    "jd.cli.tests.planted.Inner.Touches reaches System.Console.WriteLine",
                    "jd.cli.tests.planted.Inner.Touches reaches System.IO.File.Exists",
                    "jd.cli.tests.planted.Inner.Touches reaches System.Environment.GetEnvironmentVariable",
                    "jd.cli.tests.planted.Inner.Touches reaches System.IO.Path.GetFullPath",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.IO.FileInfo..ctor",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.IO.DirectoryInfo..ctor",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.IO.FileSystemInfo.get_Exists",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.IO.FileStream..ctor",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.IO.StreamWriter..ctor",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.Environment.GetEnvironmentVariables",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.Environment.GetFolderPath",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.Environment.get_CurrentDirectory",
                    "jd.cli.tests.planted.Inner.TouchesMore reaches System.Environment.set_CurrentDirectory",
                    "jd.cli.tests.planted.Inner.TouchesAsync reaches System.IO.Directory.Exists",
                ],
                findings);
        }
    }
}

// Planted references for the rule tests: never called, only scanned.
namespace jd.cli.tests.planted
{
    internal sealed record Input(string Name);

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
            (await UseCase.RunAsync(new Input("x"), connections, CancellationToken.None)).Problems[0].Message + GraphJson.Serialize(null!);

        public static object Resolves() => Resolver.Resolve(null!, null!, null!, null!);

        public static IRecordStore ReadsTheRecords(Connections connections) => connections.Records();

        public static Func<Task> CallsAPortInALambda(IRecordStore records) => () => records.ListAsync("dev", "1.0.0", CancellationToken.None);

        public static void TakesAPortFromACall(Connections connections) => _ = Composition.Records(connections);
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
