// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;
namespace JIM.Data.Repositories;

public interface ITaskingRepository
{
    public Task CreateWorkerTaskAsync(WorkerTask serviceTask);

    public Task<WorkerTask?> GetWorkerTaskAsync(Guid id);

    /// <summary>
    /// The worker task tracking a given Activity, or null when none is queued or processing for it. A worker task
    /// is deleted on completion, so a null answer means "no longer running", not "never ran".
    /// </summary>
    public Task<WorkerTask?> GetWorkerTaskByActivityIdAsync(Guid activityId);

    public Task<List<WorkerTask>> GetWorkerTasksAsync();

    public Task<List<WorkerTaskHeader>> GetWorkerTaskHeadersAsync();

    public Task<WorkerTask?> GetNextWorkerTaskAsync();

    public Task<ExampleDataTemplateWorkerTask?> GetFirstExampleDataWorkerTaskAsync(int dataGenerationTemplateId);

    /// <summary>
    /// Whether a Password Delivery Worker Task that would cover the given scope is already waiting to run (#1119).
    /// <para>
    /// Only tasks still queued count. A pass already running may have read the queue before the work being
    /// requested reached it, so relying on it would leave that work waiting for the next housekeeping tick; a
    /// duplicated pass costs a query against an empty queue, which is the cheaper mistake by far.
    /// </para>
    /// <para>
    /// A queued pass over every Connected System covers a request for any one of them. The reverse is not true: a
    /// pass aimed at one system will not deliver to the others.
    /// </para>
    /// </summary>
    /// <param name="connectedSystemId">The Connected System delivery is wanted for, or null for every system.</param>
    public Task<bool> HasQueuedPasswordDeliveryTaskAsync(int? connectedSystemId);

    /// <summary>
    /// Get all worker tasks that need cancelling.
    /// </summary>
    public Task<List<WorkerTask>> GetWorkerTasksThatNeedCancellingAsync();

    /// <summary>
    /// Get selective worker tasks that need cancelling.
    /// </summary>
    public Task<List<WorkerTask>> GetWorkerTasksThatNeedCancellingAsync(Guid[] serviceTaskIds);

    public Task<List<WorkerTask>> GetNextWorkerTasksToProcessAsync();

    /// <summary>
    /// Gets a lightweight header (status plus live progress from the associated Activity) for the first
    /// Worker Task belonging to the given Example Data Template, or null if none is queued or processing.
    /// Lets the template page show progress in place, without loading the whole task or Activity graph.
    /// </summary>
    public Task<WorkerTaskHeader?> GetFirstExampleDataTemplateWorkerTaskHeaderAsync(int templateId);

    public Task UpdateWorkerTaskAsync(WorkerTask serviceTask);

    public Task DeleteWorkerTaskAsync(WorkerTask serviceTask);

    // -----------------------------------------------------------------------------------------------------------------
    // Crash Recovery
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Updates the LastHeartbeat timestamp for all specified worker tasks to DateTime.UtcNow.
    /// Called by the worker main loop to signal liveness for tasks being processed.
    /// </summary>
    public Task UpdateWorkerTaskHeartbeatsAsync(Guid[] workerTaskIds);

    /// <summary>
    /// Gets all worker tasks in Processing status whose LastHeartbeat is older than the specified threshold,
    /// or whose LastHeartbeat is null (pre-heartbeat tasks). Used for crash recovery.
    /// </summary>
    public Task<List<WorkerTask>> GetStaleProcessingWorkerTasksAsync(TimeSpan staleThreshold);

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
    /// A step may have multiple tasks if it runs multiple Run Profiles in parallel.
    /// </summary>
    public Task<List<WorkerTask>> GetWorkerTasksByScheduleExecutionStepAsync(Guid scheduleExecutionId, int stepIndex);

    // -----------------------------------------------------------------------------------------------------------------
    // Schedule Step Advancement (Worker-driven)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Counts remaining worker tasks at a specific step within a schedule execution.
    /// Used by the worker to determine if it was the last task in a step group.
    /// </summary>
    public Task<int> GetWorkerTaskCountByExecutionStepAsync(Guid scheduleExecutionId, int stepIndex);

    /// <summary>
    /// Transitions all WaitingForPreviousStep tasks at the specified step index to Queued status.
    /// Called by the worker when the previous step group completes successfully.
    /// </summary>
    public Task<int> TransitionStepToQueuedAsync(Guid scheduleExecutionId, int stepIndex);

    /// <summary>
    /// Deletes all WaitingForPreviousStep tasks for a schedule execution and fails their associated activities.
    /// Called when a step fails and ContinueOnFailure is false, or when an execution is cancelled.
    /// Returns the number of tasks deleted.
    /// </summary>
    public Task<int> DeleteWaitingTasksForExecutionAsync(Guid scheduleExecutionId);

    /// <summary>
    /// Gets the minimum ScheduleStepIndex among remaining WaitingForPreviousStep tasks
    /// for a schedule execution. Returns null if no waiting tasks remain (execution complete).
    /// </summary>
    public Task<int?> GetNextWaitingStepIndexAsync(Guid scheduleExecutionId);
}