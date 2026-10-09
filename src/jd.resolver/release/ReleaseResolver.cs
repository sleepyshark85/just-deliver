using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.graph;
using jd.resolver.workload;

namespace jd.resolver.release;

/// <summary>Resolves every workload of a <see cref="ReleaseSet"/> on one environment: the same parser and <see cref="Resolver"/> as a single workload, nothing else.</summary>
public static class ReleaseResolver
{
    /// <summary>
    /// The graphs in <see cref="ReleaseSet.Order"/>, or every error found (resolution continues past a failing workload, so one pass reports all).
    /// Errors name <c>&lt;set file&gt;#&lt;workload&gt;</c>, the place the definition lives.
    /// </summary>
    public static async Task<(IReadOnlyList<ResolvedGraph> Graphs, IReadOnlyList<LoadError> Errors)> ResolveAsync(
        string setFile, ReleaseSet set, Catalog catalog, EnvironmentDescriptor environment, CancellationToken cancellationToken = default)
    {
        var graphs = new List<ResolvedGraph>();
        var errors = new List<LoadError>();
        foreach (var name in set.Order)
        {
            var source = $"{setFile}#{name}";
            // The set was composed from these definitions, so they parse; the YAML parser does not take the byte order mark the hash covers.
            var definition = set.Workloads.Single(w => w.Name == name).Definition.TrimStart('﻿');
            var loaded = await WorkloadFile.ParseAsync(source, definition, cancellationToken);
            if (loaded.Workload is not { } workload)
            {
                errors.AddRange(loaded.Errors);
                continue;
            }

            var graph = Resolver.Resolve(workload, source, catalog, environment);
            errors.AddRange(graph.Errors);
            graphs.Add(graph);
        }

        return (graphs, errors);
    }
}
