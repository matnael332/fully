using System.Text.RegularExpressions;

namespace WorkContinuity.Architecture.Tests;

/// <summary>
/// Gate 3F final owner review: current work is read through the current work view, never the raw Snapshot. The views
/// <c>active_snapshot_fields</c> and <c>active_snapshot_objects</c> present only what a participating connection's
/// enabled, baselined scopes hold; the raw Snapshot tables stay the projection's own state and, with the ledger and the
/// evidence, the history that provenance and audit read. No product reader of current work exists yet: this guard keeps
/// the raw tables to the projection, its one internal check and the persistence plumbing, so a later reader that names
/// them fails here and must read the views instead.
/// </summary>
public sealed class CurrentWorkViewTests
{
    private const string Projection = "src/App.Infrastructure/Processing/SnapshotProjection.cs";
    private const string Reconciler = "src/App.Infrastructure/Reconciliation/SourceReconciler.cs";
    private const string Migrations = "src/App.Infrastructure/Persistence/Migrations/";

    // The raw tables by name, their entity sets and their records; the views' names, active_snapshot_*, never match.
    private static readonly Regex RawSnapshot = new(
        @"\bsnapshot_(fields|objects)\b|\bSnapshot(Fields|Objects)\b|\bSnapshot(Field|Object)Record\b",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Only_the_projection_and_its_persistence_name_the_raw_snapshot()
    {
        var holders = Sources()
            .Where(path => !path.StartsWith(Migrations, StringComparison.Ordinal))
            .Where(path => RawSnapshot.IsMatch(Read(path)))
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            [
                "src/App.Infrastructure/Persistence/Entities/SnapshotFieldRecord.cs",
                "src/App.Infrastructure/Persistence/Entities/SnapshotObjectRecord.cs",
                "src/App.Infrastructure/Persistence/WorkContinuityDbContext.cs",
                "src/App.Infrastructure/Persistence/WorkContinuityModel.cs",
                Projection,
            ],
            holders);
    }

    [Fact]
    public void The_projection_reads_the_raw_snapshot_only_to_write_it_and_for_reconciliations_check_of_a_confirmed_value()
    {
        // Its two members: the projection itself, and the check that the Snapshot already holds an accepted value.
        Assert.Equal(
            ["HoldsAsync", "ProjectAsync"],
            Regex.Matches(Read(Projection), @"public static async Task<\w+> (\w+)\(").Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));

        // Called only by processing, which writes, and by the reconciler's check that a source's state is already current.
        Assert.Equal(
            ["src/App.Infrastructure/Processing/EventProcessor.cs", Reconciler],
            Sources().Where(path => path != Projection && Read(path).Contains("SnapshotProjection.", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.Equal(1, Regex.Count(Read(Reconciler), @"SnapshotProjection\.HoldsAsync\("));
        Assert.Equal(0, Regex.Count(Read(Reconciler), @"SnapshotProjection\.ProjectAsync\("));
    }

    [Fact]
    public void Current_work_is_presented_by_the_two_active_views_which_run_as_their_caller()
    {
        var created = Sources()
            .Where(path => path.StartsWith(Migrations, StringComparison.Ordinal))
            .SelectMany(path => Regex.Matches(Read(path), @"CREATE VIEW public\.(\w+)( WITH \(security_invoker = true\))?").Select(match => (match.Groups[1].Value, match.Groups[2].Success)))
            .Order();

        Assert.Equal([("active_snapshot_fields", true), ("active_snapshot_objects", true)], created);
    }

    private static IEnumerable<string> Sources() =>
        new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, "src"))
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Select(RepositoryLayout.RelativePath)
            .Where(path => !path.Contains("/bin/", StringComparison.Ordinal) && !path.Contains("/obj/", StringComparison.Ordinal));

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root.FullName, relativePath));
}
