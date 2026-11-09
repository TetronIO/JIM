// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// A synchronisation flush's history, prepared on the run's thread and written in one transaction (#2874).
/// </summary>
public partial class SyncRepository
{
    /// <inheritdoc />
    public SynchronisationHistoryBatch PrepareSynchronisationHistory(
        IReadOnlyList<ActivityRunProfileExecutionItem> items,
        IReadOnlySet<Guid>? itemIdsWrittenWithDeletion,
        bool includeItemChangeRecords,
        IReadOnlyList<MetaverseObjectChange> newMetaverseObjectChanges,
        IReadOnlyList<MetaverseObjectChange> metaverseObjectChangeAppends,
        int page,
        int totalPages)
    {
        // The items' ids first: the change records below name their items by them.
        foreach (var item in items.Where(i => i.Id == Guid.Empty))
            item.Id = ChangeRecordIds.New();

        // An item names its object by plain id (#2669); the navigation is a working reference the write must not need.
        foreach (var item in items.Where(i => i.ConnectedSystemObjectId == null && i.ConnectedSystemObject != null && i.ConnectedSystemObject.Id != Guid.Empty))
            item.ConnectedSystemObjectId = item.ConnectedSystemObject!.Id;

        // The items' own change records (a provisioned object's Added record), named by their items, less those a
        // deletion's transaction wrote with the item's row (#2807).
        List<ConnectedSystemObjectChange> itemChangeRecords = [];
        if (includeItemChangeRecords)
        {
            foreach (var item in items.Where(i => i.ConnectedSystemObjectChange != null))
                item.ConnectedSystemObjectChange!.ActivityRunProfileExecutionItemId = item.Id;

            itemChangeRecords = items
                .Where(i => i.ConnectedSystemObjectChange != null && itemIdsWrittenWithDeletion?.Contains(i.Id) != true)
                .Select(i => i.ConnectedSystemObjectChange!)
                .ToList();
            foreach (var change in itemChangeRecords)
                ChangeRecordIds.AssignMissing(change);
        }

        // The Metaverse Object change records, copied so the caller can clear its queues, each naming its item by id.
        var newChanges = newMetaverseObjectChanges.ToList();
        foreach (var change in newChanges)
        {
            ChangeRecordIds.AssignMissing(change);
            if (change.ActivityRunProfileExecutionItem?.Id is { } itemId && itemId != Guid.Empty)
                change.ActivityRunProfileExecutionItemId = itemId;
        }

        // An append's rows go under a record an earlier flush wrote; writing a new record for the same item would break
        // IX_MetaverseObjectChanges_ActivityRunProfileExecutionItemId.
        var appends = metaverseObjectChangeAppends.ToList();
        foreach (var append in appends)
        {
            if (append.Id == Guid.Empty)
                throw new InvalidOperationException(
                    "PrepareSynchronisationHistory: each Metaverse Object change appended to must carry the id of the existing change record it extends.");

            ChangeRecordIds.AssignMissingToAttributes(append);
        }

        var batch = CollectExecutionItemHistory(items.ToList(), itemIdsWrittenWithDeletion, itemChangeRecords, newChanges, appends, page, totalPages);

        // The batch owns the edges and candidates now, so a later flush of the same items cannot write them again.
        DrainCausalEdgeAndMatchCandidateBuffers(items);
        return batch;
    }

    /// <inheritdoc />
    public async Task<bool> WriteSynchronisationHistoryAsync(SynchronisationHistoryBatch batch)
    {
        if (_context.Database.CurrentTransaction != null)
            throw new InvalidOperationException(
                "WriteSynchronisationHistoryAsync writes a batch in its own transaction, and this repository already holds one. " +
                "Write history on a repository of its own (OpenIsolatedScope), never inside another write's transaction.");

        batch.RequireEveryIdAssigned(nameof(WriteSynchronisationHistoryAsync));
        if (batch.IsEmpty)
            return true;

        var previousTimeout = _context.Database.GetCommandTimeout();
        _context.Database.SetCommandTimeout(PostgresDataRepository.BulkOperationCommandTimeoutSeconds);
        try
        {
            // One transaction for the whole batch, so a failure leaves none of it behind. Holding it also keeps the item
            // and outcome rows off the parallel connections (#2872), which could not join it.
            await using var transaction = await _context.Database.BeginTransactionAsync();

            if (batch.Items.Count > 0)
                await WriteExecutionItemHistoryAsync(batch);

            if (batch.ItemChangeRecords.Count > 0)
            {
                using var changesSpan = MvoActivitySource.StartActivity("RpeiHistory.ItemCsoChanges");
                changesSpan?.SetTag("rowCount", batch.ItemChangeRecords.Count);
                await WriteCsoChangeRecordsAsync(batch.ItemChangeRecords);
            }

            if (batch.MetaverseObjectChanges.Count > 0 || batch.MetaverseObjectChangeAppends.Count > 0)
                await WriteMvoChangeRecordsAsync(batch.MetaverseObjectChanges, batch.MetaverseObjectChangeAppends);

            using (MvoActivitySource.StartActivity("RpeiHistory.Commit"))
                await transaction.CommitAsync();
        }
        finally
        {
            _context.Database.SetCommandTimeout(previousTimeout);
        }

        return true; // Written outside the change tracker; the caller releases the items.
    }

    /// <inheritdoc />
    public bool SupportsConcurrentHistoryWrites => _context.Database.IsRelational();

    /// <inheritdoc />
    public bool HasOpenTransaction => _context.Database.CurrentTransaction != null;
}
