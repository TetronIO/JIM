using JIM.Models.Activities;
using JIM.Models.Core;
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
        /// Creates an activity from a worker task, using the correct initiator type.
        /// </summary>
        private async Task CreateActivityFromWorkerTaskAsync(Activity activity, WorkerTask workerTask)
        {
            if (workerTask.InitiatedByType == ActivityInitiatorType.ApiKey && workerTask.InitiatedByApiKey != null)
            {
                await Application.Activities.CreateActivityAsync(activity, workerTask.InitiatedByApiKey);
            }
            else
            {
                await Application.Activities.CreateActivityAsync(activity, workerTask.InitiatedByMetaverseObject);
            }
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
                var runProfiles = await Application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(synchronisationWorkerTask.ConnectedSystemId);
                var runProfile = runProfiles.Single(rp => rp.Id == synchronisationWorkerTask.ConnectedSystemRunProfileId);
                var activity = new Activity
                {
                    TargetName = runProfile.Name,
                    TargetType = ActivityTargetType.ConnectedSystemRunProfile,
                    TargetOperationType = ActivityTargetOperationType.Execute,
                    ConnectedSystemId = synchronisationWorkerTask.ConnectedSystemId,
                    ConnectedSystemRunProfileId = runProfile.Id
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
    }
}
