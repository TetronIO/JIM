using JIM.Models.Activities;
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

        public async Task CreateWorkerTaskAsync(WorkerTask workerTask)
        {
            if (workerTask is SynchronisationWorkerTask synchronisationWorkerTask)
            {
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
                await Application.Activities.CreateActivityAsync(activity, workerTask.InitiatedBy);

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
                await Application.Activities.CreateActivityAsync(activity, workerTask.InitiatedBy);

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
                await Application.Activities.CreateActivityAsync(activity, workerTask.InitiatedBy);

                // associate the activity with the worker task so the worker task processor can complete the activity when done.
                workerTask.Activity = activity;
            }

            await Application.Repository.Tasking.CreateWorkerTaskAsync(workerTask);
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
            if (workerTask.Activity != null && workerTask.Activity.Status == ActivityStatus.InProgress)
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
