using JIM.Models.Tasking;

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

        public async Task<DataGenerationTemplateServiceTask?> GetFirstDataGenerationTemplateServiceTaskAsync(int templateId)
        {
            return await Application.Repository.Tasking.GetFirstDataGenerationServiceTaskAsync(templateId);
        }

        public async Task<ServiceTaskStatus?> GetFirstDataGenerationTemplateServiceTaskStatus(int templateId)
        {
            return await Application.Repository.Tasking.GetFirstDataGenerationTemplateServiceTaskStatus(templateId);
        }

        public async Task DeleteServiceTaskAsync(ServiceTask serviceTask)
        {
            await Application.Repository.Tasking.DeleteServiceTaskAsync(serviceTask);
        }
    }
}
