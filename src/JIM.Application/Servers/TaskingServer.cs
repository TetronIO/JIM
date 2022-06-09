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

        public async Task CreateServiceTaskAsync(ServiceTask serviceTask)
        {
            await Application.Repository.Tasking.CreateServiceTaskAsync(serviceTask);
        }

        public async Task<ServiceTask?> GetNextServiceTaskAsync()
        {
            return await Application.Repository.Tasking.GetNextServiceTaskAsync();
        }

        public async Task DeleteServiceTaskAsync(ServiceTask serviceTask)
        {
            await Application.Repository.Tasking.DeleteServiceTaskAsync(serviceTask);
        }
    }
}
