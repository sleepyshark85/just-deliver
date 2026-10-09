namespace jd.resolver.release;

/// <summary>One workload in a release: its identity, the definition exactly as received (ADR 0007's snapshot) and what pins it.</summary>
/// <param name="Name">The workload's <c>metadata.name</c>.</param>
/// <param name="Team">The workload's <c>metadata.team</c>.</param>
/// <param name="DefinitionSha256">Lowercase hex SHA-256 over the exact bytes of the definition file.</param>
/// <param name="Definition">The definition file content as received.</param>
/// <param name="Image">The image reference, pinned by digest.</param>
/// <param name="DependsOn">Names of workloads that deploy first.</param>
public sealed record ReleaseWorkload(string Name, string Team, string DefinitionSha256, string Definition, string Image, IReadOnlyList<string> DependsOn);

/// <summary>A pinned set of workloads under one label (C53). Immutable once written.</summary>
/// <param name="Label">The release label.</param>
/// <param name="CreatedAt">UTC, to the second.</param>
/// <param name="Workloads">The workloads, in manifest order.</param>
/// <param name="Order">Workload names in deploy order: dependencies first, ties by name.</param>
public sealed record ReleaseSet(string Label, DateTimeOffset CreatedAt, IReadOnlyList<ReleaseWorkload> Workloads, IReadOnlyList<string> Order);

/// <summary><see cref="Set"/> is set exactly when <see cref="Errors"/> is empty.</summary>
/// <param name="Set">The release set, when there are no errors.</param>
/// <param name="Errors">Every problem found.</param>
public sealed record ReleaseSetResult(ReleaseSet? Set, IReadOnlyList<LoadError> Errors);
