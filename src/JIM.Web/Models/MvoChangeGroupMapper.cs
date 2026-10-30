// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Web.Shared;

namespace JIM.Web.Models;

/// <summary>
/// Maps Metaverse Object change records to the change timeline's display model, shared by a Metaverse Object's
/// Changes tab and the Object Changes detail panel so both describe a change the same way.
/// </summary>
public static class MvoChangeGroupMapper
{
    /// <summary>
    /// Maps one Metaverse Object change to the shape the change timeline and the Object Changes detail panel render.
    /// </summary>
    /// <param name="dto">The change.</param>
    /// <param name="metaverseObjectId">The changed object's id, for each attribute's Inspect link.</param>
    /// <param name="typePluralName">The object's type plural name, for the Inspect link; no link when null.</param>
    public static ChangeHistoryTimeline.ChangeGroup Map(MvoChangeHistoryDto dto, Guid metaverseObjectId, string? typePluralName)
    {
        return new ChangeHistoryTimeline.ChangeGroup
        {
            ChangeId = dto.Id,
            ChangeType = dto.ChangeType,
            ChangeTime = dto.ChangeTime,
            InitiatedByType = dto.InitiatedByType,
            ChangeInitiatorName = dto.InitiatedByName,
            ChangeInitiatorId = dto.InitiatedById,
            ChangeMechanismType = MechanismType(dto),
            RunProfileName = dto.RunProfileName,
            ConnectedSystemId = dto.ConnectedSystemId,
            ConnectedSystemName = dto.ConnectedSystemName,
            SyncRuleId = dto.SyncRuleId,
            SyncRuleName = dto.SyncRuleName,
            ActivityRunProfileExecutionItemId = dto.ActivityRunProfileExecutionItemId,
            CsoId = dto.CsoId,
            CsoExternalId = dto.CsoExternalId,
            AttributeChanges = dto.AttributeChanges
                .SelectMany(ac => ac.ValueChanges.Select(vc => ToAttributeChange(
                    ac.AttributeName, ac.AttributePlurality, ac.AttributeType, ac.AttributeId, vc, metaverseObjectId, typePluralName)))
                .ToList(),
            AttributeValueTotals = dto.AttributeChanges
                .Where(ac => ac.ValueChangeTotal > ac.ValueChanges.Count)
                .ToDictionary(ac => ac.AttributeName, ac => ac.ValueChangeTotal)
        };
    }

    /// <summary>One value of an attribute change, as the timeline shows it.</summary>
    /// <param name="attributeName">The attribute, as the change recorded its name.</param>
    /// <param name="plurality">The attribute's plurality.</param>
    /// <param name="attributeType">The attribute's data type.</param>
    /// <param name="attributeId">The Metaverse Attribute's id, for the Inspect link; null when it has since been deleted.</param>
    /// <param name="value">The value change.</param>
    /// <param name="metaverseObjectId">The changed object's id, for the Inspect link.</param>
    /// <param name="typePluralName">The object's type plural name, for the Inspect link; no link when null.</param>
    public static ChangeHistoryTimeline.AttributeChange ToAttributeChange(
        string attributeName, AttributePlurality plurality, AttributeDataType attributeType, int? attributeId,
        MvoValueChangeDto value, Guid metaverseObjectId, string? typePluralName) => new()
    {
        AttributeName = attributeName,
        ChangeType = value.ValueChangeType,
        Value = value.ReferenceValue == null ? value.ToDisplayString() : null,
        ReferenceValue = value.ReferenceValue != null
            ? new ChangeHistoryTimeline.ReferenceValueInfo
            {
                TypeName = value.ReferenceValue.TypeName,
                DisplayName = value.ReferenceValue.DisplayName ?? value.ReferenceValue.Id.ToString(),
                SecondaryId = null,
                Href = JIM.Utilities.Utilities.GetMetaverseObjectHref(value.ReferenceValue.Id, value.ReferenceValue.TypePluralName)
            }
            : null,
        IsMultiValued = plurality == AttributePlurality.MultiValued,
        AttributeType = attributeType,
        // Origin and the Inspect link (#399): only when the DTO carries a Metaverse attribute id (this page) and a
        // resolvable type plural name. Neither is available before the type has loaded, in which case the change
        // still renders, just without the Source column's chip/link.
        Origin = ProvenanceLogic.ResolveChangeValueOrigin(
            value.ContributedBySyncRuleId, value.ContributedBySyncRuleName,
            value.ContributedBySystemId, value.ContributedBySystemName, value.IsGeneratedValue,
            value.ContributedByKind, value.ContributedByClaimType),
        AttributeInspectHref = attributeId.HasValue && !string.IsNullOrEmpty(typePluralName)
            ? JIM.Utilities.Utilities.GetMetaverseObjectInspectHref(metaverseObjectId, typePluralName, attributeId.Value)
            : null
    };

    private static string MechanismType(MvoChangeHistoryDto dto)
    {
        if (dto.ChangeInitiatorType is MetaverseObjectChangeInitiatorType.ExampleData
            or MetaverseObjectChangeInitiatorType.User
            or MetaverseObjectChangeInitiatorType.WorkflowInstance
            or MetaverseObjectChangeInitiatorType.GroupMembershipRuleEvaluation
            or MetaverseObjectChangeInitiatorType.System)
        {
            return dto.ChangeInitiatorType.ToString();
        }

        return dto.ConnectedSystemRunType switch
        {
            ConnectedSystemRunType.FullImport or ConnectedSystemRunType.DeltaImport => "Import",
            ConnectedSystemRunType.FullSynchronisation or ConnectedSystemRunType.DeltaSynchronisation => "SynchronisationRule",
            ConnectedSystemRunType.Export => "Export",
            _ => "SynchronisationRule"
        };
    }
}
