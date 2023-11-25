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

        public async Task<ServiceTask?> GetServiceTaskAsync(Guid id)
        {
            return await Application.Repository.Tasking.GetServiceTaskAsync(id);
        }

        public async Task<List<ServiceTask>> GetServiceTasksAsync()
        {
            return await Application.Repository.Tasking.GetServiceTasksAsync();
        }

        /// <summary>
        /// Retrieves a list of the current tasks, with any inherited task information formatted into the name, 
        /// i.e. connected system name and connected system run profile name for a SynchronisationServiceTask.
        /// </summary>
        public async Task<List<ServiceTaskHeader>> GetServiceTaskHeadersAsync()
        {
            return await Application.Repository.Tasking.GetServiceTaskHeadersAsync();
        }

        public async Task CreateServiceTaskAsync(ServiceTask serviceTask)
        {
            if (serviceTask is SynchronisationServiceTask synchronisationServiceTask)
            {
                // every CRUD operation requires tracking with an activity...
                var runProfiles = await Application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(synchronisationServiceTask.ConnectedSystemId);
                var runProfile = runProfiles.Single(rp => rp.Id == synchronisationServiceTask.ConnectedSystemRunProfileId);
                var activity = new Activity
                {
                    TargetType = ActivityTargetType.ConnectedSystemRunProfile,
                    TargetOperationType = ActivityTargetOperationType.Execute,
                    ConnectedSystemId = synchronisationServiceTask.ConnectedSystemId,
                    RunProfile = runProfile              
                };
                await Application.Activities.CreateActivityAsync(activity, serviceTask.InitiatedBy);

                // associate the activity with the service task so the service task processor can complete the activity when done.
                serviceTask.Activity = activity;
            }
            else if (serviceTask is DataGenerationTemplateServiceTask dataGenerationServiceTask)
            {
                var template = await Application.DataGeneration.GetTemplateAsync(dataGenerationServiceTask.TemplateId) ?? 
                    throw new InvalidDataException("CreateServiceTaskAsync: template not found for id " + dataGenerationServiceTask.TemplateId);

                // every data generation operation requires tracking with an activity...
                var activity = new Activity
                {
                    TargetType = ActivityTargetType.DataGenerationTemplate,
                    TargetOperationType = ActivityTargetOperationType.Execute,
                    DataGenerationTemplateId = template.Id,
                    TargetName = template.Name
                };
                await Application.Activities.CreateActivityAsync(activity, serviceTask.InitiatedBy);

                // associate the activity with the service task so the service task processor can complete the activity when done.
                serviceTask.Activity = activity;
            }

            await Application.Repository.Tasking.CreateServiceTaskAsync(serviceTask);
        }

        public async Task<ServiceTask?> GetNextServiceTaskAsync()
        {
            var task = await Application.Repository.Tasking.GetNextServiceTaskAsync();
            if (task == null)
                return null;

            // we need to mark the task as being processed, so it's not picked up again by any other queue clients
            task.Status = ServiceTaskStatus.Processing;
            await Application.Repository.Tasking.UpdateServiceTaskAsync(task);
            return task;
        }

        public async Task<List<ServiceTask>> GetNextServiceTasksToProcessAsync()
        {
            var tasks = await Application.Repository.Tasking.GetNextServiceTasksToProcessAsync();
            if (tasks.Count == 0)
                return tasks;

            // we need to mark the tasks as being processed, so it's not picked up again by any other queue clients
            foreach (var task in tasks)
            {
                task.Status = ServiceTaskStatus.Processing;
                await Application.Repository.Tasking.UpdateServiceTaskAsync(task);
            }

            return tasks;
        }

        public async Task<List<ServiceTask>> GetServiceTasksThatNeedCancellingAsync()
        {
            return await Application.Repository.Tasking.GetServiceTasksThatNeedCancellingAsync();
        }

        public async Task<List<ServiceTask>> GetServiceTasksThatNeedCancellingAsync(Guid[] serviceTaskIds)
        {
            return await Application.Repository.Tasking.GetServiceTasksThatNeedCancellingAsync(serviceTaskIds);
        }

        public async Task UpdateServiceTaskAsync(ServiceTask serviceTask)
        {
            await Application.Repository.Tasking.UpdateServiceTaskAsync(serviceTask);
        }

        public async Task CancelServiceTaskAsync(Guid serviceTaskId)
        {
            var serviceTask = await GetServiceTaskAsync(serviceTaskId);
            if (serviceTask == null)
            {
                Log.Warning($"CancelServiceTaskAsync: no activity for id {serviceTaskId} exists. Aborting.");
                return;
            }

            if (serviceTask.Activity != null)
                await Application.Activities.CancelActivityAsync(serviceTask.Activity);

            await Application.Repository.Tasking.DeleteServiceTaskAsync(serviceTask);
        }

        public async Task CancelServiceTaskAsync(ServiceTask serviceTask)
        {
            await CancelServiceTaskAsync(serviceTask.Id);
        }

        public async Task CompleteServiceTaskAsync(ServiceTask serviceTask)
        {
            if (serviceTask.Activity != null)
                await Application.Activities.CompleteActivityAsync(serviceTask.Activity);

            await Application.Repository.Tasking.DeleteServiceTaskAsync(serviceTask);
        }

        #region Data Generation Tasks
        public async Task<DataGenerationTemplateServiceTask?> GetFirstDataGenerationTemplateServiceTaskAsync(int templateId)
        {
            return await Application.Repository.Tasking.GetFirstDataGenerationServiceTaskAsync(templateId);
        }

        public async Task<ServiceTaskStatus?> GetFirstDataGenerationTemplateServiceTaskStatus(int templateId)
        {
            return await Application.Repository.Tasking.GetFirstDataGenerationTemplateServiceTaskStatus(templateId);
        }
        #endregion
    }
}
