using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;
using JIM.Utilities;
using Microsoft.EntityFrameworkCore;
using Serilog;
namespace JIM.PostgresData.Repositories;

public class TaskingRepository : ITaskingRepository
{
    private PostgresDataRepository Repository { get; }

    internal TaskingRepository(PostgresDataRepository dataRepository)
    {
        Repository = dataRepository;
    }

    public async Task CreateWorkerTaskAsync(WorkerTask workerTask)
    {
        if (workerTask.Activity == null)
            throw new InvalidDataException("CreateWorkerTaskAsync: workerTask.Activity was null. Cannot continue.");

        switch (workerTask)
        {
            case DataGenerationTemplateWorkerTask dataGenerationTemplateWorkerTask:
                Repository.Database.DataGenerationTemplateWorkerTasks.Add(dataGenerationTemplateWorkerTask);
                await Repository.Database.SaveChangesAsync();
                break;
            case SynchronisationWorkerTask synchronisationWorkerTask:
                Repository.Database.SynchronisationWorkerTasks.Add(synchronisationWorkerTask);
                await Repository.Database.SaveChangesAsync();
                break;
            case ClearConnectedSystemObjectsWorkerTask clearConnectedSystemObjectsTask:
                Repository.Database.ClearConnectedSystemObjectsTasks.Add(clearConnectedSystemObjectsTask);
                await Repository.Database.SaveChangesAsync();
                break;
            case DeleteConnectedSystemWorkerTask deleteConnectedSystemTask:
                Repository.Database.DeleteConnectedSystemWorkerTasks.Add(deleteConnectedSystemTask);
                await Repository.Database.SaveChangesAsync();
                break;
            default:
                throw new ArgumentException("workerTask was of an unexpected type: " + workerTask.GetType());
        }
    }

    public async Task<WorkerTask?> GetWorkerTaskAsync(Guid id)
    {
        return await Repository.Database.WorkerTasks.
            AsSplitQuery(). // Use split query to avoid cartesian explosion from multiple collection includes
            Include(st => st.Activity).
            Include(st => st.InitiatedBy).
            ThenInclude(ib => ib!.AttributeValues.Where(rvav => rvav.Attribute.Name == Constants.BuiltInAttributes.DisplayName)).
            ThenInclude(av => av.Attribute).
            Include(st => st.InitiatedBy).
            ThenInclude(ib => ib!.Type).
            SingleOrDefaultAsync(st => st.Id == id);
    }

    public async Task<List<WorkerTask>> GetWorkerTasksAsync()
    {
        return await Repository.Database.WorkerTasks
            .Include(st => st.Activity)
            .Include(st => st.InitiatedBy)
            .ThenInclude(ib => ib!.Type)
            .ToListAsync();
    }

    public async Task<List<WorkerTaskHeader>> GetWorkerTaskHeadersAsync()
    {
        // todo: find a way to retrieve a stub user, i.e. just MVO with id and displayname
        var workerTaskHeaders = new List<WorkerTaskHeader>();
        var workerTasks = await Repository.Database.WorkerTasks.
            AsSplitQuery(). // Use split query to avoid cartesian explosion from multiple collection includes
            Include(st => st.InitiatedBy).
            ThenInclude(ib => ib!.AttributeValues.Where(rvav => rvav.Attribute.Name == Constants.BuiltInAttributes.DisplayName)).
            ThenInclude(av => av.Attribute).
            Include(st => st.InitiatedBy).
            ThenInclude(ib => ib!.Type).
            OrderByDescending(q => q.Timestamp).ToListAsync();

        foreach (var workerTask in workerTasks)
        {
            workerTaskHeaders.Add(new WorkerTaskHeader
            {
                Id = workerTask.Id,
                Status = workerTask.Status,
                Timestamp = workerTask.Timestamp,
                Name = await GetWorkerHeaderNameAsync(workerTask),
                Type = GetWorkerTaskType(workerTask),
                InitiatedBy = workerTask.InitiatedBy
            });
        }
        return workerTaskHeaders;
    }

    public async Task<WorkerTask?> GetNextWorkerTaskAsync()
    {
        return await Repository.Database.WorkerTasks
            .Include(st => st.Activity)
            .Where(st => st.Status == WorkerTaskStatus.Queued)
            .OrderBy(st => st.Timestamp)
            .FirstOrDefaultAsync();
    }

    public async Task<List<WorkerTask>> GetNextWorkerTasksToProcessAsync()
    {
        var tasks = new List<WorkerTask>();
        foreach (var task in await Repository.Database.WorkerTasks
                     .AsSplitQuery() // Use split query to avoid cartesian explosion from multiple collection includes
                     .Include(st => st.Activity)
                     .Include(st => st.InitiatedBy)
                     .ThenInclude(ib => ib!.AttributeValues.Where(av => av.Attribute.Name == Constants.BuiltInAttributes.DisplayName))
                     .ThenInclude(av => av.Attribute)
                     .Where(st => st.Status == WorkerTaskStatus.Queued)
                     .OrderBy(st => st.Timestamp).ToListAsync())
        {
            if (task.ExecutionMode == WorkerTaskExecutionMode.Sequential)
            {
                tasks.Add(task);
                break;
            }

            if (task.ExecutionMode == WorkerTaskExecutionMode.Parallel)
                tasks.Add(task);
            else
                break;
        }

        await UpdateWorkerTasksAsProcessingAsync(tasks);
        return tasks;
    }

    public async Task<List<WorkerTask>> GetWorkerTasksThatNeedCancellingAsync()
    {
        return await Repository.Database.WorkerTasks.Include(st => st.Activity).Where(st => st.Status == WorkerTaskStatus.CancellationRequested).ToListAsync();
    }

    public async Task<List<WorkerTask>> GetWorkerTasksThatNeedCancellingAsync(Guid[] workerTaskIds)
    {
        return await Repository.Database.WorkerTasks.Include(st => st.Activity).Where(q => workerTaskIds.Contains(q.Id) && q.Status == WorkerTaskStatus.CancellationRequested).ToListAsync();
    }

    public async Task<DataGenerationTemplateWorkerTask?> GetFirstDataGenerationWorkerTaskAsync(int dataGenerationTemplateId)
    {
        return await Repository.Database.DataGenerationTemplateWorkerTasks.OrderBy(q => q.Timestamp).FirstOrDefaultAsync(q => q.TemplateId == dataGenerationTemplateId);
    }

    public async Task<WorkerTaskStatus?> GetFirstDataGenerationTemplateWorkerTaskStatus(int templateId)
    {
        await using var db = new JimDbContext();
        var result = await db.DataGenerationTemplateWorkerTasks.Where(q => q.TemplateId == templateId).Select(q => q.Status).Take(1).ToListAsync();
        if (result.Count == 1)
            return result[0];

        return null;
    }

    public async Task UpdateWorkerTaskAsync(WorkerTask workerTask)
    {
        switch (workerTask)
        {
            case DataGenerationTemplateWorkerTask dataGenerationTemplateWorkerTask:
            {
                var dbDataGenerationTemplateWorkerTask = await Repository.Database.DataGenerationTemplateWorkerTasks.Include(st => st.Activity).SingleOrDefaultAsync(q => q.Id == workerTask.Id);
                if (dbDataGenerationTemplateWorkerTask == null)
                {
                    Log.Error("UpdateWorkerTaskAsync: Could not retrieve a DataGenerationTemplateWorkerTask object to update.");
                    return;
                }

                // map scalar value updates to the db version of the object
                Repository.Database.Entry(dbDataGenerationTemplateWorkerTask).CurrentValues.SetValues(dataGenerationTemplateWorkerTask);
                break;
            }
            case SynchronisationWorkerTask synchronisationWorkerTask:
            {
                var dbSynchronisationWorkerTask = await Repository.Database.SynchronisationWorkerTasks.Include(st => st.Activity).SingleOrDefaultAsync(q => q.Id == workerTask.Id);
                if (dbSynchronisationWorkerTask == null)
                {
                    Log.Error("UpdateWorkerTaskAsync: Could not retrieve a SynchronisationWorkerTask object to update.");
                    return;
                }

                // map scalar value updates to the db version of the object
                Repository.Database.Entry(dbSynchronisationWorkerTask).CurrentValues.SetValues(synchronisationWorkerTask);
                break;
            }
        }

        await Repository.Database.SaveChangesAsync();
    }

    public async Task DeleteWorkerTaskAsync(WorkerTask workerTask)
    {
        // re-retrieve the worker task to avoid issues with EF
        var localWorkerTask = await Repository.Database.WorkerTasks.SingleOrDefaultAsync(q => q.Id == workerTask.Id);
        if (localWorkerTask != null)
        {
            Repository.Database.WorkerTasks.Remove(localWorkerTask);
            await Repository.Database.SaveChangesAsync();
        }
        else
        {
            Log.Debug($"DeleteWorkerTaskAsync: Did not delete worker task {workerTask.Id} as it doesn't exist (already deleted?)");
        }
    }

    #region private methods
    private static async Task<string> GetWorkerHeaderNameAsync(WorkerTask workerTask)
    {
        await using var db = new JimDbContext();
        switch (workerTask)
        {
            case DataGenerationTemplateWorkerTask dataGenerationTemplateWorkerTask:
            {
                var templatePart = await db.DataGenerationTemplates.Select(q => new { q.Id, q.Name }).SingleOrDefaultAsync(q => q.Id == dataGenerationTemplateWorkerTask.TemplateId);
                return templatePart != null ? templatePart.Name : "template not found!";
            }
            case SynchronisationWorkerTask synchronisationWorkerTask:
            {
                var runProfilePart = await db.ConnectedSystemRunProfiles.Select(q => new { q.Id, q.Name, ConnectedSystemName = db.ConnectedSystems.Single(cs => cs.Id == q.ConnectedSystemId).Name }).
                    SingleOrDefaultAsync(q => q.Id == synchronisationWorkerTask.ConnectedSystemRunProfileId);

                return runProfilePart != null ? $"{runProfilePart.ConnectedSystemName} - {runProfilePart.Name}" : "run profile not found!";
            }
            case ClearConnectedSystemObjectsWorkerTask clearConnectedSystemObjectsTask:
                // use the name of the connected system
                return db.ConnectedSystems.Single(q => q.Id == clearConnectedSystemObjectsTask.ConnectedSystemId).Name;
            case DeleteConnectedSystemWorkerTask deleteConnectedSystemTask:
                // use the name of the connected system (may be null if already deleted)
                var systemToDelete = db.ConnectedSystems.SingleOrDefault(q => q.Id == deleteConnectedSystemTask.ConnectedSystemId);
                return systemToDelete?.Name ?? $"Connected System {deleteConnectedSystemTask.ConnectedSystemId}";
            default:
                return "Unknown WorkerTask type";
        }
    }

    private static string GetWorkerTaskType(WorkerTask workerTask)
    {
        return workerTask switch
        {
            DataGenerationTemplateWorkerTask => nameof(DataGenerationTemplateWorkerTask).SplitOnCapitalLetters(),
            SynchronisationWorkerTask => nameof(SynchronisationWorkerTask).SplitOnCapitalLetters(),
            ClearConnectedSystemObjectsWorkerTask => nameof(ClearConnectedSystemObjectsWorkerTask).SplitOnCapitalLetters(),
            DeleteConnectedSystemWorkerTask => nameof(DeleteConnectedSystemWorkerTask).SplitOnCapitalLetters(),
            _ => "Unknown Worker Task Type"
        };
    }

    private async Task UpdateWorkerTasksAsProcessingAsync(List<WorkerTask> workerTasks)
    {
        // this is 100% sub-optimal, but I had issues with EF thinking an Activity on the workerTasks came from another db context, when it hadn't.
        foreach (var workerTask in workerTasks)
        {
            workerTask.Status = WorkerTaskStatus.Processing;
            var dbWorkerTask = await Repository.Database.WorkerTasks.SingleOrDefaultAsync(q => q.Id == workerTask.Id);
            if (dbWorkerTask == null) 
                continue;
                
            dbWorkerTask.Status = WorkerTaskStatus.Processing;
            Repository.Database.WorkerTasks.Update(dbWorkerTask);
        }

        await Repository.Database.SaveChangesAsync();
    }
    #endregion
}
