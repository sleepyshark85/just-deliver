namespace jd.resolver;

/// <summary>What loading a file gave: <see cref="Value"/> is set exactly when <see cref="Errors"/> is empty.</summary>
/// <param name="Value">What was loaded, when there are no errors.</param>
/// <param name="Errors">Every problem found, each at its file and location.</param>
public sealed record LoadResult<T>(T? Value, IReadOnlyList<LoadError> Errors)
    where T : class;
