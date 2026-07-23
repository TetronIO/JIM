// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// Records an error raised during Attribute Flow when a multi-valued source attribute holds more than
/// one value but the target attribute is single-valued. A single-valued target can hold only one value,
/// and JIM will not pick one arbitrarily, so no value is flowed for the attribute; the object's other
/// attributes still synchronise. Surfaced to the administrator as a
/// <c>MultiValuedToSingleValued</c> Run Profile Execution Item error.
/// </summary>
public class AttributeFlowError
{
    /// <summary>
    /// The name of the source attribute (the Connected System attribute on import, the Metaverse
    /// attribute on export).
    /// </summary>
    public required string SourceAttributeName { get; set; }

    /// <summary>
    /// The name of the target attribute (the Metaverse attribute on import, the Connected System
    /// attribute on export).
    /// </summary>
    public required string TargetAttributeName { get; set; }

    /// <summary>
    /// The number of distinct values present on the source attribute (after inbound value processing
    /// and de-duplication for text values).
    /// </summary>
    public int ValueCount { get; set; }
}
