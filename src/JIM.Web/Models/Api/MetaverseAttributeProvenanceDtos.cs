// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// Everything the portal's attribute inspector shows for one attribute on one Metaverse Object.
/// </summary>
public class MetaverseAttributeProvenanceDto
{
    /// <summary>
    /// The unique identifier of the Metaverse Object.
    /// </summary>
    public Guid MetaverseObjectId { get; set; }

    /// <summary>
    /// The unique identifier of the Metaverse Object's Object Type.
    /// </summary>
    public int MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// The unique identifier of the attribute.
    /// </summary>
    public int AttributeId { get; set; }

    /// <summary>
    /// The attribute's name.
    /// </summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>
    /// The kind of value the attribute holds.
    /// </summary>
    public AttributeDataType AttributeType { get; set; }

    /// <summary>
    /// Whether the attribute holds one value or many.
    /// </summary>
    public AttributePlurality AttributePlurality { get; set; }

    /// <summary>
    /// The values the attribute holds now, each with its origin; at most 50, CurrentValueTotalCount giving the full
    /// count.
    /// </summary>
    public List<ProvenanceValueDto> CurrentValues { get; set; } = new();

    /// <summary>
    /// How many values the attribute holds, including any beyond the 50 CurrentValues lists.
    /// </summary>
    public int CurrentValueTotalCount { get; set; }

    /// <summary>
    /// The joined Connected System Object the current value came from; null when none can be identified.
    /// </summary>
    public ProvenanceConnectedSystemObjectDto? ContributingConnectedSystemObject { get; set; }

    /// <summary>
    /// The most recent recorded change that set the current value, with its Activity; null when no change is
    /// recorded.
    /// </summary>
    public ProvenanceChangeDto? LastSet { get; set; }

    /// <summary>
    /// Every Attribute Flow that can contribute to the attribute on this Object Type, in priority order, each with
    /// the value it would supply now and how that compares with the value in use.
    /// </summary>
    public List<AttributeSourceCandidateDto> Sources { get; set; } = new();

    /// <summary>
    /// The attribute's change history, newest first: at most 50 entries, HistoryTruncated saying whether there were
    /// more.
    /// </summary>
    public List<AttributeHistoryEntryDto> History { get; set; } = new();

    /// <summary>
    /// Whether History stops short of the attribute's full history.
    /// </summary>
    public bool HistoryTruncated { get; set; }

    public static MetaverseAttributeProvenanceDto FromModel(MetaverseAttributeProvenance model)
    {
        return new MetaverseAttributeProvenanceDto
        {
            MetaverseObjectId = model.MetaverseObjectId,
            MetaverseObjectTypeId = model.MetaverseObjectTypeId,
            AttributeId = model.AttributeId,
            AttributeName = model.AttributeName,
            AttributeType = model.AttributeType,
            AttributePlurality = model.AttributePlurality,
            CurrentValues = model.CurrentValues.Select(ProvenanceValueDto.FromModel).ToList(),
            CurrentValueTotalCount = model.CurrentValueTotalCount,
            ContributingConnectedSystemObject = model.ContributingConnectedSystemObject == null
                ? null
                : ProvenanceConnectedSystemObjectDto.FromModel(model.ContributingConnectedSystemObject),
            LastSet = model.LastSet == null ? null : ProvenanceChangeDto.FromModel(model.LastSet),
            Sources = model.Sources.Select(AttributeSourceCandidateDto.FromModel).ToList(),
            History = model.History.Select(AttributeHistoryEntryDto.FromModel).ToList(),
            HistoryTruncated = model.HistoryTruncated
        };
    }
}

/// <summary>API representation of one current attribute value, formatted for display, with its origin.</summary>
public class ProvenanceValueDto
{
    public string? DisplayValue { get; set; }

    public Guid? ReferenceMetaverseObjectId { get; set; }

    public string? ReferenceTypeName { get; set; }

    public string? ReferenceTypePluralName { get; set; }

    public ValueOriginDto Origin { get; set; } = null!;

    public static ProvenanceValueDto FromModel(ProvenanceValue model)
    {
        return new ProvenanceValueDto
        {
            DisplayValue = model.DisplayValue,
            ReferenceMetaverseObjectId = model.ReferenceMetaverseObjectId,
            ReferenceTypeName = model.ReferenceTypeName,
            ReferenceTypePluralName = model.ReferenceTypePluralName,
            Origin = ValueOriginDto.FromModel(model.Origin)
        };
    }
}

/// <summary>API representation of a Connected System Object named in provenance.</summary>
public class ProvenanceConnectedSystemObjectDto
{
    public Guid Id { get; set; }

    public int ConnectedSystemId { get; set; }

    public string ConnectedSystemName { get; set; } = null!;

    public string TypeName { get; set; } = null!;

    public string? DisplayName { get; set; }

    public string? ExternalId { get; set; }

    public static ProvenanceConnectedSystemObjectDto FromModel(ProvenanceConnectedSystemObject model)
    {
        return new ProvenanceConnectedSystemObjectDto
        {
            Id = model.Id,
            ConnectedSystemId = model.ConnectedSystemId,
            ConnectedSystemName = model.ConnectedSystemName,
            TypeName = model.TypeName,
            DisplayName = model.DisplayName,
            ExternalId = model.ExternalId
        };
    }
}

/// <summary>API representation of the recorded change behind a value.</summary>
public class ProvenanceChangeDto
{
    public DateTime ChangeTime { get; set; }

    public Guid? ActivityId { get; set; }

    public Guid? ActivityRunProfileExecutionItemId { get; set; }

    public string? ActivityDescription { get; set; }

    public string? InitiatedByName { get; set; }

    public MetaverseObjectChangeInitiatorType ChangeInitiatorType { get; set; }

    public static ProvenanceChangeDto FromModel(ProvenanceChange model)
    {
        return new ProvenanceChangeDto
        {
            ChangeTime = model.ChangeTime,
            ActivityId = model.ActivityId,
            ActivityRunProfileExecutionItemId = model.ActivityRunProfileExecutionItemId,
            ActivityDescription = model.ActivityDescription,
            InitiatedByName = model.InitiatedByName,
            ChangeInitiatorType = model.ChangeInitiatorType
        };
    }
}

/// <summary>
/// API representation of one import mapping that can contribute a Metaverse Object attribute, with the value
/// it would supply for this Metaverse Object and its standing against the value in use.
/// </summary>
public class AttributeSourceCandidateDto
{
    public int Rank { get; set; }

    public int MappingId { get; set; }

    public int SyncRuleId { get; set; }

    public string SyncRuleName { get; set; } = null!;

    public int ConnectedSystemId { get; set; }

    public string ConnectedSystemName { get; set; } = null!;

    public bool IsExpression { get; set; }

    /// <summary>
    /// True when the Attribute Flow's Source Type is Generated Value. Its candidate value is the value it
    /// generated for this Metaverse Object, if any.
    /// </summary>
    public bool IsGeneratedValue { get; set; }

    public string? Expression { get; set; }

    public AttributeSourceState State { get; set; }

    public List<string> CandidateValues { get; set; } = new();

    public string? Note { get; set; }

    public static AttributeSourceCandidateDto FromModel(AttributeSourceCandidate model)
    {
        return new AttributeSourceCandidateDto
        {
            Rank = model.Rank,
            MappingId = model.MappingId,
            SyncRuleId = model.SyncRuleId,
            SyncRuleName = model.SyncRuleName,
            ConnectedSystemId = model.ConnectedSystemId,
            ConnectedSystemName = model.ConnectedSystemName,
            IsExpression = model.IsExpression,
            IsGeneratedValue = model.IsGeneratedValue,
            Expression = model.Expression,
            State = model.State,
            CandidateValues = model.CandidateValues,
            Note = model.Note
        };
    }
}

/// <summary>API representation of one entry in an attribute's change history on a Metaverse Object.</summary>
public class AttributeHistoryEntryDto
{
    public AttributeHistoryChangeKind Kind { get; set; }

    public string? Value { get; set; }

    public string? PreviousValue { get; set; }

    public int? SyncRuleId { get; set; }

    public string? SyncRuleName { get; set; }

    /// <summary>
    /// What kind of source set the value at the time: SynchronisationRule, SignInClaim, System or Person. Null when no
    /// source was recorded.
    /// </summary>
    public ValueContributorKind? ContributedByKind { get; set; }

    /// <summary>The sign-in claim that supplied the value, when the source is a sign-in claim; otherwise null.</summary>
    public string? ClaimType { get; set; }

    /// <summary>True when a Generated Value Attribute Flow produced the value, rather than reading it from the Connected System.</summary>
    public bool IsGeneratedValue { get; set; }

    public ProvenanceChangeDto Change { get; set; } = null!;

    public static AttributeHistoryEntryDto FromModel(AttributeHistoryEntry model)
    {
        return new AttributeHistoryEntryDto
        {
            Kind = model.Kind,
            Value = model.Value,
            PreviousValue = model.PreviousValue,
            SyncRuleId = model.SyncRuleId,
            SyncRuleName = model.SyncRuleName,
            ContributedByKind = model.ContributedByKind,
            ClaimType = model.ClaimType,
            IsGeneratedValue = model.IsGeneratedValue,
            Change = ProvenanceChangeDto.FromModel(model.Change)
        };
    }
}
