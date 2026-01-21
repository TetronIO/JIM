using JIM.Application.Diagnostics;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Executes pending exports by calling connector export methods.
/// Implements Q5 (preview mode) and Q6 (retry with backoff) decisions.
///
/// Note on parallelism (Q8 decision): Database operations are executed sequentially
/// because EF Core DbContext is not thread-safe. Parallel processing may be introduced
/// in the future behind a feature flag once proper testing has been completed.
/// See OUTBOUND_SYNC_DESIGN.md for the full parallelism discussion.
/// </summary>
public class ExportExecutionServer
{
    /// <summary>
    /// Default batch size for processing exports. Can be overridden per call.
    /// </summary>
    public const int DefaultBatchSize = 100;

    private JimApplication Application { get; }

    internal ExportExecutionServer(JimApplication application)
    {
        Application = application;
    }

    /// <summary>
    /// Executes all pending exports for a connected system.
    /// </summary>
    /// <param name="connectedSystem">The connected system to export to</param>
    /// <param name="connector">The connector instance to use for export</param>
    /// <param name="runMode">Whether to preview only or actually sync (Q5 decision)</param>
    /// <returns>Export execution result with preview information</returns>
    public Task<ExportExecutionResult> ExecuteExportsAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        SyncRunMode runMode = SyncRunMode.PreviewAndSync)
    {
        return ExecuteExportsAsync(connectedSystem, connector, runMode, null, CancellationToken.None);
    }

    /// <summary>
    /// Executes all pending exports for a connected system with progress reporting and cancellation support.
    /// </summary>
    /// <param name="connectedSystem">The connected system to export to</param>
    /// <param name="connector">The connector instance to use for export</param>
    /// <param name="runMode">Whether to preview only or actually sync (Q5 decision)</param>
    /// <param name="options">Optional execution options for batch size and parallelism</param>
    /// <param name="cancellationToken">Cancellation token to stop export processing</param>
    /// <param name="progressCallback">Optional callback for progress reporting</param>
    /// <returns>Export execution result with preview information</returns>
    public async Task<ExportExecutionResult> ExecuteExportsAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        SyncRunMode runMode,
        ExportExecutionOptions? options,
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback = null)
    {
        options ??= new ExportExecutionOptions();

        var result = new ExportExecutionResult
        {
            ConnectedSystemId = connectedSystem.Id,
            RunMode = runMode,
            StartedAt = DateTime.UtcNow
        };

        // Get pending exports that are ready to execute
        List<PendingExport> pendingExports;
        using (Diagnostics.Diagnostics.Sync.StartSpan("GetExecutableExports"))
        {
            pendingExports = await GetExecutableExportsAsync(connectedSystem.Id);
        }
        result.TotalPendingExports = pendingExports.Count;

        if (pendingExports.Count == 0)
        {
            Log.Debug("ExecuteExportsAsync: No pending exports to execute for system {SystemId}", connectedSystem.Id);
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        Log.Information("ExecuteExportsAsync: Found {Count} pending exports to execute for system {SystemName} (BatchSize: {BatchSize})",
            pendingExports.Count, connectedSystem.Name, options.BatchSize);

        // Report initial progress
        await ReportProgressAsync(progressCallback, new ExportProgressInfo
        {
            Phase = ExportPhase.Preparing,
            TotalExports = pendingExports.Count,
            ProcessedExports = 0,
            Message = "Preparing exports"
        });

        // Track the IDs of pending exports being processed
        foreach (var pendingExport in pendingExports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.ProcessedPendingExportIds.Add(pendingExport.Id);
        }

        // If preview only mode, stop here (Q5 decision)
        // Note: In preview mode, callers can use ProcessedPendingExportIds to fetch
        // the actual PendingExport records for detailed preview information
        if (runMode == SyncRunMode.PreviewOnly)
        {
            Log.Information("ExecuteExportsAsync: Preview mode - not executing exports for system {SystemName}",
                connectedSystem.Name);
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        // Execute exports using the connector with batching
        await ExecuteExportsViaConnectorAsync(connectedSystem, connector, pendingExports, result, options, cancellationToken, progressCallback);

        // Second pass: retry any exports with deferred references that might now be resolvable
        if (!cancellationToken.IsCancellationRequested)
        {
            await ExecuteDeferredReferencesAsync(connectedSystem, connector, result);
        }

        result.CompletedAt = DateTime.UtcNow;

        // Report completion
        await ReportProgressAsync(progressCallback, new ExportProgressInfo
        {
            Phase = ExportPhase.Completed,
            TotalExports = result.TotalPendingExports,
            ProcessedExports = result.SuccessCount + result.FailedCount + result.DeferredCount,
            SuccessCount = result.SuccessCount,
            FailedCount = result.FailedCount,
            DeferredCount = result.DeferredCount,
            Message = $"Export completed: {result.SuccessCount} succeeded, {result.FailedCount} failed, {result.DeferredCount} deferred"
        });

        return result;
    }

    /// <summary>
    /// Reports progress to the callback if provided.
    /// </summary>
    private static async Task ReportProgressAsync(Func<ExportProgressInfo, Task>? callback, ExportProgressInfo info)
    {
        if (callback != null)
        {
            await callback(info);
        }
    }

    /// <summary>
    /// Gets pending exports that are ready to be executed.
    /// Filters out exports that are not yet due for retry (Q6 decision).
    /// </summary>
    private async Task<List<PendingExport>> GetExecutableExportsAsync(int connectedSystemId)
    {
        var allPendingExports = await Application.Repository.ConnectedSystems.GetPendingExportsAsync(connectedSystemId);

        return allPendingExports
            .Where(pe => IsReadyForExecution(pe))
            .OrderBy(pe => pe.CreatedAt) // Process oldest first
            .ToList();
    }

    /// <summary>
    /// Determines if a pending export is ready for execution.
    /// Considers status and retry timing (Q6 decision), as well as attribute change statuses.
    /// </summary>
    private static bool IsReadyForExecution(PendingExport pendingExport)
    {
        // For Delete operations, we don't need attribute changes - just the operation itself
        // For Create/Update operations, check if there are exportable attribute changes
        if (pendingExport.ChangeType == PendingExportChangeType.Update)
        {
            // For Update operations, we need at least one attribute change to export
            var hasExportableAttributeChanges = pendingExport.AttributeValueChanges.Any(ac =>
                ac.Status == PendingExportAttributeChangeStatus.Pending ||
                ac.Status == PendingExportAttributeChangeStatus.ExportedNotConfirmed);

            // If there are no attribute changes that need exporting, skip this export
            if (!hasExportableAttributeChanges)
            {
                return false;
            }
        }
        // For Create and Delete, we proceed even if there are no attribute changes
        // (Create might have no initial attributes, Delete just needs the operation)

        // Execute if status is Pending, Exported (for retry), or ExportNotConfirmed
        // - Pending: New export, not yet processed
        // - Exported: Was exported, but has attribute changes needing retry (ExportedNotConfirmed)
        // - ExportNotConfirmed: Previous export indicated not all values persisted
        if (pendingExport.Status != PendingExportStatus.Pending &&
            pendingExport.Status != PendingExportStatus.Exported &&
            pendingExport.Status != PendingExportStatus.ExportNotConfirmed)
        {
            return false;
        }

        // If it has a next retry time, check if we've passed it
        if (pendingExport.NextRetryAt.HasValue && pendingExport.NextRetryAt > DateTime.UtcNow)
        {
            return false;
        }

        // If max retries exceeded at the PendingExport level, don't execute
        // (Individual attribute changes have their own retry limits)
        if (pendingExport.ErrorCount >= pendingExport.MaxRetries)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Executes exports using the connector's export interface with batching support.
    /// </summary>
    private async Task ExecuteExportsViaConnectorAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        List<PendingExport> pendingExports,
        ExportExecutionResult result,
        ExportExecutionOptions options,
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback)
    {
        // Check if connector supports export using calls
        if (connector is IConnectorExportUsingCalls callsConnector)
        {
            await ExecuteUsingCallsWithBatchingAsync(connectedSystem, callsConnector, pendingExports, result, options, cancellationToken, progressCallback);
        }
        // Check if connector supports export using files
        else if (connector is IConnectorExportUsingFiles filesConnector)
        {
            await ExecuteUsingFilesWithBatchingAsync(connectedSystem, filesConnector, pendingExports, result, options, cancellationToken, progressCallback);
        }
        else
        {
            Log.Warning("ExecuteExportsViaConnectorAsync: Connector {ConnectorName} does not support export",
                connector.Name);

            // Mark all exports as failed with appropriate message
            foreach (var pendingExport in pendingExports)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await MarkExportFailedAsync(pendingExport, "Connector does not support export operations");
                result.FailedCount++;
            }
        }
    }

    /// <summary>
    /// Executes exports using the IConnectorExportUsingCalls interface with batching.
    /// </summary>
    private async Task ExecuteUsingCallsWithBatchingAsync(
        ConnectedSystem connectedSystem,
        IConnectorExportUsingCalls connector,
        List<PendingExport> pendingExports,
        ExportExecutionResult result,
        ExportExecutionOptions options,
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback)
    {
        try
        {
            // Inject certificate provider for connectors that support it
            if (connector is IConnectorCertificateAware certificateAwareConnector)
            {
                var certificateProvider = new CertificateProviderService(Application);
                certificateAwareConnector.SetCertificateProvider(certificateProvider);
            }

            // Inject credential protection for connectors that support it (for password decryption)
            if (connector is IConnectorCredentialAware credentialAwareConnector)
            {
                // Use pre-configured credential protection if available (from DI in JIM.Web),
                // otherwise create a new instance (for JIM.Worker which doesn't use DI)
                var credentialProtection = Application.CredentialProtection ??
                    new CredentialProtectionService(DataProtectionHelper.CreateProvider());
                credentialAwareConnector.SetCredentialProtection(credentialProtection);
            }

            // Open connection
            using (Diagnostics.Diagnostics.Connector.StartSpan("OpenExportConnection"))
            {
                connector.OpenExportConnection(connectedSystem.SettingValues);
            }
            Log.Debug("ExecuteUsingCallsWithBatchingAsync: Opened export connection for {SystemName}", connectedSystem.Name);

            try
            {
                // First pass: Execute exports that don't have unresolved references
                var immediateExports = pendingExports
                    .Where(pe => !pe.HasUnresolvedReferences)
                    .ToList();

                if (immediateExports.Count > 0)
                {
                    // Process in batches
                    var batches = immediateExports
                        .Select((export, index) => new { export, index })
                        .GroupBy(x => x.index / options.BatchSize)
                        .Select(g => g.Select(x => x.export).ToList())
                        .ToList();

                    var processedCount = 0;
                    foreach (var batch in batches)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Report progress (simple message without batch details for users)
                        await ReportProgressAsync(progressCallback, new ExportProgressInfo
                        {
                            Phase = ExportPhase.Executing,
                            TotalExports = result.TotalPendingExports,
                            ProcessedExports = processedCount,
                            CurrentBatchSize = batch.Count,
                            Message = "Exporting"
                        });

                        // Mark batch as executing
                        await MarkBatchAsExecutingAsync(batch);

                        // Execute batch via connector - now returns ExportResult list
                        List<ExportResult> exportResults;
                        using (Diagnostics.Diagnostics.Connector.StartSpan("ExportBatch").SetTag("batchSize", batch.Count))
                        {
                            exportResults = connector.Export(batch);
                        }

                        // Process results with ExportResult data
                        await ProcessBatchSuccessAsync(batch, exportResults, result);

                        processedCount += batch.Count;
                    }
                }

                // Second pass: Exports with unresolved references (deferred)
                var deferredExports = pendingExports
                    .Where(pe => pe.HasUnresolvedReferences)
                    .ToList();

                if (deferredExports.Count > 0)
                {
                    await ReportProgressAsync(progressCallback, new ExportProgressInfo
                    {
                        Phase = ExportPhase.ResolvingReferences,
                        TotalExports = result.TotalPendingExports,
                        ProcessedExports = result.SuccessCount,
                        Message = $"Resolving {deferredExports.Count} deferred exports"
                    });
                }

                foreach (var export in deferredExports)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Try to resolve references
                    var resolved = await TryResolveReferencesAsync(export, connectedSystem);
                    if (resolved)
                    {
                        export.HasUnresolvedReferences = false;
                        export.Status = PendingExportStatus.Executing;
                        export.LastAttemptedAt = DateTime.UtcNow;
                        await Application.Repository.ConnectedSystems.UpdatePendingExportAsync(export);

                        var exportResults = connector.Export(new List<PendingExport> { export });
                        var exportResult = exportResults.Count > 0 ? exportResults[0] : ExportResult.Succeeded();
                        await ProcessExportSuccessAsync(export, exportResult, result);
                    }
                    else
                    {
                        // Still unresolved - mark as deferred
                        await MarkExportDeferredAsync(export);
                        result.DeferredCount++;
                    }
                }

                // Capture created containers before closing connection
                if (connector is IConnectorContainerCreation containerCreator &&
                    containerCreator.CreatedContainerExternalIds.Count > 0)
                {
                    result.CreatedContainerExternalIds.AddRange(containerCreator.CreatedContainerExternalIds);
                    Log.Information("ExecuteUsingCallsWithBatchingAsync: Captured {Count} created container(s) for auto-selection",
                        containerCreator.CreatedContainerExternalIds.Count);
                }
            }
            finally
            {
                // Always close connection
                using (Diagnostics.Diagnostics.Connector.StartSpan("CloseExportConnection"))
                {
                    connector.CloseExportConnection();
                }
                Log.Debug("ExecuteUsingCallsWithBatchingAsync: Closed export connection for {SystemName}", connectedSystem.Name);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("ExecuteUsingCallsWithBatchingAsync: Export cancelled for {SystemName}", connectedSystem.Name);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ExecuteUsingCallsWithBatchingAsync: Failed to execute exports for {SystemName}", connectedSystem.Name);

            // Mark all pending exports as failed using batch update
            var executingExports = pendingExports.Where(pe => pe.Status == PendingExportStatus.Executing).ToList();
            foreach (var export in executingExports)
            {
                MarkExportFailed(export, ex.Message);
                result.FailedCount++;
            }
            if (executingExports.Count > 0)
            {
                await Application.Repository.ConnectedSystems.UpdatePendingExportsAsync(executingExports);
            }
        }
    }

    /// <summary>
    /// Marks a batch of exports as executing using batch update for efficiency.
    /// </summary>
    private async Task MarkBatchAsExecutingAsync(List<PendingExport> batch)
    {
        var now = DateTime.UtcNow;
        foreach (var export in batch)
        {
            export.Status = PendingExportStatus.Executing;
            export.LastAttemptedAt = now;
        }
        await Application.Repository.ConnectedSystems.UpdatePendingExportsAsync(batch);
    }

    /// <summary>
    /// Processes a batch of exports with their corresponding ExportResult data.
    /// Uses batch updates for efficiency.
    /// </summary>
    private async Task ProcessBatchSuccessAsync(List<PendingExport> batch, List<ExportResult> exportResults, ExportExecutionResult result)
    {
        var exportsToUpdate = new List<PendingExport>();
        var csosToUpdate = new List<(ConnectedSystemObject cso, ExportResult exportResult)>();

        for (var i = 0; i < batch.Count; i++)
        {
            var export = batch[i];
            var exportResult = i < exportResults.Count ? exportResults[i] : ExportResult.Succeeded();

            if (!exportResult.Success)
            {
                // Export failed - mark as failed
                MarkExportFailed(export, exportResult.ErrorMessage ?? "Export failed");
                exportsToUpdate.Add(export);
                result.FailedCount++;

                // Capture export data for activity tracking (before any state changes)
                result.ProcessedExportItems.Add(new ProcessedExportItem
                {
                    ChangeType = export.ChangeType,
                    ConnectedSystemObject = export.ConnectedSystemObject,
                    AttributeChangeCount = export.AttributeValueChanges.Count,
                    Succeeded = false,
                    ErrorMessage = exportResult.ErrorMessage ?? "Export failed",
                    ErrorCount = export.ErrorCount
                });
                continue;
            }

            // Capture export data for activity tracking (before deletion)
            result.ProcessedExportItems.Add(new ProcessedExportItem
            {
                ChangeType = export.ChangeType,
                ConnectedSystemObject = export.ConnectedSystemObject,
                AttributeChangeCount = export.AttributeValueChanges.Count,
                Succeeded = true
            });

            export.Status = PendingExportStatus.Exported;

            // For Create exports, update the CSO with the system-assigned external ID and status
            // For Update exports with SecondaryExternalId (e.g., LDAP renames), update the CSO's secondary ID
            if (export.ConnectedSystemObject != null &&
                (export.ChangeType == PendingExportChangeType.Create ||
                 !string.IsNullOrEmpty(exportResult.SecondaryExternalId)))
            {
                csosToUpdate.Add((export.ConnectedSystemObject, exportResult));
            }

            // Update attribute change statuses to ExportedPendingConfirmation
            UpdateAttributeChangeStatusesAfterExport(export);

            exportsToUpdate.Add(export);
            result.SuccessCount++;
            Log.Debug("ProcessBatchSuccessAsync: Successfully exported {ExportId}, awaiting confirmation via import", export.Id);
        }

        // Batch update all pending exports
        if (exportsToUpdate.Count > 0)
        {
            await Application.Repository.ConnectedSystems.UpdatePendingExportsAsync(exportsToUpdate);
        }

        // Update CSOs that need external ID or status changes
        // These are less frequent (only Create exports and renames), so individual updates are acceptable
        foreach (var (cso, exportResult) in csosToUpdate)
        {
            await UpdateCsoAfterSuccessfulExportAsync(cso, exportResult);
        }
    }

    /// <summary>
    /// Marks an export as failed and applies retry logic (Q6 decision).
    /// This is a synchronous version for batch processing - does not save to database.
    /// </summary>
    private static void MarkExportFailed(PendingExport export, string errorMessage, string? stackTrace = null)
    {
        export.ErrorCount++;
        export.LastErrorMessage = errorMessage;
        export.LastErrorStackTrace = stackTrace;
        export.LastAttemptedAt = DateTime.UtcNow;
        export.NextRetryAt = CalculateNextRetryTime(export.ErrorCount);

        // If max retries exceeded, mark as Failed (Q6 decision - requires manual intervention)
        if (export.ErrorCount >= export.MaxRetries)
        {
            export.Status = PendingExportStatus.Failed;
            Log.Warning("MarkExportFailed: Export {ExportId} has exceeded max retries ({MaxRetries}). Requires manual intervention.",
                export.Id, export.MaxRetries);
        }
        else
        {
            // Keep as Pending while we're still retrying
            export.Status = PendingExportStatus.Pending;
            Log.Warning("MarkExportFailed: Export {ExportId} failed (attempt {Attempt}/{MaxRetries}). Next retry at {NextRetry}. Error: {Error}",
                export.Id, export.ErrorCount, export.MaxRetries, export.NextRetryAt, errorMessage);
        }
    }

    /// <summary>
    /// Updates the CSO after a successful export.
    /// For Create exports, transitions the CSO from PendingProvisioning to Normal status
    /// and populates the external ID attribute with the system-assigned value.
    /// </summary>
    private async Task UpdateCsoAfterSuccessfulExportAsync(ConnectedSystemObject cso, ExportResult? exportResult = null)
    {
        var needsUpdate = false;
        var newAttributeValues = new List<ConnectedSystemObjectAttributeValue>();

        // Note: We do NOT transition CSO status from PendingProvisioning to Normal here.
        // The CSO should remain PendingProvisioning until the confirming import verifies
        // that the object actually exists in the target system. This allows the confirming
        // import to match the CSO by secondary external ID (e.g., distinguishedName) since
        // the primary external ID (e.g., objectGUID) is typically system-assigned and not
        // known until the confirming import.

        // Populate external ID attribute if provided in the export result
        if (exportResult != null && !string.IsNullOrEmpty(exportResult.ExternalId) && cso.ExternalIdAttributeId > 0)
        {
            // Get the attribute definition to determine the correct data type
            var externalIdAttribute = await Application.Repository.ConnectedSystems.GetAttributeAsync(cso.ExternalIdAttributeId);

            // Find or create the external ID attribute value
            var externalIdAttrValue = cso.AttributeValues
                .FirstOrDefault(av => av.AttributeId == cso.ExternalIdAttributeId);

            if (externalIdAttrValue == null)
            {
                // Create new attribute value for external ID
                externalIdAttrValue = new ConnectedSystemObjectAttributeValue
                {
                    ConnectedSystemObject = cso,
                    AttributeId = cso.ExternalIdAttributeId
                };
                cso.AttributeValues.Add(externalIdAttrValue);
                newAttributeValues.Add(externalIdAttrValue);
            }

            // Set the external ID value based on the attribute's data type
            // This ensures consistency with how import stores values
            if (externalIdAttribute?.Type == AttributeDataType.Guid && Guid.TryParse(exportResult.ExternalId, out var guidValue))
            {
                externalIdAttrValue.GuidValue = guidValue;
                externalIdAttrValue.StringValue = null;
            }
            else
            {
                // For Text or other types, or if attribute type is unknown, store as string
                externalIdAttrValue.StringValue = exportResult.ExternalId;
                externalIdAttrValue.GuidValue = null;
            }

            needsUpdate = true;
            Log.Information("UpdateCsoAfterSuccessfulExportAsync: Set CSO {CsoId} external ID to {ExternalId} (type: {AttrType})",
                cso.Id, exportResult.ExternalId, externalIdAttribute?.Type.ToString() ?? "Unknown");
        }

        // Update secondary external ID if provided
        if (exportResult != null && !string.IsNullOrEmpty(exportResult.SecondaryExternalId) && cso.SecondaryExternalIdAttributeId.HasValue)
        {
            var secondaryExternalIdAttrValue = cso.AttributeValues
                .FirstOrDefault(av => av.AttributeId == cso.SecondaryExternalIdAttributeId.Value);

            if (secondaryExternalIdAttrValue == null)
            {
                secondaryExternalIdAttrValue = new ConnectedSystemObjectAttributeValue
                {
                    ConnectedSystemObject = cso,
                    AttributeId = cso.SecondaryExternalIdAttributeId.Value
                };
                cso.AttributeValues.Add(secondaryExternalIdAttrValue);
                newAttributeValues.Add(secondaryExternalIdAttrValue);
            }

            secondaryExternalIdAttrValue.StringValue = exportResult.SecondaryExternalId;
            needsUpdate = true;
            Log.Debug("UpdateCsoAfterSuccessfulExportAsync: Set CSO {CsoId} secondary external ID to {SecondaryExternalId}",
                cso.Id, exportResult.SecondaryExternalId);
        }

        if (needsUpdate)
        {
            // Explicitly add new attribute values to ensure they are tracked by EF Core
            // This handles the case where the CSO was loaded without attribute values (PendingProvisioning)
            // and we're adding new values that need to be persisted
            await Application.Repository.ConnectedSystems.UpdateConnectedSystemObjectWithNewAttributeValuesAsync(cso, newAttributeValues);
            Log.Information("UpdateCsoAfterSuccessfulExportAsync: Updated CSO {CsoId}", cso.Id);
        }
    }

    /// <summary>
    /// Updates the status of attribute changes after a successful export.
    /// Changes with Pending or ExportedNotConfirmed status are transitioned to ExportedPendingConfirmation.
    /// </summary>
    private static void UpdateAttributeChangeStatusesAfterExport(PendingExport export)
    {
        var now = DateTime.UtcNow;

        foreach (var attrChange in export.AttributeValueChanges)
        {
            // Only update changes that were pending or being retried
            if (attrChange.Status == PendingExportAttributeChangeStatus.Pending ||
                attrChange.Status == PendingExportAttributeChangeStatus.ExportedNotConfirmed)
            {
                attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
                attrChange.ExportAttemptCount++;
                attrChange.LastExportedAt = now;

                Log.Debug("UpdateAttributeChangeStatusesAfterExport: Attribute {AttrId} status set to ExportedPendingConfirmation (attempt {Attempt})",
                    attrChange.AttributeId, attrChange.ExportAttemptCount);
            }
        }
    }

    /// <summary>
    /// Executes exports using the IConnectorExportUsingFiles interface with batching.
    /// </summary>
    private async Task ExecuteUsingFilesWithBatchingAsync(
        ConnectedSystem connectedSystem,
        IConnectorExportUsingFiles connector,
        List<PendingExport> pendingExports,
        ExportExecutionResult result,
        ExportExecutionOptions options,
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            await ReportProgressAsync(progressCallback, new ExportProgressInfo
            {
                Phase = ExportPhase.Executing,
                TotalExports = result.TotalPendingExports,
                ProcessedExports = 0,
                Message = $"Exporting {pendingExports.Count} changes to file"
            });

            // File-based export - execute all at once (file connectors typically batch internally)
            var exportResults = connector.Export(connectedSystem.SettingValues, pendingExports);

            // Check if the connector supports auto-confirm and the setting is enabled
            var autoConfirm = false;
            if (connector is IConnectorCapabilities caps && caps.SupportsAutoConfirmExport)
            {
                var autoConfirmSetting = connectedSystem.SettingValues
                    .SingleOrDefault(s => s.Setting.Name == "Auto-Confirm Exports");
                autoConfirm = autoConfirmSetting?.CheckboxValue ?? true; // default true when capability exists
            }

            // Process exports and collect for batch operations
            var exportsToUpdate = new List<PendingExport>();
            var exportsToDelete = new List<PendingExport>();
            var csosToUpdate = new List<(ConnectedSystemObject cso, ExportResult exportResult)>();

            for (var i = 0; i < pendingExports.Count; i++)
            {
                var export = pendingExports[i];
                var exportResult = i < exportResults.Count ? exportResults[i] : ExportResult.Succeeded();

                if (!exportResult.Success)
                {
                    MarkExportFailed(export, exportResult.ErrorMessage ?? "Export failed");
                    exportsToUpdate.Add(export);
                    result.FailedCount++;

                    // Capture export data for activity tracking
                    result.ProcessedExportItems.Add(new ProcessedExportItem
                    {
                        ChangeType = export.ChangeType,
                        ConnectedSystemObject = export.ConnectedSystemObject,
                        AttributeChangeCount = export.AttributeValueChanges.Count,
                        Succeeded = false,
                        ErrorMessage = exportResult.ErrorMessage ?? "Export failed",
                        ErrorCount = export.ErrorCount
                    });
                    continue;
                }

                // Capture export data for activity tracking (before deletion or status update)
                result.ProcessedExportItems.Add(new ProcessedExportItem
                {
                    ChangeType = export.ChangeType,
                    ConnectedSystemObject = export.ConnectedSystemObject,
                    AttributeChangeCount = export.AttributeValueChanges.Count,
                    Succeeded = true
                });

                // For Create exports, update the CSO status from PendingProvisioning to Normal
                if (export.ChangeType == PendingExportChangeType.Create && export.ConnectedSystemObject != null)
                {
                    csosToUpdate.Add((export.ConnectedSystemObject, exportResult));
                }

                // Update attribute change statuses to ExportedPendingConfirmation
                UpdateAttributeChangeStatusesAfterExport(export);

                if (autoConfirm)
                {
                    // Auto-confirm: for file-based exports where the file system is the source of truth,
                    // we can consider the export confirmed immediately
                    exportsToDelete.Add(export);
                }
                else
                {
                    // Standard behaviour: mark as exported, will be confirmed on next import
                    export.Status = PendingExportStatus.Exported;
                    export.LastAttemptedAt = DateTime.UtcNow;
                    exportsToUpdate.Add(export);
                }
                result.SuccessCount++;
            }

            // Batch update exports that need updating
            if (exportsToUpdate.Count > 0)
            {
                await Application.Repository.ConnectedSystems.UpdatePendingExportsAsync(exportsToUpdate);
            }

            // Batch delete exports that are auto-confirmed
            if (exportsToDelete.Count > 0)
            {
                await Application.Repository.ConnectedSystems.DeletePendingExportsAsync(exportsToDelete);
            }

            // Update CSOs (typically a small number for Create operations)
            foreach (var (cso, exportResult) in csosToUpdate)
            {
                await UpdateCsoAfterSuccessfulExportAsync(cso, exportResult);
            }

            Log.Information("ExecuteUsingFilesWithBatchingAsync: Exported {Count} changes to file for {SystemName}",
                pendingExports.Count, connectedSystem.Name);
        }
        catch (OperationCanceledException)
        {
            Log.Information("ExecuteUsingFilesWithBatchingAsync: Export cancelled for {SystemName}", connectedSystem.Name);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ExecuteUsingFilesWithBatchingAsync: Failed to export to file for {SystemName}", connectedSystem.Name);

            // Mark all as failed using batch update
            var now = DateTime.UtcNow;
            foreach (var export in pendingExports)
            {
                export.ErrorCount++;
                export.LastErrorMessage = ex.Message;
                export.LastErrorStackTrace = ex.StackTrace;
                export.LastAttemptedAt = now;
                export.NextRetryAt = CalculateNextRetryTime(export.ErrorCount);
                export.Status = PendingExportStatus.ExportNotConfirmed;
            }
            await Application.Repository.ConnectedSystems.UpdatePendingExportsAsync(pendingExports);

            result.FailedCount = pendingExports.Count;
        }
    }

    /// <summary>
    /// Attempts to resolve unresolved reference attributes in a pending export.
    /// For LDAP systems, references like 'member' need to be resolved to Distinguished Names (DN),
    /// not the primary external ID (objectGUID). We use the secondary external ID when available.
    /// </summary>
    private async Task<bool> TryResolveReferencesAsync(PendingExport pendingExport, ConnectedSystem targetSystem)
    {
        var allResolved = true;

        foreach (var attrChange in pendingExport.AttributeValueChanges)
        {
            if (string.IsNullOrEmpty(attrChange.UnresolvedReferenceValue))
                continue;

            // The unresolved reference value contains an MVO ID
            if (!Guid.TryParse(attrChange.UnresolvedReferenceValue, out var referencedMvoId))
                continue;

            // Look up the CSO for this MVO in the target system
            var referencedCso = await Application.Repository.ConnectedSystems
                .GetConnectedSystemObjectByMetaverseObjectIdAsync(referencedMvoId, targetSystem.Id);

            if (referencedCso != null)
            {
                // For reference attributes, prefer the secondary external ID (e.g., DN for LDAP)
                // as this is what the connected system uses for references.
                // Fall back to primary external ID if secondary is not available.
                var secondaryExternalIdAttr = referencedCso.AttributeValues
                    .FirstOrDefault(av => av.Attribute?.IsSecondaryExternalId == true);

                var externalIdAttr = referencedCso.AttributeValues
                    .FirstOrDefault(av => av.Attribute?.IsExternalId == true);

                // Use secondary external ID (DN) if available, otherwise fall back to primary
                var resolvedAttr = secondaryExternalIdAttr ?? externalIdAttr;

                if (resolvedAttr != null)
                {
                    attrChange.StringValue = resolvedAttr.StringValue ??
                                             resolvedAttr.GuidValue?.ToString() ??
                                             resolvedAttr.IntValue?.ToString();
                    attrChange.UnresolvedReferenceValue = null;

                    Log.Debug("Resolved reference for MVO {MvoId} to {Value} using {IdType}",
                        referencedMvoId,
                        attrChange.StringValue,
                        secondaryExternalIdAttr != null ? "secondary external ID (DN)" : "primary external ID");
                }
                else
                {
                    Log.Warning("Could not resolve reference for MVO {MvoId}: CSO {CsoId} has no external ID attribute",
                        referencedMvoId, referencedCso.Id);
                    allResolved = false;
                }
            }
            else
            {
                // Still unresolved - CSO doesn't exist yet in target system
                allResolved = false;
            }
        }

        return allResolved;
    }

    /// <summary>
    /// Executes a second pass for deferred references that might now be resolvable.
    /// </summary>
    private async Task ExecuteDeferredReferencesAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        ExportExecutionResult result)
    {
        // Get any exports that were marked as having unresolved references
        var deferredExports = await Application.Repository.ConnectedSystems.GetPendingExportsAsync(connectedSystem.Id);
        var unresolvedExports = deferredExports
            .Where(pe => pe.HasUnresolvedReferences && pe.Status == PendingExportStatus.Pending)
            .ToList();

        if (unresolvedExports.Count == 0)
            return;

        Log.Debug("ExecuteDeferredReferencesAsync: Checking {Count} deferred exports for resolution", unresolvedExports.Count);

        foreach (var export in unresolvedExports)
        {
            var resolved = await TryResolveReferencesAsync(export, connectedSystem);
            if (resolved)
            {
                export.HasUnresolvedReferences = false;
                await Application.Repository.ConnectedSystems.UpdatePendingExportAsync(export);
                Log.Debug("ExecuteDeferredReferencesAsync: Resolved references for export {ExportId}", export.Id);
            }
        }
    }

    /// <summary>
    /// Processes a successful export execution with ExportResult data.
    /// </summary>
    private async Task ProcessExportSuccessAsync(PendingExport export, ExportResult exportResult, ExportExecutionResult result)
    {
        if (!exportResult.Success)
        {
            await MarkExportFailedAsync(export, exportResult.ErrorMessage ?? "Export failed");
            result.FailedCount++;

            // Capture export data for activity tracking (before any state changes)
            result.ProcessedExportItems.Add(new ProcessedExportItem
            {
                ChangeType = export.ChangeType,
                ConnectedSystemObject = export.ConnectedSystemObject,
                AttributeChangeCount = export.AttributeValueChanges.Count,
                Succeeded = false,
                ErrorMessage = exportResult.ErrorMessage ?? "Export failed",
                ErrorCount = export.ErrorCount
            });
            return;
        }

        // Capture export data for activity tracking (before deletion)
        result.ProcessedExportItems.Add(new ProcessedExportItem
        {
            ChangeType = export.ChangeType,
            ConnectedSystemObject = export.ConnectedSystemObject,
            AttributeChangeCount = export.AttributeValueChanges.Count,
            Succeeded = true
        });

        export.Status = PendingExportStatus.Exported;

        // For Create exports, update the CSO with external ID and status
        if (export.ChangeType == PendingExportChangeType.Create && export.ConnectedSystemObject != null)
        {
            await UpdateCsoAfterSuccessfulExportAsync(export.ConnectedSystemObject, exportResult);
        }

        // Update attribute change statuses to ExportedPendingConfirmation
        // They will be confirmed (and deleted) or marked for retry during the next import
        UpdateAttributeChangeStatusesAfterExport(export);

        await Application.Repository.ConnectedSystems.UpdatePendingExportAsync(export);

        result.SuccessCount++;
        Log.Debug("ProcessExportSuccessAsync: Successfully exported {ExportId}, awaiting confirmation via import", export.Id);
    }

    /// <summary>
    /// Marks an export as deferred (has unresolved references).
    /// Does not increment error count since this is expected behaviour.
    /// </summary>
    private async Task MarkExportDeferredAsync(PendingExport export)
    {
        export.Status = PendingExportStatus.Pending;
        export.HasUnresolvedReferences = true;
        export.LastAttemptedAt = DateTime.UtcNow;
        // Use a shorter retry interval for deferred references
        export.NextRetryAt = DateTime.UtcNow.AddMinutes(5);

        await Application.Repository.ConnectedSystems.UpdatePendingExportAsync(export);
        Log.Debug("MarkExportDeferredAsync: Export {ExportId} deferred due to unresolved references", export.Id);
    }

    /// <summary>
    /// Marks an export as failed and applies retry logic (Q6 decision).
    /// </summary>
    private async Task MarkExportFailedAsync(PendingExport export, string errorMessage, string? stackTrace = null)
    {
        export.ErrorCount++;
        export.LastErrorMessage = errorMessage;
        export.LastErrorStackTrace = stackTrace;
        export.LastAttemptedAt = DateTime.UtcNow;
        export.NextRetryAt = CalculateNextRetryTime(export.ErrorCount);

        // If max retries exceeded, mark as Failed (Q6 decision - requires manual intervention)
        if (export.ErrorCount >= export.MaxRetries)
        {
            export.Status = PendingExportStatus.Failed;
            Log.Warning("MarkExportFailedAsync: Export {ExportId} has exceeded max retries ({MaxRetries}). Requires manual intervention.",
                export.Id, export.MaxRetries);
        }
        else
        {
            // Keep as Pending while we're still retrying
            // ExportNotConfirmed is for when export succeeded but some values didn't persist
            export.Status = PendingExportStatus.Pending;
            Log.Warning("MarkExportFailedAsync: Export {ExportId} failed (attempt {Attempt}/{MaxRetries}). Next retry at {NextRetry}. Error: {Error}",
                export.Id, export.ErrorCount, export.MaxRetries, export.NextRetryAt, errorMessage);
        }

        await Application.Repository.ConnectedSystems.UpdatePendingExportAsync(export);
    }

    /// <summary>
    /// Calculates the next retry time using exponential backoff (Q6 decision).
    /// Uses 2^n minutes where n is the error count, capped at 1 hour.
    /// </summary>
    private static DateTime CalculateNextRetryTime(int errorCount)
    {
        // Exponential backoff: 2, 4, 8, 16, 32, 60 (max) minutes
        var minutes = Math.Min(Math.Pow(2, errorCount), 60);
        return DateTime.UtcNow.AddMinutes(minutes);
    }

    /// <summary>
    /// Gets the count of pending exports that require manual intervention (exceeded max retries).
    /// </summary>
    public async Task<int> GetFailedExportsCountAsync(int connectedSystemId)
    {
        var pendingExports = await Application.Repository.ConnectedSystems.GetPendingExportsAsync(connectedSystemId);
        return pendingExports.Count(pe => pe.Status == PendingExportStatus.Failed);
    }

    /// <summary>
    /// Retries all failed exports for a connected system (manual intervention).
    /// Resets error count and status.
    /// </summary>
    public async Task RetryFailedExportsAsync(int connectedSystemId)
    {
        var pendingExports = await Application.Repository.ConnectedSystems.GetPendingExportsAsync(connectedSystemId);
        var failedExports = pendingExports.Where(pe => pe.Status == PendingExportStatus.Failed).ToList();

        foreach (var export in failedExports)
        {
            export.ErrorCount = 0;
            export.Status = PendingExportStatus.Pending;
            export.NextRetryAt = null;
            export.LastErrorMessage = null;
            export.LastErrorStackTrace = null;
            await Application.Repository.ConnectedSystems.UpdatePendingExportAsync(export);
        }

        Log.Information("RetryFailedExportsAsync: Reset {Count} failed exports for system {SystemId}",
            failedExports.Count, connectedSystemId);
    }
}
