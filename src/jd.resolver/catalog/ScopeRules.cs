namespace jd.resolver.catalog;

/// <summary>
/// Who deploys what (docs/architecture/documents.md section 2, concept 5), over the bound part owners. The layers deploy in their order, so a shared
/// resource reads only shared resources of its layer or an earlier one, and no reads go round in a cycle. The parts one layer deploys share one
/// scope (docs/architecture/components.md section 3), so their node names are unique across owners and none takes a shared resource's name. Each
/// error is located where the binder read the part's reads or the node.
/// </summary>
internal static class ScopeRules
{
    /// <summary>Every problem with what the shared resources, by name, read: a resource of a later one of <paramref name="layers"/>, or a cycle.</summary>
    public static IReadOnlyList<LoadError> CheckReadOrder(IReadOnlyList<string> layers, IReadOnlyDictionary<string, PartOwner> shared)
    {
        // A shared resource is its one part.
        var order = layers.ToList();
        var later =
            from user in shared.Values
            let part = user.Parts[0]
            from used in part.Reads.Select(shared.GetValueOrDefault).OfType<PartOwner>()
            let usedPart = used.Parts[0]
            where order.IndexOf(usedPart.Scope) > order.IndexOf(part.Scope)
            select new LoadError(user.Source, part.ReadsLocation,
                $"'{used.Name}' is {usedPart.Scope} scope; a {part.Scope}-scope resource uses only {string.Join(" or ", layers.Take(order.IndexOf(part.Scope) + 1))}"
                + $"-scope ones, since the {part.Scope} layer is deployed first.");

        // A read name that is no (valid) shared resource is reported on its own, so the order runs over the shared resources only.
        var (_, cycle) = TopologicalOrder.Sort(shared.ToDictionary(s => s.Key, s => (IReadOnlyCollection<string>)[.. s.Value.Reads.Where(shared.ContainsKey)]));
        if (cycle is null)
        {
            return [.. later];
        }

        var first = shared[cycle[0]];
        return [.. later, new LoadError(first.Source, first.Parts[0].ReadsLocation, $"uses cycle: {string.Join(" -> ", cycle)}.")];
    }

    /// <summary>
    /// Every node of a part of one of <paramref name="layers"/>, among <paramref name="owners"/>' parts, that takes the name of one of
    /// <paramref name="shared"/> it does not read, or of a node another owner's part of the same layer declares first.
    /// </summary>
    public static IReadOnlyList<LoadError> CheckNodeNames(IReadOnlyList<string> layers, IEnumerable<PartOwner> owners, IReadOnlyDictionary<string, PartOwner> shared)
    {
        var nodes = (from owner in owners
                     from part in owner.Parts.Where(part => layers.Contains(part.Scope))
                     from node in part.Nodes
                     select (Owner: owner, Part: part, Name: node.Key, node.Value.Location)).ToList();

        // A run looks up nodes before parts, so the node would silently replace what ${<resource>.<export>} reads (components.md section 2); a
        // name the part itself reads is ReadRules'.
        var shadowing = nodes.Where(node => shared.ContainsKey(node.Name) && !node.Part.Reads.Contains(node.Name)).Select(node => new LoadError(
            node.Owner.Source, node.Location, $"node '{node.Name}' takes the name of shared resource '{node.Name}', which a part of the same run reads as ${{{node.Name}.<export>}}."));

        // A component's implementations may reuse a name, since one is selected; the first owner's nodes are the ones the others repeat.
        var repeated = from scope in nodes.GroupBy(node => (node.Part.Scope, node.Name))
                       let first = scope.First().Owner
                       from node in scope.Where(node => node.Owner.Name != first.Name)
                       select new LoadError(node.Owner.Source, node.Location,
                           $"node '{node.Name}' is also declared by '{first.Name}' ({first.Source}); node names are unique across the parts of one {scope.Key.Scope} scope.");
        return [.. shadowing, .. repeated];
    }

    /// <summary>
    /// Every implementation of <paramref name="owners"/> keyed by one of <paramref name="environmentTypes"/> of a layer before the last of
    /// <paramref name="layers"/>: only an environment of the last layer deploys a component or runtime part, so it would never be used. Whether
    /// a key is a declared type at all is the ref rule's.
    /// </summary>
    public static IReadOnlyList<LoadError> CheckImplementationKeys(
        IEnumerable<PartOwner> owners, IReadOnlyDictionary<string, EnvironmentType> environmentTypes, IReadOnlyList<string> layers) =>
        [.. from owner in owners
            from key in owner.Parts.SelectMany(part => part.Keys).Where(key => key.Role == Roles.EnvironmentType).Distinct()
            let layer = environmentTypes.GetValueOrDefault(key.Key)?.Layer
            where layer is not null && layer != layers[^1]
            select new LoadError(owner.Source, $"{key.MapLocation}.{key.Key}",
                $"'{key.Key}' is a {layer}-layer environment type; {key.Map} are keyed by {layers[^1]}-layer types or '{key.Fallback}'.")];
}
