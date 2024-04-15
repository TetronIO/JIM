using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;
namespace JIM.Data.Repositories;

public interface ITaskingRepository
{
    public Task CreateWorkerTaskAsync(WorkerTask serviceTask);

    public Task<WorkerTask?> GetWorkerTaskAsync(Guid id);

    public Task<List<WorkerTask>> GetWorkerTasksAsync();

    public Task<List<WorkerTaskHeader>> GetWorkerTaskHeadersAsync();

    public Task<WorkerTask?> GetNextWorkerTaskAsync();

    public Task<DataGenerationTemplateWorkerTask?> GetFirstDataGenerationWorkerTaskAsync(int dataGenerationTemplateId);

    /// <summary>
    /// Get all worker tasks that need cancelling.
    /// </summary>
    public Task<List<WorkerTask>> GetWorkerTasksThatNeedCancellingAsync();

    /// <summary>
    /// Get selective worker tasks that need cancelling.
    /// </summary>
    public Task<List<WorkerTask>> GetWorkerTasksThatNeedCancellingAsync(Guid[] serviceTaskIds);

    public Task<List<WorkerTask>> GetNextWorkerTasksToProcessAsync();

    public Task<WorkerTaskStatus?> GetFirstDataGenerationTemplateWorkerTaskStatus(int templateId);

    public Task UpdateWorkerTaskAsync(WorkerTask serviceTask);

    public Task DeleteWorkerTaskAsync(WorkerTask serviceTask);
}