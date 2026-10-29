// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The worker's side of the configuration changes awaiting synchronisation (#2615): a synchronisation claims the rows
/// for its Connected System as it starts, processes every object of the types they name, and applies the rows it
/// claimed as it completes. The rows are written by <c>ConnectedSystemRepository.CreateConfigurationChangeSynchronisationAsync</c>.
/// <para>
/// Claim and apply are deliberately narrow, single-column statements (exempt from the bulk column list rule, like the
/// Connected System Object marks'), and both bring any tracked instance of a row into line with what they wrote. Nothing
/// on the sync path loads these rows today, so the fix-up costs one scan of the tracker's entries; it is there because
/// the worker's context lives for the whole run, and a tracked row left stale would be written back by whichever
/// <c>SaveChanges</c> found it modified (src/CLAUDE.md, "Raw SQL Writes Must Fix Up or Detach Tracked Instances").
/// </para>
/// </summary>
public partial class SyncRepository
{
    public async Task<IReadOnlyList<ClaimedConfigurationChangeSynchronisation>> ClaimConfigurationChangeSynchronisationsAsync(int connectedSystemId, Guid runActivityId)
    {
        // One statement, and that is the whole of why the claim cannot lose or duplicate a change.
        //
        // Under READ COMMITTED a statement sees exactly the rows committed before it started. A row is only ever written
        // after the configuration change it records has been saved, so every row this statement claims is a change that
        // is already committed, and therefore already in the Synchronisation Rules the run loads afterwards (the run
        // claims before it loads any, and each load is a statement of its own that starts later). A change saved while
        // the run is in flight commits its row after this statement ran, so it is not claimed and stays awaiting
        // synchronisation for the next run. No timestamps are compared, so no clock skew between hosts can move either
        // boundary.
        //
        // A row an earlier run claimed and never applied (it failed or was cancelled) still has AppliedAt null, so it is
        // claimed again and the earlier claim is overwritten. If two runs for one Connected System overlap, whichever
        // statement runs second waits for the first's row locks, re-checks the rows and overwrites the claim; the first
        // run then applies nothing (see ApplyClaimedConfigurationChangeSynchronisationsAsync) and the second applies
        // everything. Both runs processed the types in full by then, so that is safe, only redundant.
        const string sql = """
            UPDATE "ConfigurationChangeSynchronisations"
            SET "ClaimedByActivityId" = @runActivityId
            WHERE "ConnectedSystemId" = @connectedSystemId AND "AppliedAt" IS NULL
            RETURNING "Id", "ConnectedSystemObjectTypeId"
            """;

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = new NpgsqlCommand(sql, connection, (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction());
        command.Parameters.Add(new NpgsqlParameter("runActivityId", NpgsqlDbType.Uuid) { Value = runActivityId });
        command.Parameters.Add(new NpgsqlParameter("connectedSystemId", NpgsqlDbType.Integer) { Value = connectedSystemId });

        var claimed = new List<ClaimedConfigurationChangeSynchronisation>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                claimed.Add(new ClaimedConfigurationChangeSynchronisation(reader.GetInt64(0), reader.GetInt32(1)));
        }

        BringTrackedConfigurationChangesInLine(
            claimed.Select(c => c.Id).ToHashSet(),
            entry => SetAsPersisted(entry.Property(c => c.ClaimedByActivityId), (Guid?)runActivityId));

        return claimed;
    }

    public async Task<int> ApplyClaimedConfigurationChangeSynchronisationsAsync(Guid runActivityId)
    {
        // Applies exactly the rows this run still holds the claim on. A row another run has since claimed carries that
        // run's Activity id and is left for it, and a row saved after this run claimed was never stamped with this run's
        // id, so it stays awaiting synchronisation. The applied time is the worker's clock, supplied rather than taken
        // from the database's so the tracked instances can be set to the very value stored.
        const string sql = """
            UPDATE "ConfigurationChangeSynchronisations"
            SET "AppliedAt" = @appliedAt
            WHERE "ClaimedByActivityId" = @runActivityId AND "AppliedAt" IS NULL
            RETURNING "Id"
            """;

        var appliedAt = DateTime.UtcNow;
        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = new NpgsqlCommand(sql, connection, (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction());
        command.Parameters.Add(new NpgsqlParameter("appliedAt", NpgsqlDbType.TimestampTz) { Value = appliedAt });
        command.Parameters.Add(new NpgsqlParameter("runActivityId", NpgsqlDbType.Uuid) { Value = runActivityId });

        var applied = new HashSet<long>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                applied.Add(reader.GetInt64(0));
        }

        BringTrackedConfigurationChangesInLine(
            applied,
            entry => SetAsPersisted(entry.Property(c => c.AppliedAt), (DateTime?)appliedAt));

        return applied.Count;
    }

    public async Task<IReadOnlySet<int>> GetConnectedSystemObjectTypeIdsAwaitingSynchronisationAsync(int connectedSystemId)
    {
        // A plain read, served by the partial index on the rows awaiting synchronisation. The synchronisation preview
        // calls this and must never claim: claiming would stamp rows a real run then reclaims, and a preview changes
        // nothing (engineering/SYNC_PREVIEW_ZERO_SIDE_EFFECTS.md).
        var typeIds = await _context.ConfigurationChangeSynchronisations
            .AsNoTracking()
            .Where(c => c.ConnectedSystemId == connectedSystemId && c.AppliedAt == null)
            .Select(c => c.ConnectedSystemObjectTypeId)
            .Distinct()
            .ToListAsync();
        return typeIds.ToHashSet();
    }

    public Task<int> GetConnectedSystemObjectModifiedSinceOrOfTypesCountAsync(int connectedSystemId, DateTime modifiedSince, IReadOnlyCollection<int> objectTypeIds)
    {
        // The modified-since predicate is ConnectedSystemRepository.GetConnectedSystemObjectModifiedSinceCountAsync's,
        // which must match GetConnectedSystemObjectsModifiedSinceAsync's page query exactly, with every object of the
        // given types added. The Delta Synchronisation pages those types with GetConnectedSystemObjectsAsync instead, so
        // this is the count of what its loaders will hand out; a test pins the two together
        // (ConfigurationChangeSynchronisationDatabaseTests). The count is the run's progress estimate only: the run
        // pages until a page comes back empty.
        var typeIds = objectTypeIds.ToArray();
        return _context.ConnectedSystemObjects.CountAsync(cso =>
            cso.ConnectedSystemId == connectedSystemId &&
            (typeIds.Contains(cso.TypeId) ||
             cso.SynchronisationPending ||
             cso.DerivedInputChangePending ||
             cso.ScopeReviewPending ||
             cso.Created > modifiedSince ||
             (cso.LastUpdated.HasValue && cso.LastUpdated.Value > modifiedSince)));
    }

    /// <summary>
    /// Runs <paramref name="write"/> against each tracked instance of the given rows, with change detection suspended
    /// while the tracker is enumerated (src/CLAUDE.md, "Tracker surgery must not trigger DetectChanges").
    /// </summary>
    private void BringTrackedConfigurationChangesInLine(IReadOnlySet<long> ids, Action<EntityEntry<ConfigurationChangeSynchronisation>> write)
    {
        if (ids.Count == 0)
            return;

        var autoDetectChanges = _context.ChangeTracker.AutoDetectChangesEnabled;
        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var entries = _context.ChangeTracker.Entries<ConfigurationChangeSynchronisation>()
                .Where(e => e.State is EntityState.Unchanged or EntityState.Modified && ids.Contains(e.Entity.Id))
                .ToList();
            foreach (var entry in entries)
                write(entry);
        }
        finally
        {
            _context.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }

    /// <summary>
    /// Sets a tracked property's current and original value to what the raw write stored, so it reads as persisted
    /// rather than as a pending change.
    /// </summary>
    private static void SetAsPersisted<TProperty>(PropertyEntry<ConfigurationChangeSynchronisation, TProperty> property, TProperty value)
    {
        property.CurrentValue = value;
        property.OriginalValue = value;
        property.IsModified = false;
    }
}
