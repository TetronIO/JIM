namespace JIM.Models.Logic.DTOs
{
    public class SyncRuleHeader
    {
        public int Id { get; set; }

        public string Name { get; set; } = null!;

        public DateTime Created { get; set; }

        public string ConnectedSystemName { get; set; } = null!;

        public string ConnectedSystemObjectTypeName { get; set; } = null!;

        public string MetaverseObjectTypeName { get; set; } = null!;

        public SyncRuleDirection Direction { get; set; }

        public bool? ProvisionToConnectedSystem { get; set; }

        public bool? ProjectToMetaverse { get; set; }
        
        public bool Enabled { get; set; }
    }
}
