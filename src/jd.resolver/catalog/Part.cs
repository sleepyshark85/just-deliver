namespace jd.resolver.catalog;

/// <summary>
/// A set of nodes deployed together, and the exports evaluated from them (docs/architecture/documents.md section 2, concept 3), as the binder
/// read it from its owner's file.
/// </summary>
public sealed record Part
{
    /// <summary>Where the part is in its file; empty for a part that is the whole document.</summary>
    public required string Location { get; init; }

    /// <summary>The layer that deploys the part, or a scope that is no layer: deployed with each workload.</summary>
    public required string Scope { get; init; }

    /// <summary>The select map entries the part is in, outermost first: its implementation, then its class.</summary>
    public required IReadOnlyList<SelectKey> Keys { get; init; }

    /// <summary>The shared resources the part may read as <c>${&lt;name&gt;.&lt;export&gt;}</c>.</summary>
    public required IReadOnlyList<string> Reads { get; init; }

    /// <summary>Where <see cref="Reads"/> is in the file, which may be its owner's; the part's own <see cref="Location"/> when it reads none.</summary>
    public required string ReadsLocation { get; init; }

    /// <summary>The nodes, by name.</summary>
    public required IReadOnlyDictionary<string, Node> Nodes { get; init; }

    /// <summary>The exports, by name, each an expression as written.</summary>
    public required IReadOnlyDictionary<string, string> Exports { get; init; }

    /// <summary>Where the exports are in the file; the part's own <see cref="Location"/> when it has none.</summary>
    public required string ExportsLocation { get; init; }

    /// <summary>Where each export is in the file, by name: the location of an error about that export.</summary>
    public required IReadOnlyDictionary<string, string> ExportLocations { get; init; }
}
