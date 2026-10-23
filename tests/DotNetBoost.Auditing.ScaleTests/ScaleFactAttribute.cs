namespace DotNetBoost.Auditing.ScaleTests;

/// <summary>
/// A measurement, not a test. Skipped unless <c>DOTNETBOOST_SCALE=run</c> is set.
/// </summary>
/// <remarks>
/// <b>Two guards, deliberately.</b> This project is not in the CI workflow's list of test projects,
/// so CI compiles it and never runs it — and the attribute means that someone running
/// <c>dotnet test</c> across the solution gets a visible skip rather than a ten-minute surprise.
/// Skipped rather than quietly passing, because a measurement that reports success without
/// measuring anything is worse than one that says it did not run.
/// <para>
/// They are excluded because a benchmark is not a gate. These numbers vary with the machine, the
/// container runtime and what else is running, so failing a build on them would mean failing builds
/// for reasons nobody can act on. They are run deliberately, before a release, and what they produce
/// is a paragraph in <c>docs/testing.md</c> rather than a green tick.
/// </para>
/// </remarks>
public sealed class ScaleFactAttribute : FactAttribute
{
    public ScaleFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DOTNETBOOST_SCALE"), "run", StringComparison.Ordinal))
        {
            Skip = "A measurement rather than a gate. Set DOTNETBOOST_SCALE=run to take it; " +
                   "it builds a million-entry trail and takes minutes.";
        }
    }
}
