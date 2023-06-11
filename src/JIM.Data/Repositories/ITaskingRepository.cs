using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;

namespace JIM.Data.Repositories
{
    public interface ITaskingRepository
    {
        public Task CreateServiceTaskAsync(ServiceTask serviceTask);

        public Task<List<ServiceTask>> GetServiceTasksAsync();

        public Task<List<ServiceTaskHeader>> GetServiceTaskHeadersAsync();

        public Task<ServiceTask?> GetNextServiceTaskAsync();

        public Task<DataGenerationTemplateServiceTask?> GetFirstDataGenerationServiceTaskAsync(int dataGenerationTemplateId);

        /// <summary>
        /// Get all service tasks that need cancelling.
        /// </summary>
        public Task<List<ServiceTask>> GetServiceTasksThatNeedCancellingAsync();

        /// <summary>
        /// Get selective service tasks that need cancelling.
        /// </summary>
        public Task<List<ServiceTask>> GetServiceTasksThatNeedCancellingAsync(Guid[] serviceTaskIds);

        public Task<List<ServiceTask>> GetNextServiceTasksToProcessAsync();

        public Task<ServiceTaskStatus?> GetFirstDataGenerationTemplateServiceTaskStatus(int templateId);

        public Task UpdateServiceTaskAsync(ServiceTask serviceTask);

        public Task CancelServiceTaskAsync(Guid serviceTaskId);

        public Task DeleteServiceTaskAsync(ServiceTask serviceTask);
    }
}
