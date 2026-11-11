// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections;
using System.Reflection;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// Runs a confirming import's Pending Export reconciliation over objects a test hands it, as the import runs it alongside
/// its save phase (#2908): begin with the run's list of updated objects, reconcile each saved batch, reconcile the objects
/// saved more than once, complete. The objects were not loaded by an import's hydration, so reconciliation reloads them
/// from the repository, as it does any object it cannot judge from held values.
/// </summary>
internal static class ImportReconciliationInvoker
{
    private static MethodInfo Method(string name) =>
        typeof(SyncImportTaskProcessor).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            $"SyncImportTaskProcessor.{name} was not found. Update this invoker to call the current production methods.");

    /// <summary>Reconciles <paramref name="savedBatch"/> as the one batch of a run that updated those objects.</summary>
    public static Task ReconcileAsync(SyncImportTaskProcessor processor, List<ConnectedSystemObject> savedBatch) =>
        ReconcileAsync(processor, savedBatch, [savedBatch]);

    /// <summary>
    /// Reconciles a run whose save phase held <paramref name="updated"/> (an object may appear more than once) and saved
    /// it as <paramref name="savedBatches"/>.
    /// </summary>
    public static async Task ReconcileAsync(SyncImportTaskProcessor processor, List<ConnectedSystemObject> updated,
        IEnumerable<List<ConnectedSystemObject>> savedBatches)
    {
        var beginTask = (Task)Method("BeginPendingExportReconciliationAsync").Invoke(processor,
            [updated, (Task<Dictionary<Guid, PendingExport>>?)null])!;
        await beginTask;
        var run = beginTask.GetType().GetProperty("Result")!.GetValue(beginTask);
        if (run == null)
            return;

        foreach (var savedBatch in savedBatches)
        {
            var valuesBeforeSave = (IDictionary)Method("CaptureValuesBeforeSave").Invoke(processor, [run, savedBatch])!;
            await (Task)Method("ReconcileSavedBatchAsync").Invoke(processor,
                [run, savedBatch, valuesBeforeSave, new Dictionary<Guid, ActivityRunProfileExecutionItem>(), false])!;
        }

        await (Task)Method("ReconcileDeferredAsync").Invoke(processor, [run])!;
        Method("CompletePendingExportReconciliation").Invoke(processor, [run]);
    }
}
