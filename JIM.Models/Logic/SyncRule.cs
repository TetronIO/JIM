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

        public MetaverseObject? CreatedBy { get; set; }
        
        /// <summary>
        /// When the sync rule was last modified by an admin. Not the last time it was evaluated during a sync run.
        /// </summary>
        public DateTime? LastUpdated { get; set; }
        
        /// <summary>
        /// The connected system this sync rule applies to. A sync rule applies to a single connected system only.
        /// </summary>
        public ConnectedSystem ConnectedSystem { get; set; } = null!;
        
        /// <summary>
        /// What type of object should this sync rule apply to in the connected system?
        /// </summary>
        public ConnectedSystemObjectType ConnectedSystemObjectType { get; set; } = null!;
        
        /// <summary>
        /// What type of object in the Metaverse, should this sync rule apply to?
        /// </summary>
        public MetaverseObjectType MetaverseObjectType { get; set; } = null!;
        
        /// <summary>
        /// Which direction should the data flow? Either in to JIM, or out from it.
        /// </summary>
        public SyncRuleDirection Direction { get; set; }
        
        /// <summary>
        /// Should this sync rule also cause an object to be created in the connected system, or just update attributes for existing objects?
        /// This is normally set to true when the connected system is a 'downstream' system that JIM is responsible for managing objects in.
        /// Though it can be set to false if it's a source system (i.e. HR), or if that system has it's own Joiner processes.
        /// </summary>
        public bool? ProvisionToConnectedSystem { get; set; }

        /// <summary>
        /// Should this sync rule also cause an object imported from a connected system to be projected (created in) the Metaverse? 
        /// This is normally set to true for a source system (i.e. HR).
        /// </summary>              
        public bool? ProjectToMetaverse { get; set; }

        /// <summary>
        /// A sync rule can be disabled, meaning it will not be evaluated when run profiles are executed.
        /// This can be especially useful for admins when they need to be able to easily stop synchronising specific objects for a given system, without changing the sync schedule(s).
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Contains all the logic that controls what attributes on a metaverse object should flow to what connected system object attribute, or visa-versa, depending on the sync rule direction.
        /// </summary>
        public List<SyncRuleMapping> AttributeFlowRules { get; set; }

        /// <summary>
        /// Contains all the logic that determines how connected system objects should match a counterpart in the metaverse for inbound sync rules.
        /// </summary>
        public List<SyncRuleMapping> ObjectMatchingRules { get; set; }

        // back-link for EF
        public List<Activity> Activities { get; set; } = null!;

        // todo: scoping filters
        // what happens when an object is in scope, then falls out of scope?
        // should/can we provide an option to cause deprovisioning?

        public SyncRule()
        {
            Enabled = true;
            Created = DateTime.UtcNow;
            AttributeFlowRules = new List<SyncRuleMapping>();
            ObjectMatchingRules = new List<SyncRuleMapping>();
        }
    }
}
