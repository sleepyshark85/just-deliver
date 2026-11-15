namespace jd.resolver.environment;

/// <summary>The descriptor as YAML text, set exactly when <see cref="Errors"/> is empty and the text passes <see cref="EnvironmentParser"/>.</summary>
public sealed record ComposedDescriptor(string? Yaml, IReadOnlyList<LoadError> Errors);
