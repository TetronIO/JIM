// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The Pending Export batch scope (#2492). The cross-page reference step re-evaluates exports for every object whose
/// references it resolves, and each re-evaluation replaces the Pending Export the page loop staged: deleting it one at a
/// time cost two round trips and two passes over the change tracker per object, about half the step (8 ms of each
/// object's 22 ms on Scenario 031). Inside a batch those deletes are queued and executed in one statement.
/// </summary>
/// <remarks>
/// <para>
/// The queue is invisible to callers, which is what keeps every merge decision exactly as it was:
/// </para>
/// <list type="bullet">
/// <item>Every read hides a queued Pending Export, by Connected System Object or by id, as though it had been deleted.</item>
/// <item>Every insert first executes the queued delete for the same Connected System Object, so the unique index on
/// <c>PendingExports.ConnectedSystemObjectId</c> sees the row gone before its successor arrives, as it always did.</item>
/// <item>A write naming a queued Pending Export by id cannot arise (no read returns one), so it fails fast if it does.</item>
/// </list>
/// <para>
/// A batch can also prefetch the Pending Exports of the objects it will evaluate, in one query, so the per-object
/// lookup the evaluation makes (twice, for an object still being provisioned) is answered without a round trip. The
/// prefetch holds the same tracked instances the lookup's own query would return, and any write touching one drops it.
/// </para>
/// <para>
/// A failed batch is abandoned, not flushed: its replacements were never staged, and executing its deletes would lose
/// the queued changes they carried.
/// </para>
/// </remarks>
public partial class SyncRepository
{
    private Dictionary<Guid, PendingExport>? _batchQueuedDeletesByCsoId;
    private HashSet<Guid>? _batchQueuedDeleteIds;
    private Dictionary<Guid, PendingExport?>? _batchPrefetchedByCsoId;

    /// <inheritdoc />
    public void BeginPendingExportBatch()
    {
        if (_batchQueuedDeletesByCsoId != null)
            throw new InvalidOperationException("A Pending Export batch is already open; flush or abandon it first.");

        _batchQueuedDeletesByCsoId = [];
        _batchQueuedDeleteIds = [];
        _batchPrefetchedByCsoId = [];
    }

    /// <inheritdoc />
    public async Task PrefetchPendingExportsForBatchAsync(IReadOnlyCollection<Guid> connectedSystemObjectIds)
    {
        if (_batchPrefetchedByCsoId is not { } prefetched)
            throw new InvalidOperationException("No Pending Export batch is open to prefetch into.");

        var toLoad = connectedSystemObjectIds
            .Distinct()
            .Where(id => !prefetched.ContainsKey(id) && !IsQueuedForDeleteByCsoId(id))
            .ToList();
        if (toLoad.Count == 0)
            return;

        // The include shape and tracking of GetPendingExportLightweightByConnectedSystemObjectIdAsync's own query, so a
        // prefetched instance is exactly what that method would have returned: tracked, and so resolving its
        // attributes to the instances already tracked rather than to duplicates of them.
        var loaded = await _context.PendingExports
            .Include(pe => pe.AttributeValueChanges)
                .ThenInclude(avc => avc.Attribute)
            .Where(pe => pe.ConnectedSystemObjectId != null && toLoad.Contains(pe.ConnectedSystemObjectId.Value))
            .ToListAsync();

        foreach (var csoId in toLoad)
            prefetched[csoId] = null;
        foreach (var pendingExport in loaded)
            prefetched[pendingExport.ConnectedSystemObjectId!.Value] = pendingExport;
    }

    /// <summary>
    /// The prefetched answer for an object, where the open batch holds one.
    /// </summary>
    private bool TryGetPrefetched(Guid connectedSystemObjectId, out PendingExport? pendingExport)
    {
        pendingExport = null;
        return _batchPrefetchedByCsoId?.TryGetValue(connectedSystemObjectId, out pendingExport) == true;
    }

    /// <summary>
    /// Drops the prefetched answers for objects a write touches, so the next read goes back to the database.
    /// </summary>
    private void ForgetPrefetched(IEnumerable<Guid?> connectedSystemObjectIds)
    {
        if (_batchPrefetchedByCsoId is not { Count: > 0 } prefetched)
            return;
        foreach (var csoId in connectedSystemObjectIds.OfType<Guid>())
            prefetched.Remove(csoId);
    }

    /// <summary>
    /// Drops the prefetched answers holding any of these Pending Exports.
    /// </summary>
    private void ForgetPrefetchedByPendingExportIds(IEnumerable<Guid> pendingExportIds)
    {
        if (_batchPrefetchedByCsoId is not { Count: > 0 } prefetched)
            return;
        var ids = pendingExportIds.ToHashSet();
        foreach (var csoId in prefetched.Where(kv => kv.Value != null && ids.Contains(kv.Value.Id)).Select(kv => kv.Key).ToList())
            prefetched.Remove(csoId);
    }

    /// <summary>
    /// Drops every prefetched answer, for a write that cannot say which objects it touches.
    /// </summary>
    private void ForgetAllPrefetched() => _batchPrefetchedByCsoId?.Clear();

    /// <inheritdoc />
    public async Task DeletePendingExportForMergeAsync(PendingExport pendingExport)
    {
        if (_batchQueuedDeletesByCsoId == null || pendingExport.ConnectedSystemObjectId is not { } csoId)
        {
            await DeletePendingExportAsync(pendingExport);
            return;
        }

        // The unique index allows one Pending Export per object, and a queued one is hidden from every read, so a
        // second for the same object means something read around the scope: stop rather than guess which to keep.
        if (!_batchQueuedDeletesByCsoId.TryAdd(csoId, pendingExport))
            throw new InvalidOperationException(
                $"Pending Export {pendingExport.Id} for Connected System Object {csoId} was queued for deletion while Pending Export " +
                $"{_batchQueuedDeletesByCsoId[csoId].Id} for the same object is already queued.");

        _batchQueuedDeleteIds!.Add(pendingExport.Id);
        ForgetPrefetched([csoId]);
    }

    /// <inheritdoc />
    public async Task FlushPendingExportBatchAsync()
    {
        var queued = _batchQueuedDeletesByCsoId?.Values.ToList() ?? [];
        CloseBatch();
        if (queued.Count > 0)
            await _repo.ConnectedSystems.DeletePendingExportsAsync(queued);
    }

    /// <inheritdoc />
    public void AbandonPendingExportBatch() => CloseBatch();

    private void CloseBatch()
    {
        _batchQueuedDeletesByCsoId = null;
        _batchQueuedDeleteIds = null;
        _batchPrefetchedByCsoId = null;
    }

    private bool IsQueuedForDelete(PendingExport? pendingExport) =>
        pendingExport != null && _batchQueuedDeleteIds?.Contains(pendingExport.Id) == true;

    private bool IsQueuedForDeleteByCsoId(Guid connectedSystemObjectId) =>
        _batchQueuedDeletesByCsoId?.ContainsKey(connectedSystemObjectId) == true;

    /// <summary>
    /// Hides a queued Pending Export from a single read.
    /// </summary>
    private PendingExport? HideQueued(PendingExport? pendingExport) => IsQueuedForDelete(pendingExport) ? null : pendingExport;

    /// <summary>
    /// Hides queued Pending Exports from a list read.
    /// </summary>
    private List<PendingExport> HideQueued(List<PendingExport> pendingExports)
    {
        if (_batchQueuedDeleteIds is { Count: > 0 } queuedIds)
            pendingExports.RemoveAll(pe => queuedIds.Contains(pe.Id));
        return pendingExports;
    }

    /// <summary>
    /// Hides queued Pending Exports from a read keyed by Connected System Object.
    /// </summary>
    private Dictionary<Guid, PendingExport> HideQueued(Dictionary<Guid, PendingExport> pendingExportsByCsoId)
    {
        if (_batchQueuedDeletesByCsoId is { Count: > 0 } queued)
        {
            foreach (var csoId in queued.Keys)
                pendingExportsByCsoId.Remove(csoId);
        }
        return pendingExportsByCsoId;
    }

    /// <summary>
    /// Hides the objects whose Pending Export is queued for deletion from a read of which objects have one.
    /// </summary>
    private HashSet<Guid> HideQueued(HashSet<Guid> connectedSystemObjectIds)
    {
        if (_batchQueuedDeletesByCsoId is { Count: > 0 } queued)
            connectedSystemObjectIds.ExceptWith(queued.Keys);
        return connectedSystemObjectIds;
    }

    /// <summary>
    /// Before an insert, or a write addressed by Connected System Object: executes the queued deletes for those
    /// objects, so the write lands after them, as it did when the deletes ran at once.
    /// </summary>
    private async Task ExecuteQueuedDeletesForAsync(IEnumerable<Guid?> connectedSystemObjectIds)
    {
        var csoIds = connectedSystemObjectIds.OfType<Guid>().Distinct().ToList();
        ForgetPrefetched(csoIds.Select(id => (Guid?)id));
        if (_batchQueuedDeletesByCsoId is not { Count: > 0 } queued)
            return;

        var due = new List<PendingExport>();
        foreach (var csoId in csoIds)
        {
            if (queued.Remove(csoId, out var pendingExport))
            {
                _batchQueuedDeleteIds!.Remove(pendingExport.Id);
                due.Add(pendingExport);
            }
        }

        if (due.Count > 0)
            await _repo.ConnectedSystems.DeletePendingExportsAsync(due);
    }

    /// <summary>
    /// Before a delete: drops the queue entries the delete makes moot.
    /// </summary>
    private void ForgetQueued(IEnumerable<PendingExport> pendingExports)
    {
        var exportList = pendingExports.ToList();
        ForgetPrefetched(exportList.Select(pe => pe.ConnectedSystemObjectId));
        ForgetPrefetchedByPendingExportIds(exportList.Select(pe => pe.Id));
        if (_batchQueuedDeletesByCsoId is not { Count: > 0 } queued)
            return;

        foreach (var pendingExport in exportList.Where(pe => _batchQueuedDeleteIds!.Remove(pe.Id)))
            queued.Remove(pendingExport.ConnectedSystemObjectId!.Value);
    }

    /// <summary>
    /// Before a delete addressed by Connected System Object: drops the queue entries for those objects, whose rows the
    /// statement deletes anyway.
    /// </summary>
    private void ForgetQueuedForCsoIds(IEnumerable<Guid> connectedSystemObjectIds)
    {
        var csoIds = connectedSystemObjectIds.ToList();
        ForgetPrefetched(csoIds.Select(id => (Guid?)id));
        if (_batchQueuedDeletesByCsoId is not { Count: > 0 } queued)
            return;

        foreach (var csoId in csoIds)
        {
            if (queued.Remove(csoId, out var pendingExport))
                _batchQueuedDeleteIds!.Remove(pendingExport.Id);
        }
    }

    /// <summary>
    /// Before a write addressed by Pending Export id: no read returns a queued Pending Export, so a write naming one
    /// means a caller held it across its own delete. Stop rather than write to a row about to be deleted.
    /// </summary>
    private void ThrowIfAnyQueued(IEnumerable<Guid> pendingExportIds, string operation)
    {
        var ids = pendingExportIds.ToList();
        ForgetPrefetchedByPendingExportIds(ids);
        if (_batchQueuedDeleteIds is not { Count: > 0 } queuedIds)
            return;

        var queued = ids.FirstOrDefault(queuedIds.Contains);
        if (queued != Guid.Empty)
            throw new InvalidOperationException($"{operation} named Pending Export {queued}, which is queued for deletion in the open Pending Export batch.");
    }
}
