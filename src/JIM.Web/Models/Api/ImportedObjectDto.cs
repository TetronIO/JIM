// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Enums;

namespace JIM.Web.Models.Api;

/// <summary>
/// What the Connector returned for an imported object that JIM rejected before creating a Connected System Object for
/// it: the object's attributes and values, as recorded when it was rejected. Values of attributes that hold or may hold
/// credentials are never recorded.
/// </summary>
/// <remarks>#874. Built from <c>ImportedObjectSnapshot</c>.</remarks>
public class ImportedObjectDto
{
    /// <summary>The Object Type the Connector named, as it named it; null where it named none or the run limit was reached.</summary>
    public string? ObjectType { get; set; }

    /// <summary>The kind of change the Connector reported; null where the run limit was reached. Serialised as the enum member name.</summary>
    public ObjectChangeType? ChangeType { get; set; }

    /// <summary>The attributes recorded, in the order the Connector returned them. Empty where the run limit was reached.</summary>
    public List<ImportedObjectAttributeDto> Attributes { get; set; } = [];

    /// <summary>How many attributes were left out to keep the record within its size limit (32 KB); null when none were.</summary>
    public int? OmittedAttributeCount { get; set; }

    /// <summary>The names of the attributes left out, up to the first 100; null when none were left out.</summary>
    public List<string>? OmittedAttributeNames { get; set; }

    /// <summary>
    /// True when the object was not recorded because its run had already recorded the most rejected objects one run
    /// keeps (1,000); the attributes are then empty.
    /// </summary>
    public bool RunLimitReached { get; set; }

    /// <summary>Creates the DTO from the snapshot recorded on the item; null when there is none.</summary>
    public static ImportedObjectDto? FromModel(ImportedObjectSnapshot? model) => model == null ? null : new()
    {
        ObjectType = model.ObjectType,
        ChangeType = model.ChangeType,
        Attributes = model.Attributes.Select(ImportedObjectAttributeDto.FromModel).ToList(),
        OmittedAttributeCount = model.OmittedAttributeCount,
        OmittedAttributeNames = model.OmittedAttributeNames,
        RunLimitReached = model.RunLimitReached == true
    };
}
