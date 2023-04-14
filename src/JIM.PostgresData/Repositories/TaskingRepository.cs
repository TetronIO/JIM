using JIM.Data.Repositories;
using JIM.Models.Tasking;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace JIM.PostgresData.Repositories
{
    public class TaskingRepository : ITaskingRepository
    {
        internal TaskingRepository()
        {
        }

        public async Task<List<ServiceTask>> GetServiceTasksAsync()
        {
            using var db = new JimDbContext();
            return await db.ServiceTasks.ToListAsync();
        }

        public async Task CreateServiceTaskAsync(ServiceTask serviceTask)
        {
            using var db = new JimDbContext();
            if (serviceTask is DataGenerationTemplateServiceTask dataGenerationTemplateServiceTask)
            {
                db.DataGenerationTemplateServiceTasks.Add(dataGenerationTemplateServiceTask);
                await db.SaveChangesAsync();
            }
            else if (serviceTask is SynchronisationServiceTask synchronisationServiceTask)
            {
                db.SynchronisationServiceTasks.Add(synchronisationServiceTask);
                await db.SaveChangesAsync();
            }
            else
            {
                throw new ArgumentException("serviceTask was of an unexpected type: " + serviceTask.GetType());
            }
        }

        public async Task<ServiceTask?> GetNextServiceTaskAsync()
        {
            using var db = new JimDbContext();
            return await db.ServiceTasks.
                Where(q => q.Status == ServiceTaskStatus.Queued).
                OrderByDescending(q => q.Timestamp).
                FirstOrDefaultAsync();
        }

        public async Task<DataGenerationTemplateServiceTask?> GetFirstDataGenerationServiceTaskAsync(int dataGenerationTemplateId)
        {
            using var db = new JimDbContext();
            return await db.DataGenerationTemplateServiceTasks.OrderBy(q => q.Timestamp).FirstOrDefaultAsync(q => q.TemplateId == dataGenerationTemplateId);
        }

        public async Task<ServiceTaskStatus?> GetFirstDataGenerationTemplateServiceTaskStatus(int templateId)
        {
            using var db = new JimDbContext();
            var result = await db.DataGenerationTemplateServiceTasks.Where(q => q.TemplateId == templateId).Select(q => q.Status).Take(1).ToListAsync();
            if (result != null && result.Count == 1)
               return result[0];

            return null;
        }
        public async Task UpdateServiceTaskAsync(ServiceTask serviceTask)
        {
            using var db = new JimDbContext();
            if (serviceTask is DataGenerationTemplateServiceTask task)
            {
                var dbDataGenerationTemplateServiceTask = await db.DataGenerationTemplateServiceTasks.SingleOrDefaultAsync(q => q.Id == serviceTask.Id);
                if (dbDataGenerationTemplateServiceTask == null)
                {
                    Log.Error("UpdateServiceTaskAsync: Could not retrieve a DataGenerationTemplateServiceTask object to update.");
                    return;
                }

                // map scalar value updates to the db version of the object
                db.Entry(dbDataGenerationTemplateServiceTask).CurrentValues.SetValues(task);
            }

            await db.SaveChangesAsync();
        }

        public async Task DeleteServiceTaskAsync(ServiceTask serviceTask)
        {
            using var db = new JimDbContext();
            db.ServiceTasks.Remove(serviceTask);
            await db.SaveChangesAsync();
        }
    }
}
