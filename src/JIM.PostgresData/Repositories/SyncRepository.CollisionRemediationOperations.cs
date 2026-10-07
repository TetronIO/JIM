// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

public partial class SyncRepository
{
    #region Collision Remediation (#242, release 4)

    // The numeric forms of the revision's compare-and-swap statements, one per storage column. Constant text: the
    // column is chosen by which statement runs, never interpolated.
    private const string ReviseMetaverseIntValueSql =
        @"UPDATE ""MetaverseObjectAttributeValues""
          SET ""IntValue"" = {0},
              ""ContributedBySyncRuleId"" = COALESCE({1}, ""ContributedBySyncRuleId""),
              ""ContributedBySystemId"" = COALESCE({2}, ""ContributedBySystemId"")
          WHERE ""MetaverseObjectId"" = {3} AND ""AttributeId"" = {4} AND COALESCE(""LongValue"", ""IntValue"") = {5}";

    private const string ReviseMetaverseLongValueSql =
        @"UPDATE ""MetaverseObjectAttributeValues""
          SET ""LongValue"" = {0},
              ""ContributedBySyncRuleId"" = COALESCE({1}, ""ContributedBySyncRuleId""),
              ""ContributedBySystemId"" = COALESCE({2}, ""ContributedBySystemId"")
          WHERE ""MetaverseObjectId"" = {3} AND ""AttributeId"" = {4} AND COALESCE(""LongValue"", ""IntValue"") = {5}";

    private const string RevisePendingExportIntValueSql =
        @"UPDATE ""PendingExportAttributeValueChanges"" SET ""IntValue"" = {0}
          WHERE ""Id"" = {1} AND COALESCE(""LongValue"", ""IntValue"") = {2}";

    private const string RevisePendingExportLongValueSql =
        @"UPDATE ""PendingExportAttributeValueChanges"" SET ""LongValue"" = {0}
          WHERE ""Id"" = {1} AND COALESCE(""LongValue"", ""IntValue"") = {2}";

    /// <inheritdoc />
    public Task<GeneratedValueAssignment?> GetGeneratedValueAssignmentByIdAsync(Guid assignmentId)
    {
        return _context.GeneratedValueAssignments
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == assignmentId);
    }

    /// <inheritdoc />
    public async Task<GeneratedValueRevisionResult> ApplyGeneratedValueRevisionAsync(GeneratedValueRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        var assignment = revision.Assignment;

        // One transaction for every part (plan decision 8): a Metaverse value revised without its assignment, or an
        // assignment revised without the record that carries it to the exports, would leave the queued exports and the
        // object disagreeing with nothing to reconcile them. Joins an ambient transaction where the caller holds one.
        var ownsTransaction = _context.Database.CurrentTransaction == null;
        var transaction = ownsTransaction ? await _context.Database.BeginTransactionAsync() : null;

        try
        {
            // Retire the value being replaced first: the statement reads it from the assignment's row, which the update
            // below overwrites.
            if (revision.RetirePreviousValue)
                await RetiredGeneratedValueSql.RetireAssignmentsAsync(_context, [assignment.Id], RetiredGeneratedValueReason.Regenerated, activityId: null);

            var written = assignment.MetaverseObjectId.HasValue
                ? await ReviseMetaverseValueAsync(revision)
                : await RevisePendingExportValueAsync(revision);

            if (!written)
            {
                if (transaction != null)
                    await transaction.RollbackAsync();
                return GeneratedValueRevisionResult.ValueChanged;
            }

            if (revision.MetaverseObjectChange != null)
                await _repo.Metaverse.CreateMetaverseObjectChangeDirectAsync(revision.MetaverseObjectChange);

            assignment.LastUpdated = DateTime.UtcNow;
            _repo.UpdateDetachedSafe(assignment);
            if (revision.RevisionPending != null)
                _context.GeneratedValueRevisionsPending.Add(revision.RevisionPending);

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                // Another live assignment claimed the new value first (plan decision 13). Nothing of this revision may
                // stay tracked, or the next unrelated save would resend it.
                _context.Entry(assignment).State = EntityState.Detached;
                if (revision.RevisionPending != null)
                    _context.Entry(revision.RevisionPending).State = EntityState.Detached;

                if (transaction != null)
                    await transaction.RollbackAsync();
                return GeneratedValueRevisionResult.ValueTaken;
            }

            // Written by the save above; detached so a later save on this long-lived context does not resend them.
            _context.Entry(assignment).State = EntityState.Detached;
            if (revision.RevisionPending != null)
                _context.Entry(revision.RevisionPending).State = EntityState.Detached;

            if (transaction != null)
                await transaction.CommitAsync();
            return GeneratedValueRevisionResult.Applied;
        }
        finally
        {
            if (transaction != null)
                await transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// Import mode: the compare-and-swap that is the revision's optimistic concurrency check. Writes the new value (and
    /// the generated mapping's provenance) only where the object still holds the value the export carried, and touches
    /// the Metaverse Object's row so a concurrent writer holding its previous row version fails its own check. A
    /// narrow, single-purpose statement (src/CLAUDE.md, Raw SQL Column Lists: exempt).
    /// </summary>
    private async Task<bool> ReviseMetaverseValueAsync(GeneratedValueRevision revision)
    {
        var assignment = revision.Assignment;
        var metaverseObjectId = assignment.MetaverseObjectId!.Value;
        var attributeId = assignment.MetaverseAttributeId!.Value;

        int updated;
        if (revision.NewNumericValue.HasValue)
        {
            updated = await _context.Database.ExecuteSqlRawAsync(
                revision.IsLongNumber ? ReviseMetaverseLongValueSql : ReviseMetaverseIntValueSql,
                revision.IsLongNumber ? revision.NewNumericValue.Value : (object)(int)revision.NewNumericValue.Value,
                BulkSqlHelpers.NullableParam(revision.ContributedBySyncRuleId, NpgsqlDbType.Integer),
                BulkSqlHelpers.NullableParam(revision.ContributedBySystemId, NpgsqlDbType.Integer),
                metaverseObjectId, attributeId, revision.PreviousNumericValue ?? 0L);
        }
        else
        {
            updated = await _context.Database.ExecuteSqlRawAsync(
                @"UPDATE ""MetaverseObjectAttributeValues""
                  SET ""StringValue"" = {0},
                      ""ContributedBySyncRuleId"" = COALESCE({1}, ""ContributedBySyncRuleId""),
                      ""ContributedBySystemId"" = COALESCE({2}, ""ContributedBySystemId"")
                  WHERE ""MetaverseObjectId"" = {3} AND ""AttributeId"" = {4} AND LOWER(""StringValue"") = LOWER({5})",
                assignment.Value,
                BulkSqlHelpers.NullableParam(revision.ContributedBySyncRuleId, NpgsqlDbType.Integer),
                BulkSqlHelpers.NullableParam(revision.ContributedBySystemId, NpgsqlDbType.Integer),
                metaverseObjectId, attributeId, revision.PreviousValue);
        }

        // Exactly one: the attribute is single-valued (a generated value always is), so anything else means the object
        // no longer holds the value the export carried, or holds it twice, and either way this is not the object the
        // revision was decided against.
        if (updated != 1)
            return false;

        await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""MetaverseObjects"" SET ""LastUpdated"" = {0}, ""CachedDisplayName"" = COALESCE({1}, ""CachedDisplayName"") WHERE ""Id"" = {2}",
            DateTime.UtcNow, BulkSqlHelpers.NullableParam(revision.CachedDisplayName, NpgsqlDbType.Text), metaverseObjectId);

        // Raw SQL bypasses the change tracker: fix up any tracked copy of the value so a later save does not write the
        // old value back (src/CLAUDE.md, Raw SQL Writes Must Fix Up or Detach Tracked Instances).
        DetachTrackedEntities<JIM.Models.Core.MetaverseObjectAttributeValue>(av => av.AttributeId == attributeId && av.MetaverseObject != null && av.MetaverseObject.Id == metaverseObjectId);
        DetachTrackedEntities<JIM.Models.Core.MetaverseObject>(mvo => mvo.Id == metaverseObjectId);
        return true;
    }

    /// <summary>
    /// Export mode: rewrites the queued Pending Export attribute change carrying the value, compare-and-swap on the
    /// value it held. A narrow, single-purpose statement (src/CLAUDE.md, Raw SQL Column Lists: exempt).
    /// </summary>
    private async Task<bool> RevisePendingExportValueAsync(GeneratedValueRevision revision)
    {
        if (!revision.PendingExportAttributeValueChangeId.HasValue)
            return false;

        var changeId = revision.PendingExportAttributeValueChangeId.Value;
        int updated;
        if (revision.NewNumericValue.HasValue)
        {
            updated = await _context.Database.ExecuteSqlRawAsync(
                revision.IsLongNumber ? RevisePendingExportLongValueSql : RevisePendingExportIntValueSql,
                revision.IsLongNumber ? revision.NewNumericValue.Value : (object)(int)revision.NewNumericValue.Value,
                changeId, revision.PreviousNumericValue ?? 0L);
        }
        else
        {
            updated = await _context.Database.ExecuteSqlRawAsync(
                @"UPDATE ""PendingExportAttributeValueChanges"" SET ""StringValue"" = {0}
                  WHERE ""Id"" = {1} AND LOWER(""StringValue"") = LOWER({2})",
                revision.Assignment.Value, changeId, revision.PreviousValue);
        }

        if (updated != 1)
            return false;

        DetachTrackedEntities<PendingExportAttributeValueChange>(c => c.Id == changeId);
        return true;
    }

    /// <inheritdoc />
    public Task<List<GeneratedValueRevisionPending>> GetGeneratedValueRevisionsPendingAsync(int maxResults)
    {
        return _context.GeneratedValueRevisionsPending
            .AsNoTracking()
            .OrderBy(r => r.Created)
            .ThenBy(r => r.Id)
            .Take(maxResults)
            .ToListAsync();
    }

    /// <inheritdoc />
    public Task<bool> AnyGeneratedValueRevisionsPendingAsync()
        => _context.GeneratedValueRevisionsPending.AsNoTracking().AnyAsync();

    /// <inheritdoc />
    public Task<bool> HasGeneratedValueRevisionPendingAsync(Guid metaverseObjectId, int metaverseAttributeId)
        => _context.GeneratedValueRevisionsPending.AsNoTracking()
            .AnyAsync(r => r.MetaverseObjectId == metaverseObjectId && r.MetaverseAttributeId == metaverseAttributeId);

    /// <inheritdoc />
    public async Task DeleteGeneratedValueRevisionsPendingAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0)
            return;

        var idSet = ids.ToHashSet();
        DetachTrackedEntities<GeneratedValueRevisionPending>(r => idSet.Contains(r.Id));
        await _context.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""GeneratedValueRevisionsPending"" WHERE ""Id"" = ANY({0})", ids.ToArray());
    }

    /// <inheritdoc />
    public async Task<int> ReleaseParkedPendingExportsAsync(IReadOnlyCollection<Guid> connectedSystemObjectIds)
    {
        if (connectedSystemObjectIds.Count == 0)
            return 0;

        var ids = connectedSystemObjectIds.ToArray();
        var released = await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""PendingExports"" SET ""Status"" = {0}, ""NextRetryAt"" = NULL
              WHERE ""Status"" = {1} AND ""ConnectedSystemObjectId"" = ANY({2})",
            (int)PendingExportStatus.Pending, (int)PendingExportStatus.Parked, ids);

        // Fix up any tracked copies to match (src/CLAUDE.md, Raw SQL Writes Must Fix Up or Detach Tracked Instances).
        var idSet = connectedSystemObjectIds.ToHashSet();
        DetachTrackedEntities<PendingExport>(pe => pe.ConnectedSystemObjectId.HasValue && idSet.Contains(pe.ConnectedSystemObjectId.Value));
        return released;
    }

    #endregion
}
