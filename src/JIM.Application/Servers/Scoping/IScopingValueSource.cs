// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
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
    /// The value scoping compares for the attribute: the object's first value for it (#1923). False when the object
    /// holds none.
    /// </summary>
    bool TryGetFirstValue(int attributeId, out ScopingValue value);

    /// <summary>How many values the object holds for the attribute. Explanations only; synchronisation never asks.</summary>
    int CountValues(int attributeId);
}
