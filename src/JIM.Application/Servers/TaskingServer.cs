using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;

namespace JIM.Application.Servers
{
    public class TaskingServer
    {
        private JimApplication Application { get; }

        internal TaskingServer(JimApplication application)
        {
            Application = application;
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

        public async Task<List<ServiceTask>> GetServiceTasksThatNeedCancellingAsync(Guid[] serviceTaskIds)
        {
            return await Application.Repository.Tasking.GetServiceTasksThatNeedCancellingAsync(serviceTaskIds);
        }

        public async Task<DataGenerationTemplateServiceTask?> GetFirstDataGenerationTemplateServiceTaskAsync(int templateId)
        {
            return await Application.Repository.Tasking.GetFirstDataGenerationServiceTaskAsync(templateId);
        }

        public async Task<ServiceTaskStatus?> GetFirstDataGenerationTemplateServiceTaskStatus(int templateId)
        {
            return await Application.Repository.Tasking.GetFirstDataGenerationTemplateServiceTaskStatus(templateId);
        }

        public async Task UpdateServiceTaskAsync(ServiceTask serviceTask)
        {
            await Application.Repository.Tasking.UpdateServiceTaskAsync(serviceTask);
        }

        public async Task CancelServiceTaskAsync(Guid serviceTaskId)
        {
            await Application.Repository.Tasking.CancelServiceTaskAsync(serviceTaskId);
        }

        public async Task DeleteServiceTaskAsync(ServiceTask serviceTask)
        {
            await Application.Repository.Tasking.DeleteServiceTaskAsync(serviceTask);
        }
    }
}
