using Newtonsoft.Json.Linq;

namespace jd.resolver.catalog;

/// <summary>
/// A catalog node as written: the template it deploys, its kind and its config, expressions not yet evaluated. <see cref="Location"/> and
/// <see cref="ConfigLocation"/>: where the node and its config are in their file.
/// </summary>
public sealed record Node(string Template, NodeKind Kind, IReadOnlyDictionary<string, JToken> Config, string Location, string ConfigLocation);
