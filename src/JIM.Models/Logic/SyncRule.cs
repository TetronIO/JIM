using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Models.Logic
{
    /// <summary>
    /// Defines the rules for how one or more attributes should flow between JIM and a connected system, or visa-versa.
    /// </summary>
    public class SyncRule
    {
        public int Id { get; set; }
        
        public string Name { get; set; } = null!;
        
        public DateTime Created { get; set; }
        
        public DateTime? LastUpdated { get; set; }
        
        public ConnectedSystem ConnectedSystem { get; set; } = null!;
        
        public ConnectedSystemObjectType ConnectedSystemObjectType { get; set; } = null!;
        
        public MetaverseObjectType MetaverseObjectType { get; set; } = null!;
        
        public SyncRuleDirection Direction { get; set; }
        
        public bool? ProvisionToConnectedSystem { get; set; }
        
        public bool? ProjectToMetaverse { get; set; }

        public SyncRuleStatus Status { get; set; }

        public List<SyncRuleMapping> Mappings { get; set; }

        // back-link for EF
        public List<Activity> Activities { get; set; } = null!;

        // todo: scoping filters
        // what happens when an object is in scope, then falls out of scope?
        // should/can we provide an option to cause deprovisioning?

        public SyncRule()
        {
            Status = SyncRuleStatus.Enabled;
            Created = DateTime.UtcNow;
            Mappings = new List<SyncRuleMapping>();
        }
    }
}
