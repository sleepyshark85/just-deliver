namespace jd.resolver.environment;

/// <summary><see cref="Descriptor"/> is set exactly when <see cref="Errors"/> is empty.</summary>
public sealed record EnvironmentLoadResult(EnvironmentDescriptor? Descriptor, IReadOnlyList<LoadError> Errors);
