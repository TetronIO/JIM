// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// The coarse grouping of <see cref="ActivityTargetType"/> values used by the Activities list quick-filter, so a
/// reviewer can isolate, for example, all configuration changes without picking individual target types.
/// </summary>
public enum ActivityTargetCategory
{
    /// <summary>Changes to configuration objects: Connected Systems, Synchronisation Rules, Schedules, schema, settings.</summary>
    Configuration = 0,
    /// <summary>Changes to identity data: Metaverse Objects.</summary>
    IdentityData = 1,
    /// <summary>Run Profile executions: imports, synchronisations, exports.</summary>
    SyncRuns = 2,
    /// <summary>System-level operations: housekeeping, resets, example data generation.</summary>
    System = 3
}
