// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// A change a schema refresh applied to the definition of an attribute JIM already knew: the Connector restated
/// its data type or its plurality. These were previously applied silently, which is exactly the kind of drift a
/// refresh must report; a data type or plurality change can invalidate an Attribute Flow mapping that was
/// validated against the old definition.
/// </summary>
public class SchemaAttributeDefinitionChange
{
    /// <summary>
    /// The name of the attribute whose definition changed.
    /// </summary>
    public required string AttributeName { get; set; }

    /// <summary>
    /// Which aspect of the definition changed.
    /// </summary>
    public required SchemaAttributeChangeAspect Aspect { get; set; }

    /// <summary>
    /// The value JIM held before the refresh, as the enum member's name (e.g. "Text", "MultiValued").
    /// </summary>
    public required string OldValue { get; set; }

    /// <summary>
    /// The value the Connector reported, as the enum member's name.
    /// </summary>
    public required string NewValue { get; set; }
}

/// <summary>
/// The aspect of an attribute's definition a schema refresh changed.
/// </summary>
public enum SchemaAttributeChangeAspect
{
    /// <summary>
    /// The attribute's data type was restated by the Connector (only applied when the administrator has not
    /// overridden the type; an override is never overwritten by a refresh).
    /// </summary>
    DataType = 0,

    /// <summary>
    /// The attribute's plurality (single-valued or multi-valued) was restated by the Connector.
    /// </summary>
    Plurality = 1
}
