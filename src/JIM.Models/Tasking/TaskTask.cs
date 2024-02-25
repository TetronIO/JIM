namespace JIM.Models.Tasking
{
    public class TaskTask
    {
        /// <summary>
        /// The identifier for the WorkerTask being processed.
        /// </summary>
        public Guid TaskId { get; set; }

        /// <summary>
        /// The Task where the ServiceTask is being executed within.
        /// </summary>
        public Task Task { get; set; }

        /// <summary>
        /// The cancellation token source that will cancel the task.
        /// </summary>
        public CancellationTokenSource CancellationTokenSource { get; set; }

        public TaskTask(Guid taskId, Task task, CancellationTokenSource cancellationTokenSource)
        {
            TaskId = taskId;
            Task = task;
            CancellationTokenSource = cancellationTokenSource;
        }
    }
}
