namespace JIM.Models.Logic.DTOs
{
    public class SyncRuleHeader
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime Created { get; set; }
        public string ConnectedSystemName { get; set; }
        public string ConnectedSystemObjectTypeName { get; set; }
        public string MetaverseObjectTypeName { get; set; }
        public SyncRuleDirection Direction { get; set; }
        public bool? ProvisionToConnectedSystem { get; set; }
        public bool? ProjectToMetaverse { get; set; }
    }
}
