// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// Which import mappings a Metaverse-Derived Attribute Flow dependency graph is built from (#1750, plan decision 2).
/// </summary>
public enum DerivedFlowGraphScope
{
    /// <summary>
    /// Every import mapping, disabled mappings and mappings of disabled Synchronisation Rules included. Save-time
    /// validation uses this, so enabling a flow later can never introduce a dependency cycle.
    /// </summary>
    AllMappings = 0,

    /// <summary>
    /// Enabled mappings of enabled Synchronisation Rules only: what synchronisation actually evaluates.
    /// </summary>
    EnabledMappingsOnly = 1
}

/// <summary>
/// What a read-only analysis of a proposed import Attribute Flow found it to be (#1750).
/// </summary>
public enum DerivedFlowAnalysisStatus
{
    /// <summary>
    /// Nothing to analyse: the rule is an export rule, or the mapping has no target Metaverse attribute yet.
    /// </summary>
    NotApplicable = 0,

    /// <summary>
    /// An import Attribute Flow whose expression reads no <c>mv["..."]</c> attribute: evaluated in the ordinary pass.
    /// </summary>
    NotDerived = 1,

    /// <summary>
    /// An import Attribute Flow whose expression reads at least one <c>mv["..."]</c> attribute: a derived flow.
    /// </summary>
    Derived = 2
}
