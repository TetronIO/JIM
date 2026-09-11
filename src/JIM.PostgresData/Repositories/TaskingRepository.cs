// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;
using JIM.Utilities;
using Microsoft.EntityFrameworkCore;
using Serilog;
namespace JIM.PostgresData.Repositories;

public class TaskingRepository : ITaskingRepository
{
    private PostgresDataRepository Repository { get; }

    internal TaskingRepository(PostgresDataRepository dataRepository)
    {
        Repository = dataRepository;
    }

    public async Task CreateWorkerTaskAsync(WorkerTask workerTask)
    {
        if (workerTask.Activity == null)
            throw new InvalidDataException("CreateWorkerTaskAsync: workerTask.Activity was null. Cannot continue.");

        switch (workerTask)
        {
            case ExampleDataTemplateWorkerTask dataGenerationTemplateWorkerTask:
                Repository.Database.ExampleDataTemplateWorkerTasks.Add(dataGenerationTemplateWorkerTask);
                await Repository.Database.SaveChangesAsync();
                break;
            case SynchronisationWorkerTask synchronisationWorkerTask:
                Repository.Database.SynchronisationWorkerTasks.Add(synchronisationWorkerTask);
                await Repository.Database.SaveChangesAsync();
                break;
            case ConfigurationChangePreviewWorkerTask configurationChangePreviewWorkerTask:
                // Unlike every other task type, this one attaches to an Activity that already exists. Add() walks
                // the graph and marks every untracked entity it reaches for insertion, so without tracking the
                // Activity first the insert would try to create it a second time and fail on its primary key.
                Repository.Database.Entry(configurationChangePreviewWorkerTask.Activity).State = EntityState.Unchanged;
                Repository.Database.ConfigurationChangePreviewWorkerTasks.Add(configurationChangePreviewWorkerTask);
                await Repository.Database.SaveChangesAsync();
                break;
            case TemporalScopeReconciliationWorkerTask temporalScopeReconciliationTask:
                Repository.Database.TemporalScopeReconciliationWorkerTasks.Add(temporalScopeReconciliationTask);
                await Repository.Database.SaveChangesAsync();
                break;
            case HistoryRetentionCleanupWorkerTask historyRetentionCleanupTask:
                Repository.Database.HistoryRetentionCleanupWorkerTasks.Add(historyRetentionCleanupTask);
                await Repository.Database.SaveChangesAsync();
                break;
            case ClearConnectedSystemObjectsWorkerTask clearConnectedSystemObjectsTask:
                Repository.Database.ClearConnectedSystemObjectsTasks.Add(clearConnectedSystemObjectsTask);
                await Repository.Database.SaveChangesAsync();
                break;
            case DeleteConnectedSystemWorkerTask deleteConnectedSystemTask:
                Repository.Database.DeleteConnectedSystemWorkerTasks.Add(deleteConnectedSystemTask);
                await Repository.Database.SaveChangesAsync();
                break;
            case SchemaRefreshRemovalWorkerTask schemaRefreshRemovalTask:
                Repository.Database.SchemaRefreshRemovalWorkerTasks.Add(schemaRefreshRemovalTask);
                await Repository.Database.SaveChangesAsync();
                break;
            case DeleteSyncRuleWorkerTask deleteSyncRuleTask:
                Repository.Database.DeleteSyncRuleWorkerTasks.Add(deleteSyncRuleTask);
                await Repository.Database.SaveChangesAsync();
                break;
            case AuxiliaryClassDiscoveryWorkerTask auxiliaryClassDiscoveryTask:
                Repository.Database.AuxiliaryClassDiscoveryWorkerTasks.Add(auxiliaryClassDiscoveryTask);
                await Repository.Database.SaveChangesAsync();
                break;
            default:
                throw new ArgumentException("workerTask was of an unexpected type: " + workerTask.GetType());
        }
    }

    public async Task<WorkerTask?> GetWorkerTaskAsync(Guid id)
    {
        return await Repository.Database.WorkerTasks
            .Include(st => st.Activity)
            .SingleOrDefaultAsync(st => st.Id == id);
    }

    public async Task<WorkerTask?> GetWorkerTaskByActivityIdAsync(Guid activityId)
    {
        return await Repository.Database.WorkerTasks
            .Include(st => st.Activity)
            .SingleOrDefaultAsync(st => st.Activity.Id == activityId);
    }

    public async Task<List<WorkerTask>> GetWorkerTasksAsync()
    {
        return await Repository.Database.WorkerTasks
            .Include(st => st.Activity)
            .ToListAsync();
    }

    public async Task<List<WorkerTaskHeader>> GetWorkerTaskHeadersAsync()
    {
        var workerTaskHeaders = new List<WorkerTaskHeader>();
        var workerTasks = await Repository.Database.WorkerTasks
            .Include(st => st.Activity)
            .Include(st => st.ScheduleExecution)
            .OrderByDescending(q => q.Timestamp)
            .ToListAsync();

        var stepsByActivity = await GetStepsForActivitiesAsync(workerTasks);

        foreach (var workerTask in workerTasks)
        {
            workerTaskHeaders.Add(new WorkerTaskHeader
            {
                Id = workerTask.Id,
                Status = workerTask.Status,
                Timestamp = workerTask.Timestamp,
                Name = await GetWorkerHeaderNameAsync(workerTask),
                Type = GetWorkerTaskType(workerTask),
                InitiatedByType = workerTask.InitiatedByType,
                InitiatedById = workerTask.InitiatedById,
                InitiatedByName = workerTask.InitiatedByName,
                ActivityId = workerTask.Activity?.Id,
                ObjectsToProcess = workerTask.Activity?.ObjectsToProcess,
                ObjectsProcessed = workerTask.Activity?.ObjectsProcessed,
                ProgressMessage = workerTask.Activity?.Message,
                ScheduleExecutionId = workerTask.ScheduleExecutionId,
                ScheduleExecutionName = workerTask.ScheduleExecution?.ScheduleName,
                ScheduleStepIndex = workerTask.ScheduleStepIndex,
                ScheduleTotalSteps = workerTask.ScheduleExecution?.TotalSteps,
                ScheduleCurrentStepIndex = workerTask.ScheduleExecution?.CurrentStepIndex,
                Steps = workerTask.Activity != null && stepsByActivity.TryGetValue(workerTask.Activity.Id, out var steps) ? steps : null
            });
        }
        return workerTaskHeaders;
    }

    /// <summary>
    /// The run steps (#454) for every task in the queue that has any, keyed by Activity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phases are their own table keyed by ActivityId rather than a navigation off the Activity, so
    /// they are fetched in one batch and matched up here; the alternative is a query per row, and
    /// the queue re-reads on every progress notification.
    /// </para>
    /// <para>
    /// Bounded by design: a Worker Task row is deleted once its work completes, so this only ever
    /// sees work still in flight, at roughly ten phase rows apiece.
    /// </para>
    /// <para>
    /// Every phase is fetched, including a Connector's own, and <see cref="RunPhaseSummary.From"/>
    /// decides which of them are steps of the run. Filtering to <c>ParentKey == null</c> in SQL
    /// would be cheaper by a handful of rows and would put a second definition of "what counts as a
    /// step" outside <see cref="RunPhaseReading"/>, which is the thing that must not happen.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<Guid, RunPhaseSummary>> GetStepsForActivitiesAsync(List<WorkerTask> workerTasks)
    {
        var activityIds = workerTasks
            .Where(t => t.Activity != null)
            .Select(t => t.Activity!.Id)
            .Distinct()
            .ToList();

        if (activityIds.Count == 0)
            return [];

        var phases = await Repository.Database.ActivityPhases
            .AsNoTracking()
            .Where(p => activityIds.Contains(p.ActivityId))
            .ToListAsync();

        return phases
            .GroupBy(p => p.ActivityId)
            .Select(g => new { g.Key, Summary = RunPhaseSummary.From(g) })
            .Where(x => x.Summary != null)
            .ToDictionary(x => x.Key, x => x.Summary!);
    }

    public async Task<WorkerTask?> GetNextWorkerTaskAsync()
    {
        return await Repository.Database.WorkerTasks
            .Include(st => st.Activity)
            .Where(st => st.Status == WorkerTaskStatus.Queued)
            .OrderBy(st => st.Timestamp)
            .FirstOrDefaultAsync();
    }

    public async Task<List<WorkerTask>> GetNextWorkerTasksToProcessAsync()
    {
        var tasks = new List<WorkerTask>();
        foreach (var task in await Repository.Database.WorkerTasks
                     .Include(st => st.Activity)
                     .Where(st => st.Status == WorkerTaskStatus.Queued)
                     .OrderBy(st => st.ScheduleStepIndex ?? int.MaxValue)
                     .ThenBy(st => st.Timestamp)
                     .ToListAsync())
        {
            if (task.ExecutionMode == WorkerTaskExecutionMode.Sequential)
            {
                tasks.Add(task);
                break;
            }

            if (task.ExecutionMode == WorkerTaskExecutionMode.Parallel)
                tasks.Add(task);
            else
                break;
        }

        await UpdateWorkerTasksAsProcessingAsync(tasks);
        return tasks;
    }

    public async Task<List<WorkerTask>> GetWorkerTasksThatNeedCancellingAsync()
    {
        return await Repository.Database.WorkerTasks.Include(st => st.Activity).Where(st => st.Status == WorkerTaskStatus.CancellationRequested).ToListAsync();
    }

    public async Task<List<WorkerTask>> GetWorkerTasksThatNeedCancellingAsync(Guid[] workerTaskIds)
    {
        return await Repository.Database.WorkerTasks.Include(st => st.Activity).Where(q => workerTaskIds.Contains(q.Id) && q.Status == WorkerTaskStatus.CancellationRequested).ToListAsync();
    }

    public async Task<DeleteConnectedSystemWorkerTask?> GetDeleteConnectedSystemWorkerTaskAsync(int connectedSystemId)
    {
        // Filtered off the shared Worker Task set rather than the typed one, so the discriminator this depends on
        // is part of what the query does rather than something taken on trust. At most one deletion task should
        // exist per system (a fenced system refuses to queue a second); the oldest is returned defensively.
        return await Repository.Database.WorkerTasks
            .OfType<DeleteConnectedSystemWorkerTask>()
            .Include(t => t.Activity)
            .Where(t => t.ConnectedSystemId == connectedSystemId)
            .OrderBy(t => t.Timestamp)
            .FirstOrDefaultAsync();
    }

    public async Task<ExampleDataTemplateWorkerTask?> GetFirstExampleDataWorkerTaskAsync(int dataGenerationTemplateId)
    {
        return await Repository.Database.ExampleDataTemplateWorkerTasks.OrderBy(q => q.Timestamp).FirstOrDefaultAsync(q => q.TemplateId == dataGenerationTemplateId);
    }

    public async Task<WorkerTaskHeader?> GetFirstExampleDataTemplateWorkerTaskHeaderAsync(int templateId)
    {
        var workerTask = await Repository.Database.ExampleDataTemplateWorkerTasks
            .Include(q => q.Activity)
            .Where(q => q.TemplateId == templateId)
            .OrderBy(q => q.Id)
            .FirstOrDefaultAsync();

        if (workerTask == null)
            return null;

        return new WorkerTaskHeader
        {
            Id = workerTask.Id,
            Status = workerTask.Status,
            Timestamp = workerTask.Timestamp,
            Name = await GetWorkerHeaderNameAsync(workerTask),
            Type = GetWorkerTaskType(workerTask),
            InitiatedByType = workerTask.InitiatedByType,
            InitiatedById = workerTask.InitiatedById,
            InitiatedByName = workerTask.InitiatedByName,
            ActivityId = workerTask.Activity?.Id,
            ObjectsToProcess = workerTask.Activity?.ObjectsToProcess,
            ObjectsProcessed = workerTask.Activity?.ObjectsProcessed,
            ProgressMessage = workerTask.Activity?.Message
        };
    }

    public async Task UpdateWorkerTaskAsync(WorkerTask workerTask)
    {
        switch (workerTask)
        {
            case ExampleDataTemplateWorkerTask dataGenerationTemplateWorkerTask:
            {
                var dbExampleDataTemplateWorkerTask = await Repository.Database.ExampleDataTemplateWorkerTasks.Include(st => st.Activity).AsTracking().SingleOrDefaultAsync(q => q.Id == workerTask.Id);
                if (dbExampleDataTemplateWorkerTask == null)
                {
                    Log.Error("UpdateWorkerTaskAsync: Could not retrieve a ExampleDataTemplateWorkerTask object to update.");
                    return;
                }

                // map scalar value updates to the db version of the object
                Repository.Database.Entry(dbExampleDataTemplateWorkerTask).CurrentValues.SetValues(dataGenerationTemplateWorkerTask);
                break;
            }
            case SynchronisationWorkerTask synchronisationWorkerTask:
            {
                var dbSynchronisationWorkerTask = await Repository.Database.SynchronisationWorkerTasks.Include(st => st.Activity).AsTracking().SingleOrDefaultAsync(q => q.Id == workerTask.Id);
                if (dbSynchronisationWorkerTask == null)
                {
                    Log.Error("UpdateWorkerTaskAsync: Could not retrieve a SynchronisationWorkerTask object to update.");
                    return;
                }

                // map scalar value updates to the db version of the object
                Repository.Database.Entry(dbSynchronisationWorkerTask).CurrentValues.SetValues(synchronisationWorkerTask);
                break;
            }
            case DeleteConnectedSystemWorkerTask deleteConnectedSystemWorkerTask:
            {
                // The Synchronised Deprovisioning run (#809) persists its resumability checkpoint through
                // this path after each completed batch.
                var dbDeleteConnectedSystemWorkerTask = await Repository.Database.DeleteConnectedSystemWorkerTasks.Include(st => st.Activity).AsTracking().SingleOrDefaultAsync(q => q.Id == workerTask.Id);
                if (dbDeleteConnectedSystemWorkerTask == null)
                {
                    Log.Error("UpdateWorkerTaskAsync: Could not retrieve a DeleteConnectedSystemWorkerTask object to update.");
                    return;
                }

                // map scalar value updates to the db version of the object
                Repository.Database.Entry(dbDeleteConnectedSystemWorkerTask).CurrentValues.SetValues(deleteConnectedSystemWorkerTask);
                break;
            }
        }

        await Repository.Database.SaveChangesAsync();
    }

    public async Task DeleteWorkerTaskAsync(WorkerTask workerTask)
    {
        // re-retrieve the worker task to avoid issues with EF
        var localWorkerTask = await Repository.Database.WorkerTasks.AsTracking().SingleOrDefaultAsync(q => q.Id == workerTask.Id);
        if (localWorkerTask != null)
        {
            Repository.Database.WorkerTasks.Remove(localWorkerTask);
            await Repository.Database.SaveChangesAsync();
        }
        else
        {
            Log.Debug($"DeleteWorkerTaskAsync: Did not delete worker task {workerTask.Id} as it doesn't exist (already deleted?)");
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Scheduler Service Queries
    // -----------------------------------------------------------------------------------------------------------------

    public async Task<List<WorkerTask>> GetWorkerTasksByScheduleExecutionAsync(Guid scheduleExecutionId)
    {
        return await Repository.Database.WorkerTasks
            .Include(st => st.Activity)
            .Where(st => st.ScheduleExecutionId == scheduleExecutionId)
            .OrderBy(st => st.ScheduleStepIndex)
            .ThenBy(st => st.Timestamp)
            .ToListAsync();
    }

    public async Task<List<WorkerTask>> GetWorkerTasksByScheduleExecutionStepAsync(Guid scheduleExecutionId, int stepIndex)
    {
        return await Repository.Database.WorkerTasks
            .Include(st => st.Activity)
            .Where(st => st.ScheduleExecutionId == scheduleExecutionId && st.ScheduleStepIndex == stepIndex)
            .OrderBy(st => st.Timestamp)
            .ToListAsync();
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Schedule Step Advancement (Worker-driven)
    // -----------------------------------------------------------------------------------------------------------------

    public async Task<int> GetWorkerTaskCountByExecutionStepAsync(Guid scheduleExecutionId, int stepIndex)
    {
        return await Repository.Database.WorkerTasks
            .CountAsync(st => st.ScheduleExecutionId == scheduleExecutionId && st.ScheduleStepIndex == stepIndex);
    }

    public async Task<int> TransitionStepToQueuedAsync(Guid scheduleExecutionId, int stepIndex)
    {
        return await Repository.Database.WorkerTasks
            .Where(st => st.ScheduleExecutionId == scheduleExecutionId
                         && st.ScheduleStepIndex == stepIndex
                         && st.Status == WorkerTaskStatus.WaitingForPreviousStep)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, WorkerTaskStatus.Queued));
    }

    public async Task<int> DeleteWaitingTasksForExecutionAsync(Guid scheduleExecutionId)
    {
        // Fail the activities for all waiting tasks before deleting them
        var waitingTasks = await Repository.Database.WorkerTasks
            .Include(st => st.Activity)
            .AsTracking()
            .Where(st => st.ScheduleExecutionId == scheduleExecutionId
                         && st.Status == WorkerTaskStatus.WaitingForPreviousStep)
            .ToListAsync();

        foreach (var task in waitingTasks)
        {
            if (task.Activity != null)
            {
                task.Activity.Status = Models.Activities.ActivityStatus.Cancelled;
                task.Activity.TotalActivityTime = DateTime.UtcNow - task.Activity.Created;
                task.Activity.Message = "Cancelled: previous step failed or execution was cancelled.";
            }
        }

        if (waitingTasks.Count > 0)
            await Repository.Database.SaveChangesAsync();

        // Now delete the worker tasks
        var deletedCount = await Repository.Database.WorkerTasks
            .Where(st => st.ScheduleExecutionId == scheduleExecutionId
                         && st.Status == WorkerTaskStatus.WaitingForPreviousStep)
            .ExecuteDeleteAsync();

        return deletedCount;
    }

    public async Task<int?> GetNextWaitingStepIndexAsync(Guid scheduleExecutionId)
    {
        return await Repository.Database.WorkerTasks
            .Where(st => st.ScheduleExecutionId == scheduleExecutionId
                         && st.Status == WorkerTaskStatus.WaitingForPreviousStep)
            .MinAsync(st => (int?)st.ScheduleStepIndex);
    }

    #region private methods
    /// <summary>
    /// What a Worker Task is called in the queue, derived from whatever it is a task to do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads through the repository's own context. It used to open a <see cref="JimDbContext"/> of
    /// its own, once per Worker Task: an extra pooled connection for every row in the queue, on a
    /// read that re-runs on every progress notification, and configured from environment variables
    /// rather than from whatever the caller was working against.
    /// </para>
    /// <para>
    /// Every lookup tolerates its subject having been deleted. A queue row whose configuration has
    /// gone is not an error state; it is the ordinary consequence of deleting a Connected System
    /// while one of its tasks is still queued, and the queue is exactly where an administrator
    /// would go to find out about it.
    /// </para>
    /// </remarks>
    private async Task<string> GetWorkerHeaderNameAsync(WorkerTask workerTask)
    {
        var db = Repository.Database;
        switch (workerTask)
        {
            case ExampleDataTemplateWorkerTask dataGenerationTemplateWorkerTask:
            {
                var templatePart = await db.ExampleDataTemplates.Select(q => new { q.Id, q.Name }).SingleOrDefaultAsync(q => q.Id == dataGenerationTemplateWorkerTask.TemplateId);
                return templatePart?.Name ?? $"Example Data Template {dataGenerationTemplateWorkerTask.TemplateId}";
            }
            case SynchronisationWorkerTask synchronisationWorkerTask:
            {
                // The Connected System behind the Run Profile is looked up by Single because the
                // schema guarantees it: ConnectedSystemRunProfiles.ConnectedSystemId cascades on
                // delete, so a Run Profile whose Connected System has gone cannot exist to be read.
                // The Run Profile itself is a different matter, and is why the outer lookup tolerates
                // nothing coming back: deleting a Connected System takes its Run Profiles with it
                // while leaving any already-queued task behind.
                var runProfilePart = await db.ConnectedSystemRunProfiles.Select(q => new { q.Id, q.Name, ConnectedSystemName = db.ConnectedSystems.Single(cs => cs.Id == q.ConnectedSystemId).Name }).
                    SingleOrDefaultAsync(q => q.Id == synchronisationWorkerTask.ConnectedSystemRunProfileId);

                return runProfilePart != null
                    ? $"{runProfilePart.ConnectedSystemName} - {runProfilePart.Name}"
                    : $"Run Profile {synchronisationWorkerTask.ConnectedSystemRunProfileId}";
            }
            case ClearConnectedSystemObjectsWorkerTask clearConnectedSystemObjectsTask:
            {
                // Named the same way a delete task for a missing system already is, so an orphaned
                // clear task reads alike. Single() here threw instead, and because the read builds
                // the whole list before returning, one orphaned row took out every other row with
                // it, including the ones needed to work out what had happened.
                var systemToClear = await db.ConnectedSystems.SingleOrDefaultAsync(q => q.Id == clearConnectedSystemObjectsTask.ConnectedSystemId);
                return systemToClear?.Name ?? $"Connected System {clearConnectedSystemObjectsTask.ConnectedSystemId}";
            }
            case DeleteConnectedSystemWorkerTask deleteConnectedSystemTask:
            {
                // The Connected System may be null: this is the task that deletes it.
                var systemToDelete = await db.ConnectedSystems.SingleOrDefaultAsync(q => q.Id == deleteConnectedSystemTask.ConnectedSystemId);
                return systemToDelete?.Name ?? $"Connected System {deleteConnectedSystemTask.ConnectedSystemId}";
            }
            case TemporalScopeReconciliationWorkerTask:
                // the sweep carries no per-instance configuration, so the feature name is the display name
                return "Temporal Scope Reconciliation";
            case HistoryRetentionCleanupWorkerTask:
                // likewise: the pass reads every cutoff from Service Settings, so there is nothing per-instance to name
                return "History Retention Cleanup";
            case SchemaRefreshRemovalWorkerTask schemaRefreshRemovalTask:
            {
                // The Connected System survives a schema refresh removal, but tolerate its deletion the way the
                // clear and delete tasks above do: an orphaned queue row must never take the list out.
                var refreshedSystem = await db.ConnectedSystems.SingleOrDefaultAsync(q => q.Id == schemaRefreshRemovalTask.ConnectedSystemId);
                return refreshedSystem?.Name ?? $"Connected System {schemaRefreshRemovalTask.ConnectedSystemId}";
            }
            case DeleteSyncRuleWorkerTask deleteSyncRuleTask:
            {
                // The Synchronisation Rule may be gone: this is the task that deletes it as its final step.
                var ruleToDelete = await db.SyncRules.SingleOrDefaultAsync(q => q.Id == deleteSyncRuleTask.SyncRuleId);
                return ruleToDelete?.Name ?? $"Synchronisation Rule {deleteSyncRuleTask.SyncRuleId}";
            }
            default:
                return "Unknown WorkerTask type";
        }
    }

    private static string GetWorkerTaskType(WorkerTask workerTask)
    {
        return workerTask switch
        {
            ExampleDataTemplateWorkerTask => nameof(ExampleDataTemplateWorkerTask).SplitOnCapitalLetters(),
            SynchronisationWorkerTask => nameof(SynchronisationWorkerTask).SplitOnCapitalLetters(),
            ClearConnectedSystemObjectsWorkerTask => nameof(ClearConnectedSystemObjectsWorkerTask).SplitOnCapitalLetters(),
            // The queue must distinguish the two deletion modes (#809): a Synchronised Deprovisioning run is
            // long-lived per-object work, where the immediate deletion is a bulk operation.
            DeleteConnectedSystemWorkerTask { SynchronisedDeprovisioning: true } => "Deprovision Connected System Worker Task",
            DeleteConnectedSystemWorkerTask => nameof(DeleteConnectedSystemWorkerTask).SplitOnCapitalLetters(),
            TemporalScopeReconciliationWorkerTask => nameof(TemporalScopeReconciliationWorkerTask).SplitOnCapitalLetters(),
            HistoryRetentionCleanupWorkerTask => nameof(HistoryRetentionCleanupWorkerTask).SplitOnCapitalLetters(),
            SchemaRefreshRemovalWorkerTask => nameof(SchemaRefreshRemovalWorkerTask).SplitOnCapitalLetters(),
            // Literal rather than the split type name: "Synchronisation Rule" is always written in full in
            // user-visible text, never the SyncRule code identifier's shorthand.
            DeleteSyncRuleWorkerTask => "Delete Synchronisation Rule Worker Task",
            _ => "Unknown Worker Task Type"
        };
    }

    private async Task UpdateWorkerTasksAsProcessingAsync(List<WorkerTask> workerTasks)
    {
        // this is 100% sub-optimal, but I had issues with EF thinking an Activity on the workerTasks came from another db context, when it hadn't.
        foreach (var workerTask in workerTasks)
        {
            workerTask.Status = WorkerTaskStatus.Processing;
            workerTask.LastHeartbeat = DateTime.UtcNow;
            var dbWorkerTask = await Repository.Database.WorkerTasks.AsTracking().SingleOrDefaultAsync(q => q.Id == workerTask.Id);
            if (dbWorkerTask == null)
                continue;

            dbWorkerTask.Status = WorkerTaskStatus.Processing;
            dbWorkerTask.LastHeartbeat = DateTime.UtcNow;
            Repository.Database.WorkerTasks.Update(dbWorkerTask);
        }

        await Repository.Database.SaveChangesAsync();
    }
    #endregion

    // -----------------------------------------------------------------------------------------------------------------
    // Crash Recovery
    // -----------------------------------------------------------------------------------------------------------------

    public async Task UpdateWorkerTaskHeartbeatsAsync(Guid[] workerTaskIds)
    {
        if (workerTaskIds.Length == 0)
            return;

        var now = DateTime.UtcNow;

        // Use ExecuteUpdateAsync to run a direct SQL UPDATE, bypassing the change tracker entirely.
        // This avoids DbUpdateConcurrencyException when a task completes (and is deleted from the
        // database) on its own DbContext between when the main loop reads CurrentTasks and when
        // SaveChangesAsync would execute. Tasks that no longer exist are simply not matched by
        // the WHERE clause - the UPDATE affects 0 rows for those IDs, which is not an error.
        await Repository.Database.WorkerTasks
            .Where(q => workerTaskIds.Contains(q.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastHeartbeat, now));
    }

    public async Task<List<WorkerTask>> GetStaleProcessingWorkerTasksAsync(TimeSpan staleThreshold)
    {
        var cutoff = DateTime.UtcNow - staleThreshold;
        return await Repository.Database.WorkerTasks
            .Include(st => st.Activity)
            .Where(st => st.Status == WorkerTaskStatus.Processing &&
                         (st.LastHeartbeat == null || st.LastHeartbeat < cutoff))
            .ToListAsync();
    }
}
