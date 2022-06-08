using static JIM.Models.Tasking.Enums;

namespace JIM.Models.Tasking
{
	public abstract class ServiceTask
	{
		public Guid Id { get; set; }
		public DateTime Timestamp { get; set; }
		public ServiceTaskStatus Status { get; set; }

		public ServiceTask()
        {
			Timestamp = DateTime.Now;
        }
	}
}
