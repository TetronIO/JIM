using JIM.Models.Core;

namespace JIM.Models.Tasking
{
	public abstract class ServiceTask
	{
		public Guid Id { get; set; }

		/// <summary>
		/// Typically the value for the timestamp will be when the task was created, though the value can
		/// be changed to change the order in which the tasks will be processed in relation to others, i.e. it controls ordering.
		/// </summary>
		public DateTime Timestamp { get; set; }

		public ServiceTaskStatus Status { get; set; }

		public ServiceTaskExecutionMode ExecutionMode { get; set; }

		/// <summary>
		/// If this task was initiated by a user, reference them here.
		/// </summary>
		public MetaverseObject? InitiatedBy { get; set; }

		/// <summary>
		/// If this task was initiated by a user, store their name here in case the MetaverseObject is deleted and we need to retain some kind of history.
		/// </summary>
		public string? InitiatedByName { get; set; }

        public ServiceTask()
        {
			Timestamp = DateTime.Now;
			Status = ServiceTaskStatus.Queued;
			ExecutionMode = ServiceTaskExecutionMode.Sequential;
        }
	}
}
