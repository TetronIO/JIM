// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Models.Sync;

/// <summary>
/// What flush-time reconciliation (#218) does with a deferred Pending Export that contradicts a Delete already
/// persisted for the same Connected System Object earlier in the page.
/// </summary>
public enum DeferredDeleteReconciliationOutcome
{
    /// <summary>
    /// A deferred Create meets a Pending Delete: the object was never exported, so neither export is needed.
    /// Both are cancelled and the provisioning Connected System Object is not created.
    /// </summary>
    CancelBoth,

    /// <summary>
    /// A deferred Update meets a Pending Delete: the Update is dropped and the Delete proceeds.
    /// </summary>
    DropDeferredUpdate
}

/// <summary>
/// One contradictory pair found by <c>ISyncEngine.ReconcileDeferredExportsAgainstPersistedDeletes</c>. The
/// decision persists nothing; the orchestrator removes the deferred export and, for
/// <see cref="DeferredDeleteReconciliationOutcome.CancelBoth"/>, deletes the persisted Delete.
/// </summary>
/// <param name="Deferred">The deferred (not yet persisted) Create or Update Pending Export.</param>
/// <param name="PersistedDelete">The Delete Pending Export already persisted for the same Connected System Object.</param>
/// <param name="Outcome">What to do with the pair.</param>
public sealed record DeferredDeleteReconciliation(
    PendingExport Deferred,
    PendingExport PersistedDelete,
    DeferredDeleteReconciliationOutcome Outcome);
