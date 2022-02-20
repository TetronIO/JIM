namespace JIM.Models.Staging
{
    public partial class ConnectedSystem
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string? Description { get; set; }
        public DateTime Created { get; set; }
        public DateTime LastUpdated { get; set; }
        public List<ConnectedSystemRunProfile> RunProfiles { get; set; }

        public ConnectedSystem()
        {
            RunProfiles = new List<ConnectedSystemRunProfile>();
        }
    }
}
