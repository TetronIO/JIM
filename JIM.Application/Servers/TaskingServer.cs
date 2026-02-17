using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;
using Serilog;

namespace JIM.Application.Servers
{
    public class TaskingServer
    {
        private JimApplication Application { get; }

        internal TaskingServer(JimApplication application)
        {
            Application = application;
        }

        public async Task<WorkerTask?> GetWorkerTaskAsync(Guid id)
        {
            return await Application.Repository.Tasking.GetWorkerTaskAsync(id);
        }

        public async Task<List<WorkerTask>> GetWorkerTasksAsync()
        {
            return await Application.Repository.Tasking.GetWorkerTasksAsync();
        }

        /// <summary>
        /// Retrieves a list of the current tasks, with any inherited task information formatted into the name,
        /// i.e. connected system name and connected system run profile name for a SynchronisationWorkerTask.
        /// </summary>
        public async Task<List<WorkerTaskHeader>> GetWorkerTaskHeadersAsync()
        {
            return await Application.Repository.Tasking.GetWorkerTaskHeadersAsync();
        }

        /// <summary>
        /// Creates an activity from a worker task, using the initiator triad from the task.
        /// Also copies schedule execution context if the task is part of a scheduled execution.
        /// </summary>
        private async Task CreateActivityFromWorkerTaskAsync(Activity activity, WorkerTask workerTask)
        {
            // Copy schedule execution context so that Activities survive worker task deletion
            // and the scheduler can query step outcomes directly from Activities.
            if (workerTask.ScheduleExecutionId.HasValue)
            {
                activity.ScheduleExecutionId = workerTask.ScheduleExecutionId;
                activity.ScheduleStepIndex = workerTask.ScheduleStepIndex;
            }

            await Application.Activities.CreateActivityWithTriadAsync(
                activity,
                workerTask.InitiatedByType,
                workerTask.InitiatedById,
                workerTask.InitiatedByName);
        }

        public async Task<WorkerTaskCreationResult> CreateWorkerTaskAsync(WorkerTask workerTask)
        {
            string? partitionWarning = null;

            if (workerTask is SynchronisationWorkerTask synchronisationWorkerTask)
            {
                // Validate partition selections for connectors that support partitions
                var validationResult = await ValidatePartitionSelectionsAsync(synchronisationWorkerTask.ConnectedSystemId);
                if (validationResult.HasError)
                {
                    return WorkerTaskCreationResult.Failed(validationResult.ErrorMessage!);
                }
                partitionWarning = validationResult.WarningMessage;

                // every CRUD operation requires tracking with an activity...
                var connectedSystem = await Application.ConnectedSystems.GetConnectedSystemAsync(synchronisationWorkerTask.ConnectedSystemId);
                var runProfiles = await Application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(synchronisationWorkerTask.ConnectedSystemId);
                var runProfile = runProfiles.Single(rp => rp.Id == synchronisationWorkerTask.ConnectedSystemRunProfileId);
                var activity = new Activity
                {
                    TargetName = runProfile.Name,
                    TargetContext = connectedSystem?.Name,
                    TargetType = ActivityTargetType.ConnectedSystemRunProfile,
                    TargetOperationType = ActivityTargetOperationType.Execute,
                    ConnectedSystemId = synchronisationWorkerTask.ConnectedSystemId,
                    ConnectedSystemRunProfileId = runProfile.Id,
                    ConnectedSystemRunType = runProfile.RunType
                };
                await CreateActivityFromWorkerTaskAsync(activity, workerTask);

                // associate the activity with the worker task so the worker task processor can complete the activity when done.
                workerTask.Activity = activity;
            }
            else if (workerTask is DataGenerationTemplateWorkerTask dataGenerationWorkerTask)

            {
                var template = await Application.DataGeneration.GetTemplateAsync(dataGenerationWorkerTask.TemplateId) ??
                    throw new InvalidDataException("CreateWorkerTaskAsync: template not found for id " + dataGenerationWorkerTask.TemplateId);

                // every data generation operation requires tracking with an activity...
                var activity = new Activity
                {
                    TargetName = template.Name,
                    TargetType = ActivityTargetType.DataGenerationTemplate,
                    TargetOperationType = ActivityTargetOperationType.Execute,
                    DataGenerationTemplateId = template.Id
                };
                await CreateActivityFromWorkerTaskAsync(activity, workerTask);

                // associate the activity with the worker task so the worker task processor can complete the activity when done.
                workerTask.Activity = activity;
            }
            else if (workerTask is ClearConnectedSystemObjectsWorkerTask clearConnectedSystemObjectsTask)
            {
                // every crud operation requires tracking with an activity...
                var connectedSystem = await Application.ConnectedSystems.GetConnectedSystemAsync(clearConnectedSystemObjectsTask.ConnectedSystemId);
                var activity = new Activity
                {
                    TargetName = connectedSystem?.Name,
                    TargetType = ActivityTargetType.ConnectedSystem,
                    TargetOperationType = ActivityTargetOperationType.Clear,
                    ConnectedSystemId = clearConnectedSystemObjectsTask.ConnectedSystemId,
                };
                await CreateActivityFromWorkerTaskAsync(activity, workerTask);

                // associate the activity with the worker task so the worker task processor can complete the activity when done.
                workerTask.Activity = activity;
            }
            else if (workerTask is DeleteConnectedSystemWorkerTask deleteConnectedSystemTask)
            {
                // Connected System deletion requires tracking with an activity for audit purposes.
                // The TargetName must be populated since the Connected System will be deleted.
                var connectedSystem = await Application.ConnectedSystems.GetConnectedSystemAsync(deleteConnectedSystemTask.ConnectedSystemId);
                var activity = new Activity
                {
                    TargetName = connectedSystem?.Name ?? $"Connected System {deleteConnectedSystemTask.ConnectedSystemId}",
                    TargetType = ActivityTargetType.ConnectedSystem,
                    TargetOperationType = ActivityTargetOperationType.Delete,
                    ConnectedSystemId = deleteConnectedSystemTask.ConnectedSystemId,
                };
                await CreateActivityFromWorkerTaskAsync(activity, workerTask);

                // associate the activity with the worker task so the worker task processor can complete the activity when done.
                workerTask.Activity = activity;
            }

            await Application.Repository.Tasking.CreateWorkerTaskAsync(workerTask);

            // Return result with any warnings
            if (!string.IsNullOrEmpty(partitionWarning))
            {
                return WorkerTaskCreationResult.SucceededWithWarnings(workerTask.Id, partitionWarning);
            }
            return WorkerTaskCreationResult.Succeeded(workerTask.Id);
        }

        /// <summary>
        /// Validates that a Connected System has the required partition/container selections.
        /// </summary>
        private async Task<PartitionValidationResult> ValidatePartitionSelectionsAsync(int connectedSystemId)
        {
            var connectedSystem = await Application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
            if (connectedSystem == null)
            {
                return PartitionValidationResult.Error("Connected System not found.");
            }

            // If the connector doesn't support partitions, or partitions are properly selected, no validation needed
            if (connectedSystem.HasPartitionsOrContainersSelected())
            {
                return PartitionValidationResult.Valid();
            }

            // Connector supports partitions but none are selected - check the validation mode setting
            var validationMode = await Application.ServiceSettings.GetPartitionValidationModeAsync();
            var message = $"Connected System '{connectedSystem.Name}' supports partitions but no partitions or containers have been selected. " +
                          "Import operations will return no objects. Please configure partition and container selections on the Connected System's Partitions & Containers tab.";

            if (validationMode == PartitionValidationMode.Error)
            {
                Log.Warning("CreateWorkerTaskAsync: Blocking execution - {Message}", message);
                return PartitionValidationResult.Error(message);
            }

            // Warning mode - allow execution but return warning
            Log.Warning("CreateWorkerTaskAsync: Proceeding with warning - {Message}", message);
            return PartitionValidationResult.Warning(message);
        }

        /// <summary>
        /// Result of partition validation check.
        /// </summary>
        private class PartitionValidationResult
        {
            public bool HasError { get; private init; }
            public string? ErrorMessage { get; private init; }
            public string? WarningMessage { get; private init; }

            public static PartitionValidationResult Valid() => new();
            public static PartitionValidationResult Error(string message) => new() { HasError = true, ErrorMessage = message };
            public static PartitionValidationResult Warning(string message) => new() { WarningMessage = message };
        }

        public async Task<WorkerTask?> GetNextWorkerTaskAsync()
        {
            var task = await Application.Repository.Tasking.GetNextWorkerTaskAsync();
            if (task == null)
                return null;

            // we need to mark the task as being processed, so it's not picked up again by any other queue clients
            task.Status = WorkerTaskStatus.Processing;
            await Application.Repository.Tasking.UpdateWorkerTaskAsync(task);
            return task;
        }

        public async Task<List<WorkerTask>> GetNextWorkerTasksToProcessAsync()
        {
            return await Application.Repository.Tasking.GetNextWorkerTasksToProcessAsync();
        }

        public async Task<List<WorkerTask>> GetWorkerTasksThatNeedCancellingAsync()
        {
            return await Application.Repository.Tasking.GetWorkerTasksThatNeedCancellingAsync();
        }

        public async Task<List<WorkerTask>> GetWorkerTasksThatNeedCancellingAsync(Guid[] workerTaskIds)
        {
            return await Application.Repository.Tasking.GetWorkerTasksThatNeedCancellingAsync(workerTaskIds);
        }

        public async Task UpdateWorkerTaskAsync(WorkerTask workerTask)
        {
            await Application.Repository.Tasking.UpdateWorkerTaskAsync(workerTask);
        }

        public async Task CancelWorkerTaskAsync(Guid workerTaskId)
        {
            var workerTask = await GetWorkerTaskAsync(workerTaskId);
            if (workerTask == null)
            {
                Log.Warning($"CancelWorkerTaskAsync: no activity for id {workerTaskId} exists. Aborting.");
                return;
            }

            if (workerTask.Activity != null)
                await Application.Activities.CancelActivityAsync(workerTask.Activity);

            await Application.Repository.Tasking.DeleteWorkerTaskAsync(workerTask);
        }

        public async Task CancelWorkerTaskAsync(WorkerTask workerTask)
        {
            await CancelWorkerTaskAsync(workerTask.Id);
        }

        public async Task CompleteWorkerTaskAsync(WorkerTask workerTask)
        {
            if (workerTask.Activity is { Status: ActivityStatus.InProgress })
                await Application.Activities.CompleteActivityAsync(workerTask.Activity);

            await Application.Repository.Tasking.DeleteWorkerTaskAsync(workerTask);
        }

        #region Data Generation Tasks
        public async Task<DataGenerationTemplateWorkerTask?> GetFirstDataGenerationTemplateWorkerTaskAsync(int templateId)
        {
            return await Application.Repository.Tasking.GetFirstDataGenerationWorkerTaskAsync(templateId);
        }

        public async Task<WorkerTaskStatus?> GetFirstDataGenerationTemplateWorkerTaskStatus(int templateId)
        {
            return await Application.Repository.Tasking.GetFirstDataGenerationTemplateWorkerTaskStatus(templateId);
        }
        #endregion

        #region Crash Recovery

        /// <summary>
        /// Updates the LastHeartbeat timestamp for all specified worker tasks.
        /// Called by the worker main loop to signal liveness.
        /// </summary>
        public async Task UpdateWorkerTaskHeartbeatsAsync(Guid[] workerTaskIds)
        {
            await Application.Repository.Tasking.UpdateWorkerTaskHeartbeatsAsync(workerTaskIds);
        }

        /// <summary>
        /// Recovers worker tasks that are stuck in Processing status due to a worker crash or restart.
        /// Fails the associated activities with a crash-recovery error message and deletes the worker tasks.
        /// Returns the number of tasks recovered.
        /// </summary>
        public async Task<int> RecoverStaleWorkerTasksAsync(TimeSpan staleThreshold)
        {
            var staleTasks = await Application.Repository.Tasking.GetStaleProcessingWorkerTasksAsync(staleThreshold);
            if (staleTasks.Count == 0)
                return 0;

            foreach (var staleTask in staleTasks)
            {
                Log.Warning("RecoverStaleWorkerTasksAsync: Recovering stale worker task {TaskId} (last heartbeat: {LastHeartbeat})",
                    staleTask.Id, staleTask.LastHeartbeat?.ToString("o") ?? "never");

                // Fail the associated activity so it appears correctly in history
                if (staleTask.Activity is { Status: ActivityStatus.InProgress })
                {
                    try
                    {
                        await Application.Activities.FailActivityWithErrorAsync(
                            staleTask.Activity,
                            "Task was abandoned due to worker crash or restart. The task was in progress when the worker stopped responding. " +
                            "This does not indicate a data integrity issue - the next sync run will process from the current state.");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "RecoverStaleWorkerTasksAsync: Failed to update activity {ActivityId} for stale task {TaskId}",
                            staleTask.Activity.Id, staleTask.Id);
                    }
                }

                // Delete the worker task to free up the queue
                await Application.Repository.Tasking.DeleteWorkerTaskAsync(staleTask);
            }

            return staleTasks.Count;
        }

        #endregion
    }
}
