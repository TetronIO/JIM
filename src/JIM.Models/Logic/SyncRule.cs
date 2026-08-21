// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Utility;
namespace JIM.Models.Logic;

/// <summary>
/// Defines the rules for how one or more attributes should flow between JIM and a Connected System, or visa-versa.
/// </summary>
public class SyncRule : IAuditable, IValidated
{
    public int Id { get; set; }
        
    public string Name { get; set; } = null!;

    /// <summary>
    /// An optional description of what this Synchronisation Rule does, for administrator reference.
    /// </summary>
    public string? Description { get; set; }

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
    /// When the Synchronisation Rule was last modified by an admin. Not the last time it was evaluated during a sync run.
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
    /// The Connected System this Synchronisation Rule applies to. A Synchronisation Rule applies to a single Connected System only.
    /// </summary>
    public ConnectedSystem ConnectedSystem { get; set; } = null!;
    public int ConnectedSystemId { get; set; }
        
    /// <summary>
    /// What type of object should this Synchronisation Rule apply to in the Connected System?
    /// </summary>
    public ConnectedSystemObjectType ConnectedSystemObjectType { get; set; } = null!;
    public int ConnectedSystemObjectTypeId { get; set; }
        
    /// <summary>
    /// What type of object in the Metaverse, should this Synchronisation Rule apply to?
    /// </summary>
    public MetaverseObjectType MetaverseObjectType { get; set; } = null!;
    public int MetaverseObjectTypeId { get; set; }
        
    /// <summary>
    /// Which direction should the data flow? Either in to JIM, or out from it.
    /// </summary>
    public SyncRuleDirection Direction { get; set; }
        
    /// <summary>
    /// Should this Synchronisation Rule also cause an object to be created in the Connected System, or just update attributes for existing objects?
    /// This is normally set to true when the Connected System is a 'downstream' system that JIM is responsible for managing objects in.
    /// Though it can be set to false if it's a source system (i.e. HR), or if that system has its own Joiner processes.
    /// </summary>
    public bool? ProvisionToConnectedSystem { get; set; }

    /// <summary>
    /// Should this Synchronisation Rule also cause an object imported from a Connected System to be projected (created in) the Metaverse? 
    /// This is normally set to true for a source system (i.e. HR).
    /// </summary>              
    public bool? ProjectToMetaverse { get; set; }

    /// <summary>
    /// A Synchronisation Rule can be disabled, meaning it will not be evaluated when Run Profiles are executed.
    /// This can be especially useful for admins when they need to be able to easily stop synchronising specific objects for a given system, without changing the sync schedule(s).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Why this Synchronisation Rule is disabled, when something disabled it on the administrator's behalf
    /// (the schema refresh decision's "Apply and Disable Dependents" option names the refresh and the schema
    /// change here, #1485). Null when the rule is enabled, and null when an administrator disabled it
    /// themselves; the presence of a reason is what distinguishes the two. Cleared whenever an enabled rule
    /// is saved.
    /// </summary>
    public string? DisabledReason { get; set; }

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
    /// Contains all the logic that controls what attributes on a Metaverse Object should flow to what Connected System Object attribute,
    /// or visa-versa, depending on the Synchronisation Rule direction.
    /// </summary>
    public List<SyncRuleMapping> AttributeFlowRules { get; set; } = new();

    /// <summary>
    /// Contains all the logic that determines how Connected System Objects should match a counterpart in the Metaverse.
    /// Used when the Connected System's ObjectMatchingRuleMode is set to SyncRule (advanced mode).
    /// When ObjectMatchingRuleMode is ConnectedSystem (default), rules are defined on the ConnectedSystemObjectType instead.
    /// </summary>
    public List<ObjectMatchingRule> ObjectMatchingRules { get; set; } = new();

    /// <summary>
    /// Whether, and how, this rule gives a newly provisioned account its first password.
    /// <para>
    /// Null means no initial password, which is where every rule starts. Only meaningful alongside
    /// <see cref="ProvisionToConnectedSystem"/>, since a rule that never creates anything never has an account
    /// to set a first password on.
    /// </para>
    /// </summary>
    public SyncRuleInitialPassword? InitialPassword { get; set; }

    /// <summary>
    /// Backlink for Entity Framework purposes to all Activities for this SyncRule.
    /// </summary>
    public List<Activity> Activities { get; set; } = null!;

    /// <summary>
    /// Contains the scoping criteria that determines which objects are in scope for this Synchronisation Rule.
    /// For Export rules: evaluates Metaverse Object attributes to determine which MVOs should be exported.
    /// For Import rules: evaluates Connected System Object attributes to determine which CSOs should be projected/joined.
    /// No rules mean all objects of the applicable type are in scope.
    /// </summary>
    public List<SyncRuleScopingCriteriaGroup> ObjectScopingCriteriaGroups { get; set; } = new();

    public override string ToString()
    {
        return $"Synchronisation Rule: {Name} ({Id})";
    }

    public bool IsValid()
    {
        return !Validate().Any(q => q.Level > ValidityStatusItemLevel.Warning);
    }

    public List<ValidityStatusItem> Validate()
    {
        var response = new List<ValidityStatusItem>();

        if (string.IsNullOrEmpty(Name))
            response.Add(new ValidityStatusItem(ValidityStatusItemLevel.Error, "Name must be set"));

        if (ConnectedSystem == null)
            response.Add(new ValidityStatusItem(ValidityStatusItemLevel.Error, "Connected System must be set"));

        if (ConnectedSystemObjectType == null)
            response.Add(new ValidityStatusItem(ValidityStatusItemLevel.Error, "Connected System Object Type must be set"));

        if (MetaverseObjectType == null)
            response.Add(new ValidityStatusItem(ValidityStatusItemLevel.Error, "Metaverse Object Type must be set"));

        if (Direction == SyncRuleDirection.NotSet)
            response.Add(new ValidityStatusItem(ValidityStatusItemLevel.Error, "Direction must be set"));

        // Only warn about missing matching rules if this Synchronisation Rule manages its own matching rules (Advanced Mode)
        // In Simple Mode (ObjectMatchingRuleMode.ConnectedSystem), matching rules are defined on the Connected System
        if (Direction == SyncRuleDirection.Import &&
            ObjectMatchingRules.Count == 0 &&
            ConnectedSystem?.ObjectMatchingRuleMode != ObjectMatchingRuleMode.ConnectedSystem)
            response.Add(new ValidityStatusItem(ValidityStatusItemLevel.Warning, "No Object Matching Rules have been defined. Whilst valid, this is not recommended. Object Matching Rules help minimise synchronisation errors in uncommon, but important scenarios"));

        if (AttributeFlowRules.Count == 0)
            response.Add(new ValidityStatusItem(ValidityStatusItemLevel.Warning, "No Attribute Flow Rules have been defined. Whilst valid, this means no data will flow between the two systems"));

        AddCredentialLikeAttributeWarnings(response);

        return response;
    }

    /// <summary>
    /// Warns where an Attribute Flow targets an attribute whose name suggests it holds a credential (#1119,
    /// requirement 16).
    /// <para>
    /// The eight well-known credential attributes are blocked outright by <see cref="CredentialAttributes"/>, but
    /// an administrator can rename an attribute and a target system can use a name JIM has never heard of, so
    /// nothing otherwise stops an Attribute Flow carrying a password as an ordinary value. Doing so persists the
    /// secret as a Connected System Object attribute value and a Metaverse Object attribute value, in both change
    /// histories, in Pending Exports, in export previews, in search results and the API, and in every database
    /// backup: the exact exposure the password channel exists to avoid, reintroduced by the back door.
    /// </para>
    /// <para>
    /// A warning rather than a refusal, deliberately. The check is a substring match on a name, so it will
    /// sometimes be wrong, and JIM does not own an administrator's schema; this is a guardrail that makes the
    /// safe path the obvious one and the dangerous path deliberate, not a security boundary.
    /// </para>
    /// </summary>
    private void AddCredentialLikeAttributeWarnings(List<ValidityStatusItem> response)
    {
        // Named one at a time rather than gathered into a single warning: an administrator acts on one attribute
        // at a time, and a list of names in one line is easy to skim past.
        foreach (var attributeName in AttributeFlowRules
                     .SelectMany(mapping => new[]
                     {
                         mapping.TargetMetaverseAttribute?.Name,
                         mapping.TargetConnectedSystemAttribute?.Name
                     })
                     .Where(CredentialAttributes.HasCredentialLikeName)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            response.Add(new ValidityStatusItem(ValidityStatusItemLevel.Warning,
                $"The Attribute Flow targeting '{attributeName}' looks like it may carry a password. If it does, " +
                "use Password Synchronisation on the Connected System instead: a password flowed as an attribute " +
                "is stored in Metaverse Object and Connected System Object attribute values, change history, " +
                "Pending Exports, export previews, search results and database backups. If this attribute is not " +
                "a password, no action is needed."));
        }
    }
}