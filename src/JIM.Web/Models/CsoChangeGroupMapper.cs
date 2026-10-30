// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging.DTOs;
using JIM.Web.Shared;

namespace JIM.Web.Models;

/// <summary>
/// Maps Connected System Object change records to the change timeline's display model, shared by a Connected System
/// Object's change history and a deleted object's, so both describe a change the same way.
/// </summary>
public static class CsoChangeGroupMapper
{
    /// <summary>
    /// Maps one Connected System Object change to the shape the change timeline renders.
    /// </summary>
    /// <param name="dto">The change, with each attribute's first values and its total.</param>
    /// <param name="connectedSystemId">The object's Connected System, for linking.</param>
    /// <param name="connectedSystemName">The object's Connected System's name.</param>
    /// <param name="changeMechanismType">What made the change, as the timeline names it.</param>
    public static ChangeHistoryTimeline.ChangeGroup Map(CsoChangeHistoryDto dto, int? connectedSystemId, string? connectedSystemName, string changeMechanismType = "Import")
    {
        return new ChangeHistoryTimeline.ChangeGroup
        {
            ChangeId = dto.Id,
            ChangeType = dto.ChangeType,
            ChangeTime = dto.ChangeTime,
            InitiatedByType = dto.InitiatedByType,
            ChangeInitiatorName = dto.InitiatedByName,
            ChangeInitiatorId = dto.InitiatedById,
            ChangeMechanismType = changeMechanismType,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemName = connectedSystemName,
            RunProfileName = dto.RunProfileName,
            ActivityRunProfileExecutionItemId = dto.ActivityRunProfileExecutionItemId,
            AttributeChanges = dto.AttributeChanges
                .SelectMany(ac => ac.ValueChanges.Select(vc => ToAttributeChange(ac.AttributeName, ac.AttributePlurality, ac.AttributeType, vc)))
                .ToList(),
            AttributeValueTotals = dto.AttributeChanges
                .Where(ac => ac.ValueChangeTotal > ac.ValueChanges.Count)
                .ToDictionary(ac => ac.AttributeName, ac => ac.ValueChangeTotal)
        };
    }

    /// <summary>One value of an attribute change, as the timeline shows it.</summary>
    public static ChangeHistoryTimeline.AttributeChange ToAttributeChange(
        string attributeName, AttributePlurality plurality, AttributeDataType attributeType, CsoValueChangeDto value) => new()
    {
        AttributeName = attributeName,
        ChangeType = value.ValueChangeType,
        Value = value.ReferenceValue == null ? value.ToDisplayString() : null,
        ReferenceValue = value.ReferenceValue != null
            ? new ChangeHistoryTimeline.ReferenceValueInfo
            {
                TypeName = value.ReferenceValue.TypeName,
                DisplayName = value.ReferenceValue.DisplayName ?? value.ReferenceValue.Id.ToString(),
                SecondaryId = value.ReferenceValue.SecondaryId,
                Href = JIM.Utilities.Utilities.GetConnectedSystemObjectHref(value.ReferenceValue.ConnectedSystemId, value.ReferenceValue.Id)
            }
            : null,
        IsMultiValued = plurality == AttributePlurality.MultiValued,
        AttributeType = attributeType
    };
}
