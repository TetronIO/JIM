// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Expressions;

/// <summary>
/// Which side of the Metaverse an Expression reads an input from.
/// </summary>
public enum ExpressionInputSource
{
    /// <summary>
    /// A Metaverse Object attribute, written as mv["Attribute Name"].
    /// </summary>
    Metaverse,

    /// <summary>
    /// A Connected System Object attribute, written as cs["Attribute Name"].
    /// </summary>
    ConnectedSystem
}

/// <summary>
/// What an Expression-based Attribute Flow does when an attribute it reads has no value on the object being
/// synchronised.
/// </summary>
/// <remarks>
/// The same absent input is a data-corruption incident for one Expression and entirely routine for the next. An
/// Expression built on <c>IIF</c> or <c>IsNullOrEmpty</c> handles the absence itself and must be left alone; a
/// concatenation building a Distinguished Name produces a syntactically valid string that no layer downstream can
/// tell from a good one. This is therefore a per-mapping choice rather than a rule JIM can make (#1361).
/// </remarks>
public enum MissingInputBehaviour
{
    /// <summary>
    /// Evaluate the Expression with the input absent and contribute whatever it returns. The behaviour JIM has
    /// always had, and the default, so no existing mapping changes on upgrade.
    /// </summary>
    EvaluateAnyway,

    /// <summary>
    /// Do not evaluate, and treat that as a legitimate outcome rather than a fault: the mapping contributes
    /// nothing, resolved by Attribute Priority and "Null is a value" like any other no-value outcome. Nothing is
    /// reported as an error.
    /// </summary>
    ContributeNoValue,

    /// <summary>
    /// Do not evaluate, and record it as an error against the object. Every other Attribute Flow on the
    /// Synchronisation Rule still runs, and the target attribute keeps whatever it already held.
    /// </summary>
    FailMapping,

    /// <summary>
    /// Do not evaluate anything for this object: no Attribute Flow on this Synchronisation Rule runs, nothing
    /// flows and nothing is exported. For an input feeding identity-critical output, where a partially populated
    /// object is worse than none.
    /// </summary>
    FailObject
}
