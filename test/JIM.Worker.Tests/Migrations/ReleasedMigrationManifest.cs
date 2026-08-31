// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography;
using System.Text;

namespace JIM.Worker.Tests.Migrations;

/// <summary>
/// Parses and verifies the released-migrations manifest
/// (<c>src/JIM.PostgresData/Migrations/released-migrations.lock</c>): the append-only record of every migration
/// that has shipped in a JIM release, written by <c>scripts/Update-ReleasedMigrationsManifest.ps1</c> during
/// <c>/release</c>. A released migration is applied on customer databases and is therefore frozen: renaming,
/// editing or deleting one, or sequencing a new migration before the newest released one, breaks customer
/// upgrades. <see cref="ReleasedMigrationImmutabilityTests"/> runs this against the real repository;
/// <see cref="ReleasedMigrationManifestTests"/> proves each violation class catchable over fabricated inputs.
/// </summary>
public static class ReleasedMigrationManifest
{
    /// <summary>One manifest line: a released migration's id, the frozen hashes of its two files, and the release that shipped it.</summary>
    public sealed record Entry(string Id, string MigrationHash, string DesignerHash, string Version);

    /// <summary>A migration as it exists in the working tree: its id and the current hashes of its two files.</summary>
    public sealed record ActualMigration(string Id, string MigrationHash, string DesignerHash);

    /// <summary>
    /// Parses manifest lines. Lines starting with <c>#</c> and blank lines are ignored; every other line must be
    /// four space-separated fields: id, migration file hash, Designer file hash, release version.
    /// </summary>
    public static IReadOnlyList<Entry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<Entry>();
        foreach (var line in lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 4)
                throw new FormatException($"Released-migrations manifest line is not '<id> <hash> <designer hash> <version>': '{line}'");
            entries.Add(new Entry(fields[0], fields[1], fields[2], fields[3]));
        }

        return entries;
    }

    /// <summary>
    /// Returns one failure message per violation of the release freeze; empty when the manifest and the working
    /// tree agree. An empty manifest verifies clean against anything, which is what lets the guard ship before
    /// the first release and arm itself when <c>/release</c> writes the first entry.
    /// </summary>
    public static IReadOnlyList<string> Verify(IReadOnlyList<Entry> entries, IReadOnlyList<ActualMigration> actualMigrations)
    {
        var failures = new List<string>();

        foreach (var duplicate in entries.GroupBy(e => e.Id).Where(g => g.Count() > 1))
            failures.Add($"{duplicate.Key} is listed twice in the manifest; entries are append-only and unique.");

        var actualById = actualMigrations.ToDictionary(m => m.Id, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!actualById.TryGetValue(entry.Id, out var actual))
            {
                failures.Add($"{entry.Id} shipped in v{entry.Version} but no longer exists; a released migration must " +
                             "never be renamed, regenerated or deleted, because customer databases have already applied it.");
                continue;
            }

            if (actual.MigrationHash != entry.MigrationHash)
                failures.Add($"{entry.Id}.cs has been edited since it shipped in v{entry.Version}; released migrations are frozen.");
            if (actual.DesignerHash != entry.DesignerHash)
                failures.Add($"{entry.Id}.Designer.cs has been edited since it shipped in v{entry.Version}; released migrations are frozen.");
        }

        if (entries.Count > 0)
        {
            var newestReleased = NewestReleasedId(entries)!;
            var listed = entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var straggler in actualMigrations
                         .Where(m => !listed.Contains(m.Id) && string.CompareOrdinal(m.Id, newestReleased) < 0)
                         .OrderBy(m => m.Id, StringComparer.Ordinal))
            {
                failures.Add($"{straggler.Id} is unreleased but its timestamp sorts before the newest released migration " +
                             $"({newestReleased}), so upgraded customer databases would apply it out of model order; " +
                             "regenerate it with a fresh timestamp (dotnet ef migrations remove/add).");
            }
        }

        return failures;
    }

    /// <summary>
    /// The newest released migration id (ordinal maximum, which for EF migration ids is timestamp order), or
    /// null when nothing has been released yet. This is the boundary the upgrade-path test migrates to first:
    /// because released migrations are frozen, migrating a fresh database to this id reproduces the last
    /// released schema exactly.
    /// </summary>
    public static string? NewestReleasedId(IReadOnlyList<Entry> entries) =>
        entries.Count == 0 ? null : entries.Select(e => e.Id).Max(StringComparer.Ordinal);

    /// <summary>
    /// Walks up from the test assembly's location to the directory holding <c>JIM.sln</c>, matching the
    /// convention-sweep tests' approach: the manifest and migration sources are not copied to the test output.
    /// Throws when the walk fails rather than returning null, so callers fail with the reason named.
    /// </summary>
    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate JIM.sln by walking up from the test output directory.");
    }

    /// <summary>The manifest's path beneath the given repository root.</summary>
    public static string GetManifestPath(string repositoryRoot) =>
        Path.Join(repositoryRoot, "src", "JIM.PostgresData", "Migrations", "released-migrations.lock");

    /// <summary>
    /// SHA-256 of the content as lowercase hex, with line endings normalised to LF first so a Windows (CRLF)
    /// checkout hashes identically to the repository's LF form.
    /// </summary>
    public static string HashContent(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n"));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
