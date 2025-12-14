using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Utility;
namespace JIM.Models.Logic;

/// <summary>
/// Defines the rules for how one or more attributes should flow between JIM and a connected system, or visa-versa.
/// </summary>
public class SyncRule: IValidated
{
    public int Id { get; set; }
        
    public string Name { get; set; } = null!;
        
    public DateTime Created { get; } = DateTime.UtcNow;

    public MetaverseObject? CreatedBy { get; set; }
        
    /// <summary>
    /// When the sync rule was last modified by an admin. Not the last time it was evaluated during a sync run.
    /// </summary>
    public DateTime? LastUpdated { get; set; }
        
    /// <summary>
    /// The connected system this sync rule applies to. A sync rule applies to a single connected system only.
    /// </summary>
    public ConnectedSystem ConnectedSystem { get; set; } = null!;
    public int ConnectedSystemId { get; set; }
        
    /// <summary>
    /// What type of object should this sync rule apply to in the connected system?
    /// </summary>
    public ConnectedSystemObjectType ConnectedSystemObjectType { get; set; } = null!;
    public int ConnectedSystemObjectTypeId { get; set; }
        
    /// <summary>
    /// What type of object in the Metaverse, should this sync rule apply to?
    /// </summary>
    public MetaverseObjectType MetaverseObjectType { get; set; } = null!;
    public int MetaverseObjectTypeId { get; set; }
        
    /// <summary>
    /// Which direction should the data flow? Either in to JIM, or out from it.
    /// </summary>
    public SyncRuleDirection Direction { get; set; }
        
    /// <summary>
    /// Should this sync rule also cause an object to be created in the connected system, or just update attributes for existing objects?
    /// This is normally set to true when the connected system is a 'downstream' system that JIM is responsible for managing objects in.
    /// Though it can be set to false if it's a source system (i.e. HR), or if that system has its own Joiner processes.
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
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Contains all the logic that controls what attributes on a metaverse object should flow to what connected system object attribute,
    /// or visa-versa, depending on the sync rule direction.
    /// </summary>
    public List<SyncRuleMapping> AttributeFlowRules { get; set; } = new();

    /// <summary>
    /// Contains all the logic that determines how Connected System Objects should match a counterpart in the Metaverse.
    /// Used when the Connected System's ObjectMatchingRuleMode is set to SyncRule (advanced mode).
    /// When ObjectMatchingRuleMode is ConnectedSystem (default), rules are defined on the ConnectedSystemObjectType instead.
    /// </summary>
    public List<ObjectMatchingRule> ObjectMatchingRules { get; set; } = new();
    
    /// <summary>
    /// Backlink for Entity Framework purposes to all Activities for this SyncRule.
    /// </summary>
    public List<Activity> Activities { get; set; } = null!;

    // TODO: what happens when an object is in scope, then falls out of scope?
    // should/can we provide an option to cause deprovisioning/disconnection?

    /// <summary>
    /// Contains all the logic that determines which Metaverse objects should be exported to the Connected System.
    /// No rules mean that all objects of the Metaverse Object Type will be in scope of an outbound sync rule.
    /// </summary>
    public List<SyncRuleScopingCriteriaGroup> ObjectScopingCriteriaGroups { get; set; } = new();

    public override string ToString()
    {
        return $"Sync Rule: {Name} ({Id})";
    }

    public bool IsValid()
    {
        return !Validate().Any(q => q.Level > ValidityStatusItemLevel.Warning);
    }

    public List<ValidityStatusItem> Validate()
    {
        var response = new List<ValidityStatusItem>();

        if (string.IsNullOrEmpty(Name))
            response.Add(new ValidityStatusItem(ValidityStatusItemLevel.Error, "Name must be set."));

        if (ConnectedSystem == null)
            response.Add(new ValidityStatusItem(ValidityStatusItemLevel.Error, "Connected System must be set."));

        if (ConnectedSystemObjectType == null)
            response.Add(new ValidityStatusItem(ValidityStatusItemLevel.Error, "Connected System Object Type must be set."));

        if (MetaverseObjectType == null)
            response.Add(new ValidityStatusItem(ValidityStatusItemLevel.Error, "Metaverse Object Type must be set."));

        if (Direction == SyncRuleDirection.NotSet)
            response.Add(new ValidityStatusItem(ValidityStatusItemLevel.Error, "Direction must be set."));

        if (Direction == SyncRuleDirection.Import && ObjectMatchingRules.Count == 0)
            response.Add(new ValidityStatusItem(ValidityStatusItemLevel.Warning, "No object matching rules have been defined. Whilst valid, this is not recommended. Object Matching rules help minimise synchronisation errors in uncommon, but important scenarios."));

        if (AttributeFlowRules.Count == 0)
            response.Add(new ValidityStatusItem(ValidityStatusItemLevel.Warning, "No attribute flow rules have been defined. Whilst valid, this means objects will lack nearly all attributes."));

        return response;
    }
}