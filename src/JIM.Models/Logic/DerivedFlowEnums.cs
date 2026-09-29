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
