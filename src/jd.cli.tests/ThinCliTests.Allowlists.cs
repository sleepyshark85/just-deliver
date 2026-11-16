namespace jd.cli.tests;

// The references each thin-CLI rule lets through, by referring type: none (application.md section 2). An entry would be
// an exception to a rule, which needs the user's decision; a stale entry fails.
public sealed partial class ThinCliTests
{
    private static readonly Dictionary<string, string[]> R1Allowlist = [];

    private static readonly Dictionary<string, string[]> R2Allowlist = [];

    private static readonly Dictionary<string, string[]> R3Allowlist = [];
}
