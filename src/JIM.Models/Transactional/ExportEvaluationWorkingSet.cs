// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Sync;

namespace JIM.Models.Transactional;

/// <summary>
/// What one export evaluation run has already decided, keyed by CSO (#288 plan Phase 1a).
/// </summary>
/// <remarks>
/// The braided implementation answered "what has this run already staged for this CSO?" by reading its own
/// writes back from the database, which is both a round trip and the reason evaluation could not run without
/// persisting. With decisions accumulated here, a second evaluation path touching the same CSO in one run gets
/// the already-made decision from a dictionary instead, and the database is only consulted for Pending Exports
/// that existed <i>before</i> the run began. Owned by the orchestrator for the duration of one evaluation run;
/// the engine stays a pure function and never touches it. Not thread-safe by design: an evaluation run's
/// decision loop is sequential, and handing one working set to parallel writers would need the same care as any
/// shared dictionary, which no current caller wants.
/// </remarks>
public class ExportEvaluationWorkingSet
{
    private readonly Dictionary<Guid, MvoDeletionExportDecision> _deleteDecisionsByCsoId = [];

    /// <summary>
    /// Records the deletion-export decision made for a CSO in this run.
    /// </summary>
    public void RecordDeleteDecision(Guid connectedSystemObjectId, MvoDeletionExportDecision decision) =>
        _deleteDecisionsByCsoId[connectedSystemObjectId] = decision;

    /// <summary>
    /// Fetches the deletion-export decision this run already made for a CSO, if any.
    /// </summary>
    public bool TryGetDeleteDecision(Guid connectedSystemObjectId, out MvoDeletionExportDecision decision) =>
        _deleteDecisionsByCsoId.TryGetValue(connectedSystemObjectId, out decision);

    private readonly Dictionary<Guid, PendingExport> _stagedDeleteExportsByCsoId = [];

    /// <summary>
    /// Records a Delete Pending Export this run has staged (created, or found and reused) for a CSO. Record
    /// only after the export is persisted, so a failed batch write cannot leave the working set claiming a
    /// Pending Export that does not exist.
    /// </summary>
    public void RecordStagedDeleteExport(Guid connectedSystemObjectId, PendingExport pendingExport) =>
        _stagedDeleteExportsByCsoId[connectedSystemObjectId] = pendingExport;

    /// <summary>
    /// Fetches the Delete Pending Export this run already staged for a CSO, if any, so the caller can reuse it
    /// without reading the run's own write back from the database.
    /// </summary>
    public bool TryGetStagedDeleteExport(Guid connectedSystemObjectId, out PendingExport pendingExport) =>
        _stagedDeleteExportsByCsoId.TryGetValue(connectedSystemObjectId, out pendingExport!);
}
