// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Expressions;

/// <summary>
/// The value processing step of the Attribute Flow tester's trace (#2055): which transforms an import Attribute Flow to
/// a Text attribute applies to the Expression's result, and whether they changed it.
/// </summary>
public sealed class AttributeFlowValueProcessing
{
    /// <summary>
    /// The transforms that apply, in the order synchronisation applies them. Empty when none is set.
    /// </summary>
    public required IReadOnlyList<AttributeFlowValueProcessingStep> Steps { get; init; }

    /// <summary>
    /// Whether the transforms changed any value, or made one no value.
    /// </summary>
    public required bool Changed { get; init; }
}
