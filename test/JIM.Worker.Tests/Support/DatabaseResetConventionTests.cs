// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using System.Text.RegularExpressions;
using JIM.TestSupport;

namespace JIM.Worker.Tests.Support;

/// <summary>
/// Sweeps every test project for real-PostgreSQL fixtures that empty the database with their own hand-written
/// reset rather than <see cref="PostgresTestDatabase.ResetAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every fixture used to carry its own copy of a PL/pgSQL loop that issued one <c>TRUNCATE ... CASCADE</c> per
/// table. With 83 tables and their foreign keys that is over 400 cascaded truncations before every test, about a
/// second each on a CI runner, and it made up most of the <c>database-tests</c> job: the tier took 17.1 minutes
/// locally with the loop and 3.9 minutes with the single statement the helper issues. Ninety-three fixtures held a
/// copy, so the next fixture would have copied it too; this sweep is what stops that.
/// </para>
/// <para>
/// A file counts as hand-rolling the reset when it both reads <c>pg_tables</c> and issues a <c>TRUNCATE</c>.
/// Truncating a few named tables is fine; enumerating them all is the helper's job.
/// </para>
/// </remarks>
[TestFixture]
public class DatabaseResetConventionTests
{
    private static readonly Regex ReadsTableCatalogue = new(@"\bpg_tables\b", RegexOptions.Compiled);
    private static readonly Regex Truncates = new(@"\bTRUNCATE\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Test]
    public void NoTestFile_HandRollsTheWholeDatabaseReset()
    {
        var testRoot = Path.Join(FindRepositoryRoot(), "test");

        // The helper is where the reset belongs, and this sweep names what it looks for.
        var exempt = new[]
        {
            Path.Join(testRoot, "JIM.TestSupport", $"{nameof(PostgresTestDatabase)}.cs"),
            Path.Join(testRoot, "JIM.Worker.Tests", "Support", $"{nameof(DatabaseResetConventionTests)}.cs")
        }.Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);

        var offenders = Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Where(path => !exempt.Contains(Path.GetFullPath(path)))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return ReadsTableCatalogue.IsMatch(source) && Truncates.IsMatch(source);
            })
            .Select(path => Path.GetRelativePath(testRoot, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.That(offenders, Is.Empty,
            "these test files empty the database with their own TRUNCATE over pg_tables; call " +
            "PostgresTestDatabase.ResetAsync(connectionString) from [SetUp] instead: " + string.Join(", ", offenders));
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
