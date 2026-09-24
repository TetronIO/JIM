// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// Where a Metaverse Object attribute value came from (#399). Every value has exactly one origin.
/// </summary>
public enum ValueOriginKind
{
    /// <summary>No contributor is recorded for the value (it pre-dates provenance, or was written internally).</summary>
    NotRecorded = 0,

    /// <summary>A Connected System contributed the value through an import Synchronisation Rule.</summary>
    SynchronisationRule = 1,

    /// <summary>JIM generated the value (Unique Value Generation, #242). Not produced until #242 lands.</summary>
    GeneratedByJim = 2,

    /// <summary>A person set the value directly (internal Metaverse Object management, #614). Not produced until #614 lands.</summary>
    SetByPerson = 3
}

/// <summary>
/// The standing of one contributing source for a Metaverse Object attribute, relative to the value in use.
/// </summary>
public enum AttributeSourceState
{
    /// <summary>This source supplied the value currently held.</summary>
    InUse = 0,

    /// <summary>This source has a value but a higher-priority source won.</summary>
    Outranked = 1,

    /// <summary>This source is joined but supplies no value for the attribute.</summary>
    NoValue = 2,

    /// <summary>The Metaverse Object has no joined Connected System Object in this source's Connected System.</summary>
    NotJoined = 3,

    /// <summary>The mapping or its Synchronisation Rule is disabled.</summary>
    Disabled = 4,

    /// <summary>The candidate value could not be determined (for example a source type that cannot be evaluated out of a run).</summary>
    NotEvaluated = 5
}

/// <summary>
/// The kind of change one attribute history entry records.
/// </summary>
public enum AttributeHistoryChangeKind
{
    Added = 0,

    /// <summary>A single-valued attribute's value was replaced (a Remove and an Add in the same change).</summary>
    Set = 1,

    Removed = 2
}
