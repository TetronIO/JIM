using JIM.Data.Repositories;
using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;
using JIM.Utilities;
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
            if (serviceTask is DataGenerationTemplateServiceTask dataGenerationTemplateServiceTask)
            {
                Repository.Database.DataGenerationTemplateServiceTasks.Add(dataGenerationTemplateServiceTask);
                await Repository.Database.SaveChangesAsync();
            }
            else if (serviceTask is SynchronisationServiceTask synchronisationServiceTask)
            {
                Repository.Database.SynchronisationServiceTasks.Add(synchronisationServiceTask);
                await Repository.Database.SaveChangesAsync();
            }
            else
            {
                throw new ArgumentException("serviceTask was of an unexpected type: " + serviceTask.GetType());
            }
        }

        public async Task<List<ServiceTask>> GetServiceTasksAsync()
        {
            return await Repository.Database.ServiceTasks.ToListAsync();
        }

        public async Task<List<ServiceTaskHeader>> GetServiceTaskHeadersAsync()
        {
            var serviceTaskHeaders = new List<ServiceTaskHeader>();
            foreach (var serviceTask in Repository.Database.ServiceTasks.OrderByDescending(q => q.Timestamp))
            {
                serviceTaskHeaders.Add(new ServiceTaskHeader
                {
                    Id = serviceTask.Id,
                    Status = serviceTask.Status,
                    Timestamp = serviceTask.Timestamp,
                    Name = await GetServiceTaskHeaderNameAync(serviceTask),
                    Type = GetServiceTaskType(serviceTask)
                });
            }
            return serviceTaskHeaders;
        }

        public async Task<ServiceTask?> GetNextServiceTaskAsync()
        {
            return await Repository.Database.ServiceTasks.
                Where(q => q.Status == ServiceTaskStatus.Queued).
                OrderBy(q => q.Timestamp).
                FirstOrDefaultAsync();
        }

        public async Task<List<ServiceTask>> GetNextServiceTasksToProcessAsync()
        {
            var tasks = new List<ServiceTask>();
            foreach (var task in await Repository.Database.ServiceTasks.Where(q => q.Status == ServiceTaskStatus.Queued).OrderBy(q => q.Timestamp).ToListAsync())
            {
                if (task.ExecutionMode == ServiceTaskExecutionMode.Sequential)
                {
                    tasks.Add(task);
                    break;
                }
                else if (task.ExecutionMode == ServiceTaskExecutionMode.Parallel)
                {
                    tasks.Add(task);
                }
                else
                {
                    break;
                }
            }

            return tasks;
        }

        public async Task<List<ServiceTask>> GetServiceTasksThatNeedCancellingAsync()
        {
            return await Repository.Database.ServiceTasks.Where(q => q.Status == ServiceTaskStatus.CancellationRequested).ToListAsync();
        }

        public async Task<List<ServiceTask>> GetServiceTasksThatNeedCancellingAsync(Guid[] serviceTaskIds)
        {
            return await Repository.Database.ServiceTasks.Where(q => serviceTaskIds.Contains(q.Id) && q.Status == ServiceTaskStatus.CancellationRequested).ToListAsync();
        }

        public async Task<DataGenerationTemplateServiceTask?> GetFirstDataGenerationServiceTaskAsync(int dataGenerationTemplateId)
        {
            return await Repository.Database.DataGenerationTemplateServiceTasks.OrderBy(q => q.Timestamp).FirstOrDefaultAsync(q => q.TemplateId == dataGenerationTemplateId);
        }

        public async Task<ServiceTaskStatus?> GetFirstDataGenerationTemplateServiceTaskStatus(int templateId)
        {
            var result = await Repository.Database.DataGenerationTemplateServiceTasks.Where(q => q.TemplateId == templateId).Select(q => q.Status).Take(1).ToListAsync();
            if (result != null && result.Count == 1)
                return result[0];

            return null;
        }

        public async Task UpdateServiceTaskAsync(ServiceTask serviceTask)
        {
            if (serviceTask is DataGenerationTemplateServiceTask dataGenerationTemplateServiceTask)
            {
                var dbDataGenerationTemplateServiceTask = await Repository.Database.DataGenerationTemplateServiceTasks.SingleOrDefaultAsync(q => q.Id == serviceTask.Id);
                if (dbDataGenerationTemplateServiceTask == null)
                {
                    Log.Error("UpdateServiceTaskAsync: Could not retrieve a DataGenerationTemplateServiceTask object to update.");
                    return;
                }

                // map scalar value updates to the db version of the object
                Repository.Database.Entry(dbDataGenerationTemplateServiceTask).CurrentValues.SetValues(dataGenerationTemplateServiceTask);
            }
            else if (serviceTask is SynchronisationServiceTask synchronisationServiceTask)
            {
                var dbSynchronisationServiceTask = await Repository.Database.SynchronisationServiceTasks.SingleOrDefaultAsync(q => q.Id == serviceTask.Id);
                if (dbSynchronisationServiceTask == null)
                {
                    Log.Error("UpdateServiceTaskAsync: Could not retrieve a SynchronisationServiceTask object to update.");
                    return;
                }

                // map scalar value updates to the db version of the object
                Repository.Database.Entry(dbSynchronisationServiceTask).CurrentValues.SetValues(synchronisationServiceTask);
            }

            await Repository.Database.SaveChangesAsync();
        }

        public async Task CancelServiceTaskAsync(Guid serviceTaskId)
        {
            var serviceTask = Repository.Database.ServiceTasks.SingleOrDefault(q => q.Id == serviceTaskId);
            if (serviceTask == null)
                return;

            serviceTask.Status = ServiceTaskStatus.CancellationRequested;
            await Repository.Database.SaveChangesAsync();
        }

        public async Task DeleteServiceTaskAsync(ServiceTask serviceTask)
        {
            if (await Repository.Database.ServiceTasks.AnyAsync(q => q.Id == serviceTask.Id))
            {
                Repository.Database.ServiceTasks.Remove(serviceTask);
                await Repository.Database.SaveChangesAsync();
            }
            else
            {
                Log.Debug($"DeleteServiceTaskAsync: Did not delete service task {serviceTask.Id} as it doesn't exist (already deleted?)");
            }
        }

        #region private methods
        private async Task<string> GetServiceTaskHeaderNameAync(ServiceTask serviceTask)
        {
            using var db = new JimDbContext();
            if (serviceTask is DataGenerationTemplateServiceTask dataGenerationTemplateServiceTask)
            {
                var templatePart = await db.DataGenerationTemplates.Select(q => new { q.Id, q.Name }).SingleOrDefaultAsync(q => q.Id == dataGenerationTemplateServiceTask.TemplateId);
                if (templatePart != null)
                    return templatePart.Name;
                else
                    return "template not found!";
            }
            else if (serviceTask is SynchronisationServiceTask synchronisationServiceTask)
            {
                var runProfilePart = await db.ConnectedSystemRunProfiles.Select(q => new { q.Id, q.Name, ConnectedSystemName = q.ConnectedSystem.Name }).
                    SingleOrDefaultAsync(q => q.Id == synchronisationServiceTask.ConnectedSystemRunProfileId);

                if (runProfilePart != null)
                    return $"{runProfilePart.ConnectedSystemName} - {runProfilePart.Name}";
                else
                    return "run profile not found!";
            }
            else
            {
                return "Unknown ServiceTask type";
            }
        }

        private string GetServiceTaskType(ServiceTask serviceTask)
        {
            if (serviceTask is DataGenerationTemplateServiceTask)
                return nameof(DataGenerationTemplateServiceTask).SplitOnCapitalLetters();
            else if (serviceTask is SynchronisationServiceTask)
                return nameof(SynchronisationServiceTask).SplitOnCapitalLetters();
            else
                return "Unknown Service Task Type";
        }
        #endregion
    }
}
