using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;

namespace JIM.Data.Repositories
{
    public interface ITaskingRepository
    {
        public Task<List<ServiceTask>> GetServiceTasksAsync();

        public Task<List<ServiceTaskHeader>> GetServiceTaskHeadersAsync();

        public Task CreateServiceTaskAsync(ServiceTask serviceTask);

        public Task<ServiceTask?> GetNextServiceTaskAsync();

        public Task<DataGenerationTemplateServiceTask?> GetFirstDataGenerationServiceTaskAsync(int dataGenerationTemplateId);

        public Task<ServiceTaskStatus?> GetFirstDataGenerationTemplateServiceTaskStatus(int templateId);

        public Task UpdateServiceTaskAsync(ServiceTask serviceTask);

        public Task DeleteServiceTaskAsync(ServiceTask serviceTask);
    }
}
