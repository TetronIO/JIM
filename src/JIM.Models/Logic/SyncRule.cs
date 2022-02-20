using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Models.Logic
{
    public class SyncRule
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public DateTime Created { get; set; }
        public DateTime? LastUpdated { get; set; }
        public ConnectedSystem ConnectedSystem { get; set; }
        public ConnectedSystemObjectType ConnectedSystemObjectType { get; set; }
        public MetaverseObjectType MetaverseObjectType { get; set; }
        public SyncRuleDirection Direction { get; set; }
        public bool? ProvisionToConnectedSystem { get; set; }
        public bool? ProjectToMetaverse { get; set; }
        public List<SyncRuleMapping> Mappings { get; set; }

        // todo: scoping filters
        // what happens when an object is in scope, then falls out of scope?
        // should/can we provide an option to cause deprovisioning?

        public SyncRule()
        {
            Mappings = new List<SyncRuleMapping>();
        }
    }
}
