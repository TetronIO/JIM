// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;

namespace JIM.Worker.Tests.Migrations;

/// <summary>
/// The release freeze: migrations listed in <c>src/JIM.PostgresData/Migrations/released-migrations.lock</c> have
/// shipped in a JIM release and been applied to customer databases, so they are immutable. This guard fails the
/// build when a released migration is renamed, edited or deleted, or when a new migration's timestamp sequences
/// it before the newest released one (the post-merge regeneration that is routine while a migration is
/// unreleased, and breaks customer upgrades the moment it is not). The manifest is append-only and written by
/// <c>scripts/Update-ReleasedMigrationsManifest.ps1</c> during <c>/release</c>; with no entries yet, the guard
/// passes, so it ships armed for the first release. Sibling of <see cref="MigrationDesignerChainTests"/>, and
/// like it a design-time guard: the EnableLegacyTimestampBehavior quirk suppresses PendingModelChangesWarning at
/// runtime, so nothing at runtime can catch this class of fault.
/// </summary>
public class ReleasedMigrationImmutabilityTests
{
    [Test]
    public void ReleasedMigrations_ComparedWithTheManifest_AreUnchangedAndNotResequenced()
    {
        var migrationsDirectory = Path.Join(FindRepositoryRoot(), "src", "JIM.PostgresData", "Migrations");
        var manifestPath = Path.Join(migrationsDirectory, "released-migrations.lock");
        Assert.That(File.Exists(manifestPath), Is.True,
            $"The released-migrations manifest is missing at {manifestPath}; it is append-only and must never be deleted.");

        var entries = ReleasedMigrationManifest.Parse(File.ReadAllLines(manifestPath));
        var actualMigrations = ReadActualMigrations(migrationsDirectory);

        var failures = ReleasedMigrationManifest.Verify(entries, actualMigrations);
        Assert.That(failures, Is.Empty, () =>
            "The release freeze is violated: a migration that shipped in a release has been changed, or a new one " +
            "is sequenced into already-upgraded databases. Released migrations are applied on customer databases " +
            "and must never be renamed, regenerated, edited or deleted; only migrations newer than the newest " +
            "released one may change. Details:" +
            Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", failures));
    }

    /// <summary>
    /// Reads every migration in the working tree as an id plus the hashes of its two files. The id is the file
    /// name (timestamp_Name), matching both the manifest and EF's own migration ids.
    /// </summary>
    private static List<ReleasedMigrationManifest.ActualMigration> ReadActualMigrations(string migrationsDirectory)
    {
        var migrations = new List<ReleasedMigrationManifest.ActualMigration>();
        foreach (var path in Directory.EnumerateFiles(migrationsDirectory, "*.cs")
                     .Where(p => Regex.IsMatch(Path.GetFileName(p), @"^\d{14}_.+\.cs$") && !p.EndsWith(".Designer.cs", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            var designerPath = Path.Join(migrationsDirectory, $"{id}.Designer.cs");
            Assert.That(File.Exists(designerPath), Is.True, $"{id} has no Designer file beside it.");

            migrations.Add(new ReleasedMigrationManifest.ActualMigration(
                id,
                ReleasedMigrationManifest.HashContent(File.ReadAllText(path)),
                ReleasedMigrationManifest.HashContent(File.ReadAllText(designerPath))));
        }

        Assert.That(migrations, Is.Not.Empty, $"No migrations found under {migrationsDirectory}; the guard is looking in the wrong place.");
        return migrations;
    }

    /// <summary>
    /// Walks up from the test assembly's location to the directory holding <c>JIM.sln</c>, matching the
    /// convention-sweep tests' approach: the guard reads source files, which are not copied to the test output.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "Could not locate JIM.sln by walking up from the test output directory.");
        return directory!.FullName;
    }
}
