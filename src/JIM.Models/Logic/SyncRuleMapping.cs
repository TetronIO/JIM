// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
namespace JIM.Models.Logic;

/// <summary>
/// Defines how data should flow from JIM to a Connected System (or vice versa) for a specific attribute.
/// There can only be one mapping per target attribute.
/// i.e. you might be mapping a single source attribute to a target attribute, or you might use an expression as a
/// source that references multiple attributes.
/// </summary>
public class SyncRuleMapping : IAuditable
{
    public int Id { get; set; }

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
    /// When the entity was last modified (UTC). Null if never modified after creation.
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
    /// A backlink to the parent SynchronisationRule.
    /// </summary>
    public SyncRule? SyncRule { get; set; }

    /// <summary>
    /// The owning Synchronisation Rule. Required (#1550): a mapping always belongs to a rule, so removing one
    /// from the rule's collection deletes the row rather than orphaning it with a null owner, and the orphan
    /// state cannot be expressed at the database level.
    /// </summary>
    public int SyncRuleId { get; set; }

    /// <summary>
    /// The sources that provide the value for the target attribute when the mapping is evaluated. 
    /// Supported scenarios:
    /// - Just one: for mapping a single attribute to the target attribute. i.e. attribute_1 => attribute
    /// - Just one: for using a single function to generate a value for the target attribute, i.e. Trim(attribute) => attribute
    /// - Just one: for using an expression to generate a value for the target attribute. i.e. attribute_1 ?? attribute_2 => attribute
    /// - Multiple: for using multiple function calls that chain through each other to generate a value for the target attribute.
    /// </summary>
    public List<SyncRuleMappingSource> Sources { get; } = new();

    /// <summary>
    /// For an import rule, this is where the imported attribute value ends up being assigned to in the Metaverse.
    /// </summary>
    public MetaverseAttribute? TargetMetaverseAttribute { get; set; }
    public int? TargetMetaverseAttributeId { get; set; }

    /// <summary>
    /// For an export rule, this is where the exported attribute value ends up being assigned to in the Connected System.
    /// </summary>
    public ConnectedSystemObjectTypeAttribute? TargetConnectedSystemAttribute { get; set; }
    public int? TargetConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// Inbound (import) text value-processing transforms applied to the value as it flows to the target
    /// Metaverse attribute. Defaults to <see cref="InboundValueProcessing.TreatWhitespaceAsNoValue"/>
    /// (JIM's opinionated default). Only applies to import mappings targeting text attributes; ignored for
    /// export mappings and non-text attribute types. See <see cref="CaseNormalisation"/> for the case option.
    /// </summary>
    public InboundValueProcessing InboundValueProcessing { get; set; } = InboundValueProcessing.TreatWhitespaceAsNoValue;

    /// <summary>
    /// Inbound (import) case normalisation applied to the text value as it flows to the target Metaverse
    /// attribute. Defaults to <see cref="InboundCaseNormalisation.None"/>. Only applies to import mappings
    /// targeting text attributes.
    /// </summary>
    public InboundCaseNormalisation CaseNormalisation { get; set; } = InboundCaseNormalisation.None;

    /// <summary>
    /// Priority for this attribute contribution when multiple import Synchronisation Rules flow to the same
    /// Metaverse Object attribute. Lower numbers win (1 is the highest priority). The priority list for a
    /// Metaverse attribute is the ordered set of import mappings targeting it; a Connected System may appear
    /// multiple times via differently-scoped Synchronisation Rules, which is what enables fine-grained authority.
    /// Defaults to <see cref="int.MaxValue"/> (a safe-addition sentinel): a newly added import mapping never wins
    /// resolution until an admin explicitly orders the attribute's priority list. Only applies to import mappings
    /// (mappings with a <see cref="TargetMetaverseAttribute"/>); ignored for export mappings.
    /// </summary>
    public int Priority { get; set; } = int.MaxValue;

    /// <summary>
    /// When true, if this mapping's Synchronisation Rule is connected to the Metaverse Object and in scope but
    /// contributes null/absent for this attribute, resolution stops immediately without falling back to
    /// lower-priority contributions ("Null is a value"; the authoritative source asserts no value). Has no effect
    /// when the rule has no opinion for the Metaverse Object (disabled rule, no joined Connected System Object, or
    /// the Connected System Object is out of the rule's scope); in that case the mapping is skipped and evaluation
    /// continues to the next priority regardless of this flag. When false (default), null contributions fall
    /// through to the next priority level. Only applies to import mappings; ignored for export mappings.
    /// </summary>
    public bool NullIsValue { get; set; }

    /// <summary>
    /// When true, this mapping only flows during the initial provisioning (Create) export to the Connected
    /// System. Once the Connected System Object is past provisioning, the target attribute is unmanaged by JIM:
    /// Update export evaluation skips this mapping and Drift Correction does not re-assert the value, allowing
    /// the external system to own it. Connected System Objects that join to pre-existing objects in the target
    /// system never receive the value. Only applies to export mappings (mappings with a
    /// <see cref="TargetConnectedSystemAttribute"/>); ignored for import mappings.
    /// </summary>
    public bool InitialExportOnly { get; set; }

    /// <summary>
    /// Whether this mapping is evaluated at all. A disabled mapping is skipped by synchronisation in both
    /// directions, on provisioning (Create) exports as much as Updates: unlike <see cref="InitialExportOnly"/>,
    /// disabled means the mapping does not run until an administrator re-enables it. Defaults to true so every
    /// mapping persisted before this field existed behaves exactly as it always has. Disabling one mapping is
    /// the smallest safe response to a single removed or redefined source attribute; disabling the whole
    /// Synchronisation Rule (its own <see cref="SyncRule.Enabled"/>) stops every flow it carries (#1485).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Why this mapping is disabled, when something disabled it on the administrator's behalf (the schema
    /// refresh decision's "Apply and Disable Dependents" option names the refresh and the schema change here).
    /// Null when the mapping is enabled, and null when an administrator disabled it themselves; the presence of
    /// a reason is what distinguishes the two. Cleared when the mapping is re-enabled.
    /// </summary>
    public string? DisabledReason { get; set; }

    /// <summary>
    /// Whether this export mapping contributes to an Update export to the Connected System. The export
    /// evaluation and Drift Correction paths both consult this before evaluating a mapping, so a mapping
    /// that does not flow on update is never turned into a Pending Export attribute value change.
    /// Import mappings have no Connected System target and are unaffected.
    /// </summary>
    /// <returns>
    /// False when the mapping is <see cref="InitialExportOnly"/>, or when its target Connected System
    /// attribute is <see cref="AttributeWritability.WritableOnCreate"/>; otherwise true.
    /// </returns>
    public bool FlowsOnUpdateExport()
    {
        if (InitialExportOnly)
            return false;

        // Synchronisation integrity: a WritableOnCreate attribute identifies the object to the Connected
        // System (a relational primary key, a directory's relative distinguished name), and the system
        // accepts a value for it only as part of creating the object. Emitting it on an Update Pending
        // Export would rewrite that identifier and silently sever the link between the Connected System
        // Object and the row or entry it is anchored to. The exclusion is deliberate and must stay
        // explicit: authoring-time validation alone cannot prevent it, because targeting the attribute
        // is legitimate (and necessary) for provisioning.
        return TargetConnectedSystemAttribute?.Writability != AttributeWritability.WritableOnCreate;
    }

    /// <summary>
    /// Helper method to provide a description for the user on what type of source configuration this is.
    /// </summary>
    public SyncRuleMappingSourcesType GetSourceType()
    {
        if (Sources.Count == 0)
            return SyncRuleMappingSourcesType.NotSet;

        if (Sources.All(s => s.ConnectedSystemAttribute != null || s.MetaverseAttribute != null))
            return SyncRuleMappingSourcesType.AttributeMapping;

        if (Sources.All(s => !string.IsNullOrWhiteSpace(s.Expression)))
            return SyncRuleMappingSourcesType.ExpressionMapping;

        return SyncRuleMappingSourcesType.AdvancedMapping;
    }
}
