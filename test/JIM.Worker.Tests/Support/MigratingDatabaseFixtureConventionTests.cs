// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using System.Text.RegularExpressions;

namespace JIM.Worker.Tests.Support;

/// <summary>
/// Sweeps every test project for real-PostgreSQL fixtures that migrate the schema without suppressing
/// <c>PendingModelChangesWarning</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>PostgresDataRepository</c>'s constructor sets the <c>Npgsql.EnableLegacyTimestampBehavior</c> AppContext
/// switch, after which the runtime model and the migrations snapshot disagree on every DateTime column (see
/// "DateTime Handling" in <c>src/CLAUDE.md</c>). From then on, in the same process, <c>Migrate()</c> throws on that
/// warning unless the options suppress it. A fixture that omits the suppression therefore passes on its own and
/// fails whenever any earlier test has constructed the repository, so whether it fails depends on test order.
/// Seven fixtures had exactly this fault and failed only when the whole Worker suite ran against a database; two
/// of them arrived on main while the first five were being fixed, which is why this is a sweep and not a one-off.
/// </para>
/// <para>
/// A source sweep rather than a behavioural test because the fault is order-dependent: no single run of the
/// fixture demonstrates it. It fails with the offending files named, so the fix is obvious.
/// </para>
/// </remarks>
[TestFixture]
public class MigratingDatabaseFixtureConventionTests
{
    private static readonly Regex MigrateCall = new(@"\bMigrate(Async)?\s*\(", RegexOptions.Compiled);

    [Test]
    public void EveryTestFileThatMigrates_SuppressesPendingModelChangesWarning()
    {
        var testRoot = Path.Join(FindRepositoryRoot(), "test");

        var offenders = Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return MigrateCall.IsMatch(source) && !source.Contains("PendingModelChangesWarning");
            })
            .Select(path => Path.GetRelativePath(testRoot, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.That(offenders, Is.Empty,
            "these test files migrate the database without " +
            ".ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)), so they fail whenever an " +
            "earlier test in the same process has set the legacy timestamp switch: " + string.Join(", ", offenders));
    }

    private static bool IsBuildOutput(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin") || segments.Contains("obj");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "Could not locate the repository root from the test assembly's location.");
        return directory!.FullName;
    }
}
