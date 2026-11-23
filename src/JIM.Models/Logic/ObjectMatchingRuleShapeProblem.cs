// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// What is wrong with the shape of an Object Matching Rule, before the types it compares are looked at.
/// </summary>
public enum ObjectMatchingRuleShapeProblem
{
    /// <summary>The shape is sound.</summary>
    None,

    /// <summary>The rule has no source.</summary>
    NoSource,

    /// <summary>The rule has more than one source.</summary>
    SeveralSources,

    /// <summary>The source holds both a Connected System attribute and an expression.</summary>
    AttributeAndExpression,

    /// <summary>The source holds neither a Connected System attribute nor an expression.</summary>
    NeitherAttributeNorExpression,

    /// <summary>The rule names no Metaverse Attribute to compare against.</summary>
    NoTarget
}
