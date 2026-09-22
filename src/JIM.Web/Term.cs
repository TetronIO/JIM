// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web;

/// <summary>
/// A JIM term that <c>TermHint</c> can explain in-portal (#1670): one entry per glossary term that an
/// administrator meets on a configuration page before they have any reason to already know it. Not every
/// glossary entry needs one here; only the terms a portal page currently explains inline via
/// <see cref="TermDefinitions"/>.
/// </summary>
public enum Term
{
    /// <summary>
    /// The central authoritative identity repository within JIM.
    /// </summary>
    Metaverse,

    /// <summary>
    /// The staging area where Connected System Objects reside before and after synchronisation.
    /// </summary>
    ConnectorSpace,

    /// <summary>
    /// Creating a new Metaverse Object when no existing match is found for an incoming Connected System Object.
    /// </summary>
    Projection,

    /// <summary>
    /// Linking a Connected System Object to an existing Metaverse Object.
    /// </summary>
    Join,

    /// <summary>
    /// A rule that maps an attribute between a Connected System Object and a Metaverse Object.
    /// </summary>
    AttributeFlow,

    /// <summary>
    /// A queued change waiting to be sent to a target system.
    /// </summary>
    PendingExport,

    /// <summary>
    /// A rule that decides whether an incoming Connected System Object corresponds to an existing identity.
    /// </summary>
    ObjectMatchingRule,

    /// <summary>
    /// The Scoping Criteria that decide which objects a Synchronisation Rule applies to.
    /// </summary>
    Scoping,

    /// <summary>
    /// How far beneath a selected Container objects are imported from.
    /// </summary>
    ContainerScope,

    /// <summary>
    /// The deterministic precedence that decides which Connected System's value wins when several contribute
    /// the same Metaverse Object attribute.
    /// </summary>
    AttributePriority
}
