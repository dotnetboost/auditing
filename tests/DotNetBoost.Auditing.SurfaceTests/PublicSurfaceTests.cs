using DotNetBoost.Auditing.Core;
using System.Reflection;

namespace DotNetBoost.Auditing.SurfaceTests;

/// <summary>
/// The public surface of each shipped package, against a baseline committed beside it.
/// </summary>
/// <remarks>
/// <b>What this is for.</b> Five packages expose fifty-odd public types between them, and nothing
/// in a build objects when one more appears — so a type added in passing becomes a compatibility
/// commitment that nobody decided to make. With a baseline, every addition, removal and re-signing
/// turns up as a diff on a file next to the code that caused it, where it gets a yes or a no.
/// <para>
/// <b>Why a test and not <c>Microsoft.CodeAnalysis.PublicApiAnalyzers</c>.</b> The analyzer is the
/// standard answer and fails the build rather than the suite, which is better. Its baseline,
/// though, can only be <i>created</i> by its IDE code fix — <c>dotnet format</c> does not apply it —
/// and a file that can only be regenerated inside an IDE does not belong in a repository whose
/// verification is otherwise entirely command-line. This renders its own format instead, which also
/// allowed two things the analyzer would not have given: the baselines are shared across both
/// shipped frameworks, so an API that exists on one and not the other fails; and the text reads as
/// C#, because the whole point is that a person reviews the diff.
/// </para>
/// </remarks>
public class PublicSurfaceTests
{
    /// <summary>Set to <c>update</c> to rewrite the baselines instead of asserting against them.</summary>
    private const string UpdateVariable = "DOTNETBOOST_SURFACE";

    /// <summary>Every package that ships. The list the baselines are keyed on.</summary>
    private static readonly string[] Shipped =
    [
        "DotNetBoost.Auditing.Core",
        "DotNetBoost.Auditing.API",
        "DotNetBoost.Auditing.Dapper",
        "DotNetBoost.Auditing.EntityFrameworkCore",
        "DotNetBoost.Auditing.MongoDb"
    ];

    public static TheoryData<string> Packages() => [.. Shipped];

    [Theory]
    [MemberData(nameof(Packages))]
    public void The_public_surface_is_the_one_that_was_agreed(string package)
    {
        var assembly = Assembly.Load(package);
        var rendered = PublicSurface.Render(assembly);
        var baseline = BaselinePath(package);

        if (string.Equals(Environment.GetEnvironmentVariable(UpdateVariable), "update", StringComparison.Ordinal))
        {
            File.WriteAllText(baseline, rendered);
            return;
        }

        Assert.True(
            File.Exists(baseline),
            $"No baseline for {package} at {baseline}. Create it with " +
            $"{UpdateVariable}=update dotnet test tests/DotNetBoost.Auditing.SurfaceTests");

        var expected = File.ReadAllText(baseline);

        if (string.Equals(Normalise(expected), Normalise(rendered), StringComparison.Ordinal))
            return;

        // Written out rather than only described, because the useful question is "what changed"
        // and a diff of two files answers it where an assertion message cannot.
        var actual = baseline + ".actual";
        File.WriteAllText(actual, rendered);

        Assert.Fail(
            $"The public surface of {package} is not the one in {Path.GetFileName(baseline)}.\n" +
            $"Written to {Path.GetFileName(actual)} — diff the two.\n" +
            "An intended change: accept it with " +
            $"{UpdateVariable}=update dotnet test tests/DotNetBoost.Auditing.SurfaceTests, and commit the\n" +
            "baseline with the change that caused it. An unintended one: that is what this test is for.\n\n" +
            Difference(expected, rendered));
    }

    [Fact]
    public void Every_shipped_package_is_covered()
    {
        // A sixth package added without a baseline would otherwise be unguarded and silent, which
        // is the failure this whole suite exists to prevent — one level up.
        var shipped = Directory
            .EnumerateDirectories(Path.Combine(Repository(), "src"))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Shipped.OrderBy(n => n, StringComparer.Ordinal), shipped);
    }

    [Fact]
    public void The_meter_name_an_application_subscribes_to_is_part_of_the_surface() =>
        // Not a type, so no baseline covers it, and renaming it would turn every dashboard built
        // on it silently quiet. See AuditingTelemetry.
        Assert.Equal("DotNetBoost.Auditing", AuditingTelemetry.MeterName);

    private static string BaselinePath(string package) =>
        Path.Combine(Repository(), "src", package, "PublicApi.txt");

    /// <summary>The repository root, found by walking up from the test assembly.</summary>
    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotNetBoost.Auditing.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>Line endings differ between the machine that wrote a baseline and the one reading it.</summary>
    private static string Normalise(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    /// <summary>The first few differing lines, which is usually the whole story.</summary>
    private static string Difference(string expected, string actual)
    {
        var left  = Normalise(expected).Split('\n');
        var right = Normalise(actual).Split('\n');

        var removed = left.Except(right, StringComparer.Ordinal).Take(10).ToList();
        var added   = right.Except(left, StringComparer.Ordinal).Take(10).ToList();

        var report = new List<string>();

        if (removed.Count > 0)
            report.Add("Gone from the surface (a break for anyone using it):\n  " + string.Join("\n  ", removed));

        if (added.Count > 0)
            report.Add("New on the surface (a commitment to keep it):\n  " + string.Join("\n  ", added));

        return string.Join("\n\n", report);
    }
}
