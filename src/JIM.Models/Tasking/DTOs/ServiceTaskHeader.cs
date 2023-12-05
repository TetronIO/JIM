using JIM.Models.Core;

namespace JIM.Models.Tasking.DTOs
{
    public class ServiceTaskHeader
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = null!;

        public string Type { get; set; } = null!;

        public DateTime Timestamp { get; set; }

        public ServiceTaskStatus Status { get; set; }

        public MetaverseObject? InitiatedBy { get; set; }
    }
}
