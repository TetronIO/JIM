namespace JIM.Models.Tasking.DTOs
{
    public class ServiceTaskHeader
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public DateTime Timestamp { get; set; }
        public ServiceTaskStatus Status { get; set; }
    }
}
