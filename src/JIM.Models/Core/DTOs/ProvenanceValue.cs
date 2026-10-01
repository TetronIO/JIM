// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// One current attribute value, formatted for display, with its origin.
/// </summary>
public class ProvenanceValue
{
    /// <summary>The value as display text; null for an asserted-null row.</summary>
    public string? DisplayValue { get; set; }

    /// <summary>For a reference value, the referenced Metaverse Object.</summary>
    public Guid? ReferenceMetaverseObjectId { get; set; }

    public string? ReferenceTypeName { get; set; }

    public ValueOrigin Origin { get; set; } = ValueOrigin.NotRecorded;
}
