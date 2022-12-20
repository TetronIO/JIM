using JIM.Models.Transactional;

namespace JIM.Models.Staging
{
    public partial class ConnectedSystem
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string? Description { get; set; }
        public DateTime Created { get; set; }
        public DateTime LastUpdated { get; set; }
        public List<ConnectedSystemRunProfile> RunProfiles { get; set; }
        public List<ConnectedSystemObject> Objects { get; set; }
        public List<PendingExport> PendingExports { get; set; }
        public ConnectorDefinition ConnectorDefinition { get; set; }
        public List<ConnectedSystemSetting> Settings { get; set; }

        public ConnectedSystem()
        {
            RunProfiles = new List<ConnectedSystemRunProfile>();
            Objects = new List<ConnectedSystemObject>();
            Settings = new List<ConnectedSystemSetting>();
            Created = DateTime.Now;
        }
    }
}
