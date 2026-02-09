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

    // -----------------------------------------------------------------------------------------------------------------
    // Scheduler Service Queries
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Gets all worker tasks associated with a schedule execution.
    /// Used by the scheduler to monitor step completion.
    /// </summary>
    public Task<List<WorkerTask>> GetWorkerTasksByScheduleExecutionAsync(Guid scheduleExecutionId);

    /// <summary>
    /// Gets all worker tasks for a specific step within a schedule execution.
    /// A step may have multiple tasks if it runs multiple run profiles in parallel.
    /// </summary>
    public Task<List<WorkerTask>> GetWorkerTasksByScheduleExecutionStepAsync(Guid scheduleExecutionId, int stepIndex);
}