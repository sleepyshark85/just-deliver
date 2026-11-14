using Newtonsoft.Json.Linq;

namespace jd.resolver.catalog;

/// <summary>
/// A catalog node as written: the template it deploys, its kind and its config, expressions not yet evaluated. <see cref="Location"/>,
/// <see cref="ConfigLocation"/> and <see cref="TemplateLocation"/>: where the node, its config and its template are in their file.
/// </summary>
public sealed record Node(string Template, NodeKind Kind, IReadOnlyDictionary<string, JToken> Config, string Location, string ConfigLocation, string TemplateLocation);
