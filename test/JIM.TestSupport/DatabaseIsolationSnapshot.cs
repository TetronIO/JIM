// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Npgsql;
using NUnit.Framework;

namespace JIM.TestSupport;

/// <summary>
/// The state of one watched table: how many rows it holds and a digest of their content, so an in-place update
/// is as visible as an insert or a delete.
/// </summary>
public readonly record struct DatabaseTableState(long RowCount, string ContentDigest);

/// <summary>
/// The preview isolation assertion (#288, PRD requirement 10): capture the synchronisation-integrity tables
/// before a preview, capture again after, and prove nothing changed. A preview that leaks a single Pending
/// Export or Metaverse Object write is worse than no preview, because administrators will trust it; this is the
/// test-side instrument that makes the zero-side-effect guarantee checkable rather than asserted.
/// </summary>
/// <remarks>
/// Content is compared by digest, not only by count, because an in-place UPDATE leaves the count identical.
/// Captures read through raw Npgsql rather than an EF context so the instrument shares nothing with the code it
/// is checking. Test-scale databases only: the digest orders and hashes every row of every watched table, which
/// is exactly right for a scenario database and wrong for a production one.
/// </remarks>
public sealed class DatabaseIsolationSnapshot
{
    /// <summary>
    /// The tables PRD requirement 10 names, plus the attribute-value child tables that give the Metaverse Object
    /// and Connected System Object counts their content. Pinned by a test; trim or rename only deliberately.
    /// </summary>
    public static readonly IReadOnlyList<string> SyncIntegrityTables =
    [
        "PendingExports",
        "PendingExportAttributeValueChanges",
        "MetaverseObjects",
        "MetaverseObjectAttributeValues",
        "ConnectedSystemObjects",
        "ConnectedSystemObjectAttributeValues",
        "ActivityRunProfileExecutionItems",
        "Activities"
    ];

    private readonly IReadOnlyDictionary<string, DatabaseTableState> _tables;

    /// <summary>
    /// Builds a snapshot from already-known table states. Public as the seam the pure comparison tests use;
    /// production test code captures with <see cref="CaptureAsync(string, CancellationToken)"/> instead.
    /// </summary>
    public DatabaseIsolationSnapshot(IReadOnlyDictionary<string, DatabaseTableState> tables)
    {
        _tables = tables;
    }

    /// <summary>
    /// Captures the state of the synchronisation-integrity tables.
    /// </summary>
    public static Task<DatabaseIsolationSnapshot> CaptureAsync(string connectionString, CancellationToken cancellationToken = default) =>
        CaptureAsync(connectionString, SyncIntegrityTables, cancellationToken);

    /// <summary>
    /// Captures the state of a specific set of tables. A watched table that does not exist fails the capture by
    /// name: a schema rename must break the snapshot loudly, or every isolation assertion in the suite quietly
    /// watches fewer tables than it claims.
    /// </summary>
    public static async Task<DatabaseIsolationSnapshot> CaptureAsync(
        string connectionString, IReadOnlyList<string> tables, CancellationToken cancellationToken = default)
    {
        // The table names are compile-time constants (or a test's own literals), never user input, but they are
        // interpolated into SQL as identifiers, so hold the line anyway: letters, digits and underscores only.
        var invalidName = tables.FirstOrDefault(t => t.Length == 0 || !t.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'));
        if (invalidName != null)
            throw new ArgumentException($"'{invalidName}' is not a plain identifier and cannot be watched.", nameof(tables));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var existsCommand = new NpgsqlCommand(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename = ANY(@tables)", connection))
        {
            existsCommand.Parameters.AddWithValue("tables", tables.ToArray());
            var found = new HashSet<string>(StringComparer.Ordinal);
            await using var existsReader = await existsCommand.ExecuteReaderAsync(cancellationToken);
            while (await existsReader.ReadAsync(cancellationToken))
                found.Add(existsReader.GetString(0));

            var missing = tables.Where(t => !found.Contains(t)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"Cannot capture an isolation snapshot: watched table(s) do not exist: {string.Join(", ", missing)}. " +
                    "If a table was renamed, update DatabaseIsolationSnapshot.SyncIntegrityTables and its pinning test.");
        }

        var states = new Dictionary<string, DatabaseTableState>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            await using var command = new NpgsqlCommand(
                $"SELECT count(*), COALESCE(md5(string_agg(t::text, '|' ORDER BY t::text)), 'empty') FROM \"{table}\" t",
                connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            states[table] = new DatabaseTableState(reader.GetInt64(0), reader.GetString(1));
        }

        return new DatabaseIsolationSnapshot(states);
    }

    /// <summary>
    /// Describes every difference between two snapshots, one message per changed table, in terms a test failure
    /// can act on: "PendingExports: 3 -> 4 rows" names the leak; "content changed" distinguishes an in-place
    /// update from an insert or delete. Empty when nothing changed.
    /// </summary>
    public static List<string> Diff(DatabaseIsolationSnapshot before, DatabaseIsolationSnapshot after)
    {
        if (!before._tables.Keys.ToHashSet().SetEquals(after._tables.Keys))
            throw new ArgumentException(
                "The two snapshots watched different table sets and cannot be compared honestly.");

        var differences = new List<string>();
        foreach (var (table, beforeState) in before._tables.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            var afterState = after._tables[table];
            if (beforeState == afterState)
                continue;

            differences.Add(beforeState.RowCount != afterState.RowCount
                ? $"{table}: {beforeState.RowCount} -> {afterState.RowCount} rows"
                : $"{table}: content changed ({afterState.RowCount} rows, count unchanged)");
        }

        return differences;
    }

    /// <summary>
    /// Asserts nothing changed between <paramref name="before"/> and this snapshot, failing with every changed
    /// table named. This is the call that sits immediately after a preview in every isolation scenario.
    /// </summary>
    public void AssertUnchangedSince(DatabaseIsolationSnapshot before)
    {
        var differences = Diff(before, this);
        if (differences.Count > 0)
            Assert.Fail("The operation was required to leave the database untouched, and did not. " +
                        $"Changed: {string.Join("; ", differences)}");
    }
}
