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
/// Runs a confirming import's Pending Export reconciliation over objects a test hands it, as the import runs it for one
/// saved update batch (#2908): begin, reconcile the batch, complete. The objects were not loaded by an import's hydration,
/// so reconciliation reloads them from the repository, as it does any object it cannot judge from held values.
/// </summary>
internal static class ImportReconciliationInvoker
{
    private static MethodInfo Method(string name) =>
        typeof(SyncImportTaskProcessor).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            $"SyncImportTaskProcessor.{name} was not found. Update this invoker to call the current production methods.");

    public static async Task ReconcileAsync(SyncImportTaskProcessor processor, List<ConnectedSystemObject> savedBatch)
    {
        var beginTask = (Task)Method("BeginPendingExportReconciliationAsync").Invoke(processor,
            [savedBatch.Count, (Task<Dictionary<Guid, PendingExport>>?)null])!;
        await beginTask;
        var run = beginTask.GetType().GetProperty("Result")!.GetValue(beginTask);
        if (run == null)
            return;

        var valuesBeforeSave = (IDictionary)Method("CaptureValuesBeforeSave").Invoke(processor, [run, savedBatch])!;
        await (Task)Method("ReconcileSavedBatchAsync").Invoke(processor,
            [run, savedBatch, valuesBeforeSave, new Dictionary<Guid, ActivityRunProfileExecutionItem>(), false])!;
        Method("CompletePendingExportReconciliation").Invoke(processor, [run]);
    }
}
