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

        /// <summary>
        /// If the Connector implements partitions, then at least one partition is required, and containers may reside under those, if implemented by the Connector.
        /// Note: Connectors don't have to support partitions, or containers.
        /// </summary>
        public List<ConnectedSystemPartition>? Partitions { get; set; }

        /// <summary>
        /// If the Connector Definition doesn't implement partitions, and the Connector does implement containers, then one or more containers will be found here.
        /// It's possible that there's a single top-level container, with child-containers under there.
        /// Note: Connectors don't have to support partitions, or containers.
        /// </summary>
        public List<ConnectedSystemContainer>? Containers { get; set; }

        public ConnectedSystem()
        {
            RunProfiles = new List<ConnectedSystemRunProfile>();
            Objects = new List<ConnectedSystemObject>();
            Settings = new List<ConnectedSystemSetting>();
            Created = DateTime.Now;
        }
    }
}
