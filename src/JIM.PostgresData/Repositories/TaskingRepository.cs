using JIM.Data.Repositories;
using JIM.Models.Tasking;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace JIM.PostgresData.Repositories
{
    public class TaskingRepository : ITaskingRepository
    {
        private PostgresDataRepository Repository { get; }

        internal TaskingRepository(PostgresDataRepository dataRepository)
        {
            Repository = dataRepository;
        }

        public async Task CreateServiceTaskAsync(ServiceTask serviceTask)
        {
            await Repository.Database.ServiceTasks.AddAsync(serviceTask);
        }

        public async Task<ServiceTask?> GetNextServiceTaskAsync()
        {
            return await Repository.Database.ServiceTasks.OrderByDescending(q => q.Timestamp).FirstOrDefaultAsync();
        }

        public async Task UpdateServiceTaskAsync(ServiceTask serviceTask)
        {
            var dbServiceTask = await Repository.Database.ServiceTasks.SingleOrDefaultAsync(q => q.Id == serviceTask.Id);
            if (dbServiceTask == null)
            {
                Log.Error("UpdateServiceTaskAsync: Could not retrieve a ServiceTask object to update.");
                return;
            }

            // map scalar value updates to the db version of the object
            Repository.Database.Entry(dbServiceTask).CurrentValues.SetValues(serviceTask);

            // manually update reference properties
            await Repository.Database.SaveChangesAsync();
        }

        public async Task DeleteServiceTaskAsync(ServiceTask serviceTask)
        {
            Repository.Database.ServiceTasks.Remove(serviceTask);
            await Repository.Database.SaveChangesAsync();
        }
    }
}
