using System.Reflection;
using jd.core.records;
using jd.definitionvalidator;
using jd.orchestrator;
using jd.orchestrator.usecases;
using jd.resolver;
using jd.resolver.graph;
using Xunit;

namespace jd.cli.tests;

// The thin-CLI rules of docs/architecture/application.md section 4, as an IL scan of jd.dll and of the inner assemblies. Each rule has an allowlist of
// the references it finds today, by type: a later S25 slice removes the entries its move makes obsolete, and a stale entry fails, so the lists only shrink.
public sealed partial class ThinCliTests
{
    private static readonly Assembly[] InnerAssemblies = [typeof(IRecordStore).Assembly, typeof(YamlSchemaValidator).Assembly, typeof(Resolver).Assembly, typeof(Orchestrator).Assembly];

    // A use case's one entry point (section 5).
    private const string RunAsync = "RunAsync";

    // The composition types of section 4; every other jd.cli type is command code.
    private static readonly string[] CompositionTypes =
        [.. new[] { typeof(Composition), typeof(DataDirectory), typeof(LocalRecordStore), typeof(PulumiTemplates), typeof(HttpRevisionProbe) }.Select(t => t.FullName!)];

    // A rule broken: Key names the referring type and what it reaches (the allowlist's entry), Message also the member that does it.
    internal sealed record Finding(string Key, string Message);

    private static bool IsInner(TypeName type) => InnerAssemblies.Any(a => a.GetName().Name == type.Assembly);

    private static bool IsComposition(string type) => CompositionTypes.Contains(type);

    private static IlScan Scan(Assembly assembly) => IlScan.Of(assembly.Location);

    // R1: command code reaches the inner assemblies only through the use cases' surface; it reads no member of Connections or of a port.
    internal static IEnumerable<Finding> R1(IlScan scan, Func<string, bool> isCommand, ISet<(string Type, string Member)> surface) =>
        scan.Uses.Where(use => isCommand(use.Type) && IsInner(use.Target) && !surface.Contains((use.Target.FullName, use.TargetMember)))
            .Select(use => new Finding($"{use.Type} -> {use.Target.FullName}.{use.TargetMember}", $"{use.Where} reaches {use.Target.FullName}.{use.TargetMember}"));

    // What R1 lets command code reach, computed from the use cases' signatures: each RunAsync, the constructors of its input and of the types the input
    // holds (and nothing else of them), the property getters of every other type its signature holds (result, progress, problem and the data types in
    // them), and GraphJson.Serialize.
    internal static ISet<(string Type, string Member)> UseCaseSurface(IEnumerable<Type> useCases)
    {
        var surface = new HashSet<(string, string)> { (typeof(GraphJson).FullName!, nameof(GraphJson.Serialize)) };
        foreach (var run in useCases.SelectMany(u => u.GetMethods(BindingFlags.Public | BindingFlags.Static)).Where(m => m.Name == RunAsync))
        {
            surface.Add((Definition(run.DeclaringType!), run.Name));
            surface.UnionWith(Held(run.GetParameters()[0].ParameterType).Select(type => (Definition(type), ConstructorInfo.ConstructorName)));
            surface.UnionWith(run.GetParameters().Skip(1).Select(p => p.ParameterType).Append(run.ReturnType).SelectMany(Held)
                .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .Select(property => property.GetMethod).OfType<MethodInfo>().Select(getter => (Definition(getter.DeclaringType!), getter.Name)));
        }

        return surface;
    }

    // The jd types a signature's type holds, itself included: through generic arguments, element types and public properties, recursively, with the
    // records derived from each. Not Connections, which a command only passes on, and not the BCL's, whose members are not jd's.
    private static IEnumerable<Type> Held(Type root)
    {
        var held = new HashSet<Type>();
        void Hold(Type type)
        {
            if (type == typeof(Connections) || type.IsGenericParameter || !held.Add(type))
            {
                return;
            }

            foreach (var part in type.GetGenericArguments().Concat(type.HasElementType ? [type.GetElementType()!] : []))
            {
                Hold(part);
            }

            if (IsBcl(type))
            {
                return;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Hold(property.PropertyType);
            }

            foreach (var derived in type.Assembly.GetTypes().Where(t => t.IsSubclassOf(type)))
            {
                Hold(derived);
            }
        }

        Hold(root);
        return held.Where(type => !IsBcl(type));
    }

    private static bool IsBcl(Type type) => type.Namespace?.StartsWith("System", StringComparison.Ordinal) ?? true;

    // The use cases: the types with a static RunAsync in the namespace of the anchor (Connections' for jd's).
    internal static IEnumerable<Type> UseCases(Type anchor) =>
        anchor.Assembly.GetTypes().Where(t => t.Namespace == anchor.Namespace && t.GetMethod(RunAsync, BindingFlags.Public | BindingFlags.Static) is not null);

    private static string Definition(Type type) => (type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName!;

    // What each of Connections' factories builds (its delegate's last type argument), and what it takes from a request (the others).
    private static Type Built(PropertyInfo factory) => factory.PropertyType.GetGenericArguments()[^1];

    private static IEnumerable<Type> Taken(PropertyInfo factory) => factory.PropertyType.GetGenericArguments()[..^1];

    // The ports: the interfaces Connections' factories build.
    private static string[] Ports() =>
        typeof(Connections).GetProperties().Select(Built).Where(t => t.IsInterface).Select(Definition).ToArray();

    // The types in the ports' signatures, other than the BCL's.
    private static string[] PortSignatureTypes()
    {
        IEnumerable<Type> Parts(Type type) => type.IsGenericType ? type.GetGenericArguments().SelectMany(Parts).Prepend(type) : type.HasElementType ? Parts(type.GetElementType()!) : [type];
        return typeof(Connections).GetProperties().Select(Built).Where(t => t.IsInterface)
            .SelectMany(port => port.GetMethods()).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)).SelectMany(Parts)
            .Where(t => !IsBcl(t) && !t.IsGenericParameter).Select(Definition).Distinct().ToArray();
    }

    // R2, first sentence: a port is named (implemented, called, held) only in the composition types, and they reach the inner assemblies only through
    // the ports, the types in the ports' signatures, what a factory takes from a request (and the types it holds) and Connections.
    internal static IEnumerable<Finding> R2(IlScan scan, Func<string, bool> inScope, Func<string, bool> isComposition)
    {
        var ports = Ports();
        var named = scan.Mentions.Where(m => inScope(m.Type) && !isComposition(m.Type) && ports.Contains(m.Named.FullName))
            .Select(m => new Finding($"{m.Type} names {m.Named.FullName}", $"{m.Where} names the port {m.Named.FullName}"));
        var taken = typeof(Connections).GetProperties().SelectMany(Taken).SelectMany(Held).Select(Definition);
        var through = ports.Concat(PortSignatureTypes()).Concat(taken).Append(Definition(typeof(Connections))).ToHashSet();
        var reached = scan.Uses.Where(use => inScope(use.Type) && isComposition(use.Type) && IsInner(use.Target) && !through.Contains(use.Target.FullName))
            .Select(use => new Finding($"{use.Type} -> {use.Target.FullName}.{use.TargetMember}", $"{use.Where} reaches {use.Target.FullName}.{use.TargetMember}, which is not a port"));
        return named.Concat(reached);
    }

    // R3: the inner assemblies touch no file, directory, environment variable, current directory or console.
    internal static IEnumerable<Finding> R3(IlScan scan, Func<string, bool> inScope) =>
        scan.Uses.Where(use => inScope(use.Type) && TouchesTheMachine(use))
            .Select(use => new Finding($"{use.Type} -> {use.Target.FullName}.{use.TargetMember}", $"{use.Where} reaches {use.Target.FullName}.{use.TargetMember}"));

    private static bool TouchesTheMachine(MemberUse use) => use.Target.FullName switch
    {
        "System.IO.File" or "System.IO.Directory" or "System.IO.FileInfo" or "System.IO.DirectoryInfo" or "System.IO.FileSystemInfo" or "System.IO.FileStream" or "System.Console" => true,
        "System.IO.StreamReader" or "System.IO.StreamWriter" => use.TargetMember == ConstructorInfo.ConstructorName && use.Parameters.FirstOrDefault() == "System.String",
        "System.IO.Path" => use.TargetMember == "GetFullPath",
        "System.Environment" => use.TargetMember is "GetEnvironmentVariable" or "GetEnvironmentVariables" or "GetFolderPath" or "get_CurrentDirectory" or "set_CurrentDirectory",
        _ => false,
    };

    private static IEnumerable<Finding> InnerFindings() =>
        InnerAssemblies.SelectMany(assembly => R3(Scan(assembly), _ => true));

    // Each finding not on the allowlist, and each entry that names no finding, as a line of its own, in full.
    internal static IEnumerable<string> Breaches(IEnumerable<Finding> findings, IReadOnlyCollection<string> allowlist)
    {
        var found = findings.ToList();
        var unlisted = found.Where(f => !allowlist.Contains(f.Key)).Select(f => f.Message).Distinct().Order(StringComparer.Ordinal);
        var stale = allowlist.Except(found.Select(f => f.Key)).Order(StringComparer.Ordinal).Select(entry => $"stale allowlist entry: {entry}");
        return unlisted.Concat(stale);
    }

    // Assert.Empty would cut each line and list five; this prints them all.
    private static void AssertOnly(IEnumerable<Finding> findings, IReadOnlyCollection<string> allowlist)
    {
        var breaches = Breaches(findings, allowlist).ToList();
        if (breaches.Count > 0)
        {
            Assert.Fail(string.Join('\n', breaches));
        }
    }

    private static IEnumerable<string> Keys(Dictionary<string, string[]> allowlist, string separator) =>
        allowlist.SelectMany(entry => entry.Value.Select(reached => $"{entry.Key}{separator}{reached}"));

    [Fact]
    public void Command_code_reaches_the_inner_assemblies_only_through_the_use_cases_beyond_todays_allowlist() =>
        AssertOnly(R1(Scan(typeof(Cli).Assembly), type => !IsComposition(type), UseCaseSurface(UseCases(typeof(Connections)))), Keys(R1Allowlist, " -> ").ToList());

    [Fact]
    public void Only_the_composition_names_a_port_and_it_reaches_the_inner_assemblies_only_through_the_ports_beyond_todays_allowlist() =>
        AssertOnly(R2(Scan(typeof(Cli).Assembly), _ => true, IsComposition), Keys(R2Allowlist, " names ").ToList());

    [Fact]
    public void The_inner_assemblies_touch_no_file_environment_or_console_beyond_todays_allowlist() =>
        AssertOnly(InnerFindings(), Keys(R3Allowlist, " -> ").ToList());
}
