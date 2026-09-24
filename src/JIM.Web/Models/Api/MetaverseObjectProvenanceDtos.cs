// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of the origin of every attribute value on a Metaverse Object (#399).
/// </summary>
public class MetaverseObjectProvenanceDto
{
    public Guid MetaverseObjectId { get; set; }

    public List<MetaverseAttributeOriginSummaryDto> Attributes { get; set; } = new();

    public static MetaverseObjectProvenanceDto FromModel(MetaverseObjectProvenance model)
    {
        return new MetaverseObjectProvenanceDto
        {
            MetaverseObjectId = model.MetaverseObjectId,
            Attributes = model.Attributes.Select(MetaverseAttributeOriginSummaryDto.FromModel).ToList()
        };
    }
}

/// <summary>
/// API representation of the distinct origins of one attribute's values on a Metaverse Object.
/// </summary>
public class MetaverseAttributeOriginSummaryDto
{
    public int AttributeId { get; set; }

    public string AttributeName { get; set; } = null!;

    public List<ValueOriginDto> Origins { get; set; } = new();

    public static MetaverseAttributeOriginSummaryDto FromModel(MetaverseAttributeOriginSummary model)
    {
        return new MetaverseAttributeOriginSummaryDto
        {
            AttributeId = model.AttributeId,
            AttributeName = model.AttributeName,
            Origins = model.Origins.Select(ValueOriginDto.FromModel).ToList()
        };
    }
}

/// <summary>
/// API representation of a Metaverse Object attribute value's origin: a Connected System through a
/// Synchronisation Rule, JIM itself, a person, or not recorded.
/// </summary>
public class ValueOriginDto
{
    public ValueOriginKind Kind { get; set; }

    public int? ConnectedSystemId { get; set; }

    public string? ConnectedSystemName { get; set; }

    public int? SyncRuleId { get; set; }

    public string? SyncRuleName { get; set; }

    public bool SyncRuleDeleted { get; set; }

    public bool AssertsNoValue { get; set; }

    public bool Corrected { get; set; }

    public Guid? PersonId { get; set; }

    public string? PersonName { get; set; }

    public static ValueOriginDto FromModel(ValueOrigin model)
    {
        return new ValueOriginDto
        {
            Kind = model.Kind,
            ConnectedSystemId = model.ConnectedSystemId,
            ConnectedSystemName = model.ConnectedSystemName,
            SyncRuleId = model.SyncRuleId,
            SyncRuleName = model.SyncRuleName,
            SyncRuleDeleted = model.SyncRuleDeleted,
            AssertsNoValue = model.AssertsNoValue,
            Corrected = model.Corrected,
            PersonId = model.PersonId,
            PersonName = model.PersonName
        };
    }
}
