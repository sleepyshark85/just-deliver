namespace jd.resolver.tests;

// Catalog snippets shared by the tests that build a small catalog in memory.
internal static class TestCatalog
{
    /// <summary>The runtime mapping every workload needs: one node named runtime. Append it after the other files so their indexes stay.</summary>
    public const string RuntimeMapping = "kind: Mapping\nmatch: { kind: runtime }\nprobe: { path: /health, expectedStatus: 200 }\nnodes:\n  runtime:\n    template: t/runtime\n    config: { k: 1 }\n";
}
