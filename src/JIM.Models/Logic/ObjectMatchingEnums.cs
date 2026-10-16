// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// What an Object Matching Rule computed for one object (#2052).
/// </summary>
public enum MatchValueResolutionKind
{
    /// <summary>One or more values to look up.</summary>
    Values,

    /// <summary>Nothing to look up, so the next rule is tried. Not an error.</summary>
    NoValue,

    /// <summary>The rule could not be worked out for this object, so the object is stopped with an error.</summary>
    Failed
}

/// <summary>
/// What an Object Matching Rule's lookup against the Metaverse found for a tested object (#2055), as an import join
/// would decide it.
/// </summary>
public enum ObjectMatchingLookupOutcome
{
    /// <summary>The values identify one Metaverse Object, and a synchronisation would join the object to it.</summary>
    WouldJoin,

    /// <summary>The values identify the Metaverse Object the tested object is already joined to.</summary>
    AlreadyJoined,

    /// <summary>
    /// The values identify one Metaverse Object, but another object from this Connected System is already joined to
    /// it, so a synchronisation would record that it could not join.
    /// </summary>
    JoinedToAnother,

    /// <summary>The values identify no Metaverse Object; a synchronisation would try the next rule.</summary>
    NoMatch,

    /// <summary>The values identify more than one Metaverse Object, so a synchronisation would neither join nor project.</summary>
    Ambiguous
}
