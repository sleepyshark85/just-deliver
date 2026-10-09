using System.Diagnostics;

namespace jd.resolver;

/// <summary>The one deterministic dependency order the resolver uses, for graph nodes and for the workloads of a release.</summary>
internal static class TopologicalOrder
{
    /// <summary>
    /// Kahn's algorithm taking the smallest ready name each time, so the order never depends on input order.
    /// </summary>
    /// <param name="dependsOn">Name to the distinct names it waits for; every name is a key.</param>
    /// <returns>
    /// The order, dependencies first. When the dependencies loop, the order holds only what could be ordered and the
    /// cycle lists its members, ending where it began; otherwise the cycle is null.
    /// </returns>
    public static (List<string> Order, List<string>? Cycle) Sort(IReadOnlyDictionary<string, IReadOnlyCollection<string>> dependsOn)
    {
        var waiting = dependsOn.ToDictionary(d => d.Key, d => d.Value.Count);
        var ready = new SortedSet<string>(waiting.Where(w => w.Value == 0).Select(w => w.Key), StringComparer.Ordinal);
        var order = new List<string>();
        while (ready.Count > 0)
        {
            var name = ready.Min ?? throw new UnreachableException("the loop runs only while ready is not empty");
            ready.Remove(name);
            order.Add(name);
            foreach (var dependent in dependsOn.Where(d => d.Value.Contains(name)))
            {
                if (--waiting[dependent.Key] == 0)
                {
                    ready.Add(dependent.Key);
                }
            }
        }

        if (order.Count == dependsOn.Count)
        {
            return (order, null);
        }

        // Each unordered name waits on another unordered one, so following dependencies must revisit a name: a cycle.
        var stuck = dependsOn.Keys.Except(order).ToHashSet();
        var path = new List<string>();
        var current = stuck.Order(StringComparer.Ordinal).First();
        while (!path.Contains(current))
        {
            path.Add(current);
            current = dependsOn[current].Order(StringComparer.Ordinal).First(stuck.Contains);
        }

        var cycle = path.Skip(path.IndexOf(current)).ToList();
        cycle.Add(current);
        return (order, cycle);
    }
}
