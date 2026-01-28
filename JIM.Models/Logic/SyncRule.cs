using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Utility;
namespace JIM.Models.Logic;

/// <summary>
/// Defines the rules for how one or more attributes should flow between JIM and a connected system, or visa-versa.
/// </summary>
public class SyncRule : IAuditable, IValidated
{
    public int Id { get; set; }
        
    public string Name { get; set; } = null!;

    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The type of security principal that created this entity.
    /// </summary>
    public ActivityInitiatorType CreatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that created this entity.
    /// Null for system-created (seeded) entities.
    /// </summary>
    public Guid? CreatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of creation.
    /// Retained even if the principal is later deleted.
    /// </summary>
    public string? CreatedByName { get; set; }

    /// <summary>
    /// When the sync rule was last modified by an admin. Not the last time it was evaluated during a sync run.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The type of security principal that last modified this entity.
    /// </summary>
    public ActivityInitiatorType LastUpdatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that last modified this entity.
    /// </summary>
    public Guid? LastUpdatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of the last modification.
    /// </summary>
    public string? LastUpdatedByName { get; set; }
        
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
    /// For Export rules: Action to take when an MVO falls out of scope.
    /// Only applies when Direction = Export.
    /// </summary>
    public OutboundDeprovisionAction OutboundDeprovisionAction { get; set; } = OutboundDeprovisionAction.Disconnect;

    /// <summary>
    /// For Import rules: Action to take when a CSO falls out of scope.
    /// Only applies when Direction = Import.
    /// </summary>
    public InboundOutOfScopeAction InboundOutOfScopeAction { get; set; } = InboundOutOfScopeAction.Disconnect;

    /// <summary>
    /// For Export rules: When true (default), inbound changes from the target system will trigger
    /// re-evaluation of this export rule to detect and remediate drift.
    /// Set to false to allow drift (e.g., for emergency access scenarios).
    /// Only applicable when Direction = Export.
    /// </summary>
    public bool EnforceState { get; set; } = true;

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

    /// <summary>
    /// Contains the scoping criteria that determines which objects are in scope for this sync rule.
    /// For Export rules: evaluates Metaverse Object attributes to determine which MVOs should be exported.
    /// For Import rules: evaluates Connected System Object attributes to determine which CSOs should be projected/joined.
    /// No rules mean all objects of the applicable type are in scope.
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

        // Only warn about missing matching rules if this sync rule manages its own matching rules (Advanced Mode)
        // In Simple Mode (ObjectMatchingRuleMode.ConnectedSystem), matching rules are defined on the Connected System
        if (Direction == SyncRuleDirection.Import &&
            ObjectMatchingRules.Count == 0 &&
            ConnectedSystem?.ObjectMatchingRuleMode != ObjectMatchingRuleMode.ConnectedSystem)
            response.Add(new ValidityStatusItem(ValidityStatusItemLevel.Warning, "No object matching rules have been defined. Whilst valid, this is not recommended. Object Matching rules help minimise synchronisation errors in uncommon, but important scenarios."));

        if (AttributeFlowRules.Count == 0)
            response.Add(new ValidityStatusItem(ValidityStatusItemLevel.Warning, "No attribute flow rules have been defined. Whilst valid, this means objects will lack nearly all attributes."));

        return response;
    }
}