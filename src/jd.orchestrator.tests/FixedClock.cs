namespace jd.orchestrator.tests;

// A clock that always reads the same time, so what a test builds from it is the same on every call.
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
