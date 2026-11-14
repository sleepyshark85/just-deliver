using jd.resolver.expressions;
using Newtonsoft.Json.Linq;

namespace jd.resolver.catalog;

/// <summary>
/// What a part reads (docs/architecture/documents.md section 2, concepts 3 and 4; security boundaries 3 and 5), over the bound part owners. Every
/// root an expression in a part's config or exports reads is one of its own nodes, a reserved root, a shared resource the part reads, or, in a
/// component's class part, its own component, read from that implementation's environment part; a read export is declared. No node takes a name
/// its part reads as a root, and only a runtime declares the runtime node. Each error is located where the binder read the node or the value.
/// </summary>
internal static class ReadRules
{
    // A part, and what it reads besides its own nodes, the reserved roots and its shared resources: for a class part, its own component
    // (OwnComponent), read from that implementation's environment part (OwnEnvironment, null when it has none).
    private sealed record Reader(PartOwner Owner, Part Part, bool IsRuntime, string? OwnComponent, Part? OwnEnvironment);

    // The part owners a root can name, by what they are; a name that is a component or runtime is read as that, even when a shared resource has it too.
    private sealed record Owners(IReadOnlyDictionary<string, PartOwner> Shared, IReadOnlySet<string> Components, IReadOnlySet<string> Runtimes);

    /// <summary>
    /// Every node-name problem, then every read problem, of the parts of <paramref name="shared"/> (<paramref name="sharedByName"/> by name, the first
    /// of a duplicate name), <paramref name="components"/> and <paramref name="runtimes"/>; a part of one of <paramref name="layers"/> before a part deployed with each workload.
    /// </summary>
    public static IReadOnlyList<LoadError> Check(
        IReadOnlyList<string> layers, IReadOnlyList<PartOwner> shared, IReadOnlyDictionary<string, PartOwner> sharedByName, IReadOnlyList<PartOwner> components,
        IReadOnlyList<PartOwner> runtimes)
    {
        List<Reader> readers =
        [
            .. shared.Concat(components).SelectMany(owner => owner.Parts.Where(part => layers.Contains(part.Scope))
                .Select(part => new Reader(owner, part, IsRuntime: false, OwnComponent: null, OwnEnvironment: null))),
            .. components.SelectMany(component => component.Parts.Where(part => !layers.Contains(part.Scope))
                .Select(part => new Reader(component, part, IsRuntime: false, component.Name, EnvironmentOf(component, part, layers)))),
            .. runtimes.SelectMany(runtime => runtime.Parts.Select(part => new Reader(runtime, part, IsRuntime: true, OwnComponent: null, OwnEnvironment: null))),
        ];
        var owners = new Owners(sharedByName, components.Select(owner => owner.Name).ToHashSet(), runtimes.Select(owner => owner.Name).ToHashSet());
        return [.. readers.SelectMany(NodeNameErrors), .. readers.SelectMany(reader => ReadErrors(reader, owners))];
    }

    // The environment part of the implementation a class part is in: the layer part whose selection keys begin the class part's.
    private static Part? EnvironmentOf(PartOwner component, Part part, IReadOnlyList<string> layers) =>
        component.Parts.FirstOrDefault(other => layers.Contains(other.Scope) && part.Keys.Take(other.Keys.Count).SequenceEqual(other.Keys));

    private static IEnumerable<LoadError> NodeNameErrors(Reader reader) =>
        from node in reader.Part.Nodes
        let problem = NodeNameProblem(reader, node.Key)
        where problem is not null
        select new LoadError(reader.Owner.Source, node.Value.Location, problem);

    // A node named like a root its part reads would make one of the two unreachable; the runtime's own node is the reserved root 'runtime'.
    // The name's form is the part schema's.
    private static string? NodeNameProblem(Reader reader, string name)
    {
        if (name == ExpressionEvaluator.RuntimeNode)
        {
            return reader.IsRuntime ? null : $"'{name}' is the runtime node, declared only by a runtime.";
        }

        var isRoot = ExpressionEvaluator.ReservedRoots.Contains(name) || reader.Part.Reads.Contains(name) || name == reader.OwnComponent;
        return isRoot ? $"'{name}' is a name this part reads as a root; rename the node." : null;
    }

    // Every ${...} path in the part's config values and exports, located at the value or the export.
    private static IEnumerable<LoadError> ReadErrors(Reader reader, Owners owners)
    {
        var values = reader.Part.Nodes.Values.SelectMany(node => node.Config.Values)
            .SelectMany(value => value is JContainer container ? container.DescendantsAndSelf() : [value])
            .OfType<JValue>().Where(value => value.Type == JTokenType.String).Select(value => (Location: value.Path, Text: value.ToString()));
        var exports = reader.Part.Exports.Select(export => (Location: reader.Part.ExportLocations[export.Key], Text: export.Value));
        return from read in values.Concat(exports)
               from path in ExpressionEvaluator.Paths(read.Text)
               let problem = ReadProblem(reader, path, owners)
               where problem is not null
               select new LoadError(reader.Owner.Source, read.Location, problem);
    }

    // Why the part may not read path, or null when it may.
    private static string? ReadProblem(Reader reader, string path, Owners owners)
    {
        var segments = path.Split('.');
        var root = segments[0];
        if (root == ExpressionEvaluator.EnvNamespace)
        {
            return segments is [_, var field] && ExpressionEvaluator.EnvFields.Contains(field) ? null : ExpressionEvaluator.EnvLimit(path);
        }

        if (ExpressionEvaluator.ReservedRoots.Contains(root) || reader.Part.Nodes.ContainsKey(root))
        {
            return null;
        }

        if (reader.Part.Reads.Contains(root))
        {
            // A read name that is no (valid) shared resource is reported on its own, at the reads or at its file. A shared resource is its one part.
            return owners.Shared.TryGetValue(root, out var used) ? ExportProblem(path, segments, used.Parts[0].Exports) : null;
        }

        if (root == reader.OwnComponent)
        {
            return reader.OwnEnvironment is { } environment
                ? ExportProblem(path, segments, environment.Exports)
                : $"'{path}': component '{root}' has no environment part in this implementation.";
        }

        if (owners.Components.Contains(root) && root == reader.Owner.Name)
        {
            return $"'{path}': an environment part cannot read its own component.";
        }

        if (owners.Components.Contains(root) || owners.Runtimes.Contains(root))
        {
            return $"'{path}' reads another component or runtime; a part reads only its own nodes, what it uses and, in a class, its own component.";
        }

        return owners.Shared.ContainsKey(root) ? $"'{path}' reads '{root}', which is not in uses; add it to the file's uses." : UnknownRoot(reader, path, root);
    }

    // A read of another part is <name>.<export>, its export one the part declares.
    private static string? ExportProblem(string path, string[] segments, IReadOnlyDictionary<string, string> exports) =>
        segments is [_, var export] && exports.ContainsKey(export) ? null : $"'{path}': '{segments[0]}' exports {string.Join(", ", exports.Keys)}.";

    private static string UnknownRoot(Reader reader, string path, string root)
    {
        var ownComponent = reader.OwnComponent is null ? string.Empty : $"'{reader.OwnComponent}', ";
        return $"'{path}' reads unknown root '{root}'; expected a node of this part ({string.Join(", ", reader.Part.Nodes.Keys)}), a name in uses, {ownComponent}"
            + $"or one of {string.Join(", ", ExpressionEvaluator.ReservedRoots.Order())}.";
    }
}
