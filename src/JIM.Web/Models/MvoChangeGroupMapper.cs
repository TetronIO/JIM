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
                .SelectMany(ac => ac.ValueChanges.Select(vc => new ChangeHistoryTimeline.AttributeChange
                {
                    AttributeName = ac.AttributeName,
                    ChangeType = vc.ValueChangeType,
                    Value = vc.ReferenceValue == null ? vc.ToDisplayString() : null,
                    ReferenceValue = vc.ReferenceValue != null
                        ? new ChangeHistoryTimeline.ReferenceValueInfo
                        {
                            TypeName = vc.ReferenceValue.TypeName,
                            DisplayName = vc.ReferenceValue.DisplayName ?? vc.ReferenceValue.Id.ToString(),
                            SecondaryId = null,
                            Href = JIM.Utilities.Utilities.GetMetaverseObjectHref(vc.ReferenceValue.Id, vc.ReferenceValue.TypePluralName)
                        }
                        : null,
                    IsMultiValued = ac.AttributePlurality == AttributePlurality.MultiValued,
                    AttributeType = ac.AttributeType,
                    // Origin and the Inspect link (#399): only when the DTO carries a Metaverse attribute id
                    // (this page) and a resolvable type plural name. Neither is available before the type has
                    // loaded, in which case the change still renders, just without the Source column's chip/link.
                    Origin = ProvenanceLogic.ResolveChangeValueOrigin(
                        vc.ContributedBySyncRuleId, vc.ContributedBySyncRuleName,
                        vc.ContributedBySystemId, vc.ContributedBySystemName, vc.IsGeneratedValue,
                        vc.ContributedByKind, vc.ContributedByClaimType),
                    AttributeInspectHref = ac.AttributeId.HasValue && !string.IsNullOrEmpty(typePluralName)
                        ? JIM.Utilities.Utilities.GetMetaverseObjectInspectHref(metaverseObjectId, typePluralName, ac.AttributeId.Value)
                        : null
                }))
                .ToList()
        };
    }

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
