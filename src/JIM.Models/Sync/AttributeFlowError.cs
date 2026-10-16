// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

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
    ExpressionMissingInput,

    /// <summary>
    /// A generated mapping's (Unique Value Generation, #242) base expression evaluated to an array or other
    /// multi-valued result. A generated value's base must be a single text value, since a uniqueness token is
    /// appended to exactly one candidate; nothing is recorded for the attribute this pass.
    /// </summary>
    GeneratedBaseNotSingleValue,

    /// <summary>
    /// An Expression's result held a value the target attribute's type cannot hold, such as text that is not a
    /// number for a Number attribute (#2179). Nothing flows, so the target keeps its value rather than being cleared.
    /// Recorded as an <c>ExpressionEvaluationError</c>.
    /// </summary>
    ExpressionResultNotConvertible,

    /// <summary>
    /// An Expression's result held more than one distinct value for a single-valued target attribute (#2179). JIM
    /// will not pick one, so nothing flows and the target keeps its value. Recorded as
    /// <c>MultiValuedToSingleValued</c>, the type of the same fault from an attribute source.
    /// </summary>
    ExpressionResultMultiValued,

    /// <summary>
    /// Two or more of the dates an export sends to a multi-valued attribute are the same date at the step of time the
    /// Connected System holds the attribute to (#2153, decision D2): two times in one second, for a directory that holds
    /// whole seconds. The Connected System can hold them only as one, so it is sent them as one, and this reports that.
    /// Unlike every other kind, the attribute still exports; only the merging is reported. Recorded as
    /// <c>DatesCollapseAtTargetResolution</c>.
    /// </summary>
    DatesCollapseAtTargetResolution
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
    /// export). For <see cref="AttributeFlowErrorKind.ExpressionResultMultiValued"/>, the attribute whose values the
    /// Expression returned, where they all came from one (#2053); otherwise null where the source is an Expression.
    /// </summary>
    public string? SourceAttributeName { get; set; }

    /// <summary>
    /// The name of the target attribute (the Metaverse attribute on import, the Connected System attribute on
    /// export).
    /// </summary>
    public required string TargetAttributeName { get; set; }

    /// <summary>
    /// The number of distinct values present on the source attribute (after inbound value processing and
    /// de-duplication for text values), or in the Expression's result. Only meaningful for
    /// <see cref="AttributeFlowErrorKind.MultiValuedToSingleValued"/> and
    /// <see cref="AttributeFlowErrorKind.ExpressionResultMultiValued"/>.
    /// </summary>
    public int ValueCount { get; set; }

    /// <summary>
    /// The Expression that was not evaluated, or whose result could not flow: it produced more than one value
    /// (<see cref="AttributeFlowErrorKind.GeneratedBaseNotSingleValue"/>,
    /// <see cref="AttributeFlowErrorKind.ExpressionResultMultiValued"/>) or a value the target cannot hold
    /// (<see cref="AttributeFlowErrorKind.ExpressionResultNotConvertible"/>). Null for
    /// <see cref="AttributeFlowErrorKind.MultiValuedToSingleValued"/>, whose source is an attribute.
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// The type of the target attribute, which the Expression's result could not be converted to. Only populated for
    /// <see cref="AttributeFlowErrorKind.ExpressionResultNotConvertible"/>.
    /// </summary>
    public AttributeDataType? TargetAttributeType { get; set; }

    /// <summary>
    /// The value the Expression's result held that the target attribute's type cannot hold, as text. Only populated
    /// for <see cref="AttributeFlowErrorKind.ExpressionResultNotConvertible"/>.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// The inputs the object had no value for, as the Expression addresses them (for example
    /// <c>cs["lastName"]</c>). Only populated for <see cref="AttributeFlowErrorKind.ExpressionMissingInput"/>.
    /// </summary>
    public IReadOnlyList<string> MissingInputs { get; set; } = Array.Empty<string>();

    /// <summary>
    /// The Synchronisation Rule hosting the mapping, when the mapping is a Metaverse-Derived Attribute Flow (#1750):
    /// a derived flow is evaluated after the object's ordinary flows, from whichever of the object's rules hosts it,
    /// so the error names the rule an administrator has to open. Null for an ordinary mapping.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// The step of time the target attribute holds dates to. Only populated for
    /// <see cref="AttributeFlowErrorKind.DatesCollapseAtTargetResolution"/>.
    /// </summary>
    public JIM.Models.Staging.DateTimeResolution? TargetDateTimeResolution { get; set; }

    /// <summary>
    /// How many dates the target attribute holds once its values are taken to its resolution: fewer than
    /// <see cref="ValueCount"/>. Only populated for <see cref="AttributeFlowErrorKind.DatesCollapseAtTargetResolution"/>.
    /// </summary>
    public int TargetValueCount { get; set; }
}
