namespace jd.resolver.catalog;

/// <summary>
/// A declared environment type; <see cref="Approval"/>: an environment of this type must carry an approval block. <see cref="Layer"/>: which
/// layer a <c>jd env up</c> of this type deploys.
/// </summary>
public sealed record EnvironmentType(bool Approval, string Layer);
