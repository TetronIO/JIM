using JIM.Models.Tasking;

namespace JIM.Data.Repositories
{
    public interface ITaskingRepository
    {
        public Task CreateServiceTaskAsync(ServiceTask serviceTask);

        public Task<ServiceTask?> GetNextServiceTaskAsync();

        public Task<DataGenerationTemplateServiceTask?> GetFirstDataGenerationServiceTaskAsync(int dataGenerationTemplateId);

        public Task UpdateServiceTaskAsync(ServiceTask serviceTask);

        public Task DeleteServiceTaskAsync(ServiceTask serviceTask);
    }
}
