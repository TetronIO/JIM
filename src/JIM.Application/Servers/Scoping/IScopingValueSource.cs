// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;

namespace JIM.Application.Servers.Scoping;

/// <summary>
/// The object a scoping evaluation reads: a Metaverse Object for an export rule, a Connected System Object for an
/// import rule. Implemented by structs and consumed through a generic constraint, so the evaluator calls them without
/// boxing or interface dispatch on synchronisation's hot path.
/// </summary>
internal interface IScopingValueSource
{
    /// <summary>
    /// The attribute a criterion compares on this side: its Metaverse Attribute for an export rule, its Connected
    /// System attribute for an import rule. False when the criterion references none.
    /// </summary>
    bool TryGetAttribute(SyncRuleScopingCriteria criterion, out int attributeId, out AttributeDataType type, out string name);

    /// <summary>
    /// The object's next value for the attribute at or after <paramref name="position"/>, advancing
    /// <paramref name="position"/> past it; false once there are no more. Start at zero. Scoping compares every value
    /// an attribute holds (#1923), and walking them this way allocates nothing.
    /// </summary>
    bool TryGetNextValue(int attributeId, ref int position, out ScopingValue value);

    /// <summary>How many values the object holds for the attribute. Explanations only; synchronisation never asks.</summary>
    int CountValues(int attributeId);

    /// <summary>
    /// The context an expression criterion is evaluated in (#2054): the object's attributes as <c>cs["..."]</c> on an
    /// import rule or <c>mv["..."]</c> on an export rule, read exactly as Attribute Flow reads them.
    /// </summary>
    ExpressionContext ExpressionContext(DateTime nowUtc);

    /// <summary>The side an expression criterion reads on this object: <c>cs["..."]</c> or <c>mv["..."]</c>.</summary>
    ExpressionInputSource ExpressionSide { get; }

    /// <summary>
    /// Whether the scoped object type has an attribute of this name (ignoring case, as expressions do); null when no
    /// schema is available to say. An expression input naming no attribute must not read as "no value" (#2054): a
    /// renamed or removed attribute would then decide scope on a name that no longer means anything.
    /// </summary>
    bool? IsInSchema(string attributeName);

    /// <summary>
    /// The type of the scoped object type's attribute of this name (ignoring case); null when it has none, or no schema
    /// is available. Explanations use it to decide whether an expression input's values may be shown.
    /// </summary>
    AttributeDataType? SchemaType(string attributeName);
}
