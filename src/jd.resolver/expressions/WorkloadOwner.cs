namespace jd.resolver.expressions;

/// <summary>
/// Who a run belongs to, as <c>${workload.…}</c> reads it and <c>name()</c> hashes it: a workload, or an environment definition's run, named
/// <c>@</c> and the environment's name, with no team, image or port.
/// </summary>
/// <param name="Name">The workload's name, or <c>@</c> and the environment's name.</param>
/// <param name="Team">The team that owns the workload; empty for an environment definition.</param>
/// <param name="Image">The workload's image; null for an environment definition.</param>
/// <param name="Port">The workload's first port; null when it declares none, and for an environment definition.</param>
public sealed record WorkloadOwner(string Name, string Team, string? Image, int? Port);
