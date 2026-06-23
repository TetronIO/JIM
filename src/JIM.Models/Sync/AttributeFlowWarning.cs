// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// Records a warning generated during inbound Attribute Flow when a multi-valued
/// source attribute was mapped to a single-valued target attribute. The first value
/// was selected and all others were discarded.
/// </summary>
public class AttributeFlowWarning
{
    /// <summary>
    /// The name of the source Connected System attribute.
    /// </summary>
    public required string SourceAttributeName { get; set; }

    /// <summary>
    /// The name of the target metaverse attribute.
    /// </summary>
    public required string TargetAttributeName { get; set; }

    /// <summary>
    /// The total number of values present on the source attribute.
    /// </summary>
    public int ValueCount { get; set; }

    /// <summary>
    /// A string representation of the value that was selected (the first value).
    /// </summary>
    public required string SelectedValue { get; set; }
}
