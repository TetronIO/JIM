// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of everything the attribute inspector shows for one attribute on one Metaverse Object
/// (#399): current values and origin, the contributing Connected System Object, the change that last set the
/// value, every contributing source in priority order, and the attribute's history.
/// </summary>
public class MetaverseAttributeProvenanceDto
{
    public Guid MetaverseObjectId { get; set; }

    public int MetaverseObjectTypeId { get; set; }

    public int AttributeId { get; set; }

    public string AttributeName { get; set; } = null!;

    public AttributeDataType AttributeType { get; set; }

    public AttributePlurality AttributePlurality { get; set; }

    public List<ProvenanceValueDto> CurrentValues { get; set; } = new();

    public int CurrentValueTotalCount { get; set; }

    public ProvenanceConnectedSystemObjectDto? ContributingConnectedSystemObject { get; set; }

    public ProvenanceChangeDto? LastSet { get; set; }

    public List<AttributeSourceCandidateDto> Sources { get; set; } = new();

    public List<AttributeHistoryEntryDto> History { get; set; } = new();

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

    public ValueOriginDto Origin { get; set; } = null!;

    public static ProvenanceValueDto FromModel(ProvenanceValue model)
    {
        return new ProvenanceValueDto
        {
            DisplayValue = model.DisplayValue,
            ReferenceMetaverseObjectId = model.ReferenceMetaverseObjectId,
            ReferenceTypeName = model.ReferenceTypeName,
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
            Change = ProvenanceChangeDto.FromModel(model.Change)
        };
    }
}
