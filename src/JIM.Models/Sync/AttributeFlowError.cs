// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// Why a single Attribute Flow mapping produced no value while the object's other attributes carried on.
/// </summary>
/// <remarks>
/// Failing one mapping and continuing is an established shape in JIM, and this enum is what lets the one channel
/// carry more than one reason for it. Each member corresponds to an
/// <c>ActivityRunProfileExecutionItemErrorType</c> the worker records.
/// </remarks>
public enum AttributeFlowErrorKind
{
    /// <summary>
    /// A multi-valued source attribute held more than one value but the target attribute is single-valued.
    /// </summary>
    MultiValuedToSingleValued,

    /// <summary>
    /// An Expression read an attribute the object has no value for, and the mapping's Missing Input Behaviour is
    /// <c>FailMapping</c>.
    /// </summary>
    ExpressionMissingInput
}

/// <summary>
/// Records an error raised during Attribute Flow that costs the object one attribute rather than the whole
/// object: no value is flowed for the mapping (import) or no Pending Export is generated for it (export), and the
/// object's other attributes still synchronise. Surfaced to the administrator as a Run Profile Execution Item
/// error of the type <see cref="Kind"/> names.
/// </summary>
public class AttributeFlowError
{
    /// <summary>
    /// Why the mapping produced no value, which decides the error type and message the worker records.
    /// </summary>
    public required AttributeFlowErrorKind Kind { get; set; }

    /// <summary>
    /// The name of the source attribute (the Connected System attribute on import, the Metaverse attribute on
    /// export). Null where the source is an Expression rather than an attribute.
    /// </summary>
    public string? SourceAttributeName { get; set; }

    /// <summary>
    /// The name of the target attribute (the Metaverse attribute on import, the Connected System attribute on
    /// export).
    /// </summary>
    public required string TargetAttributeName { get; set; }

    /// <summary>
    /// The number of distinct values present on the source attribute (after inbound value processing and
    /// de-duplication for text values). Only meaningful for
    /// <see cref="AttributeFlowErrorKind.MultiValuedToSingleValued"/>.
    /// </summary>
    public int ValueCount { get; set; }

    /// <summary>
    /// The Expression that was not evaluated. Only populated for
    /// <see cref="AttributeFlowErrorKind.ExpressionMissingInput"/>.
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// The inputs the object had no value for, as the Expression addresses them (for example
    /// <c>cs["lastName"]</c>). Only populated for <see cref="AttributeFlowErrorKind.ExpressionMissingInput"/>.
    /// </summary>
    public IReadOnlyList<string> MissingInputs { get; set; } = Array.Empty<string>();
}
