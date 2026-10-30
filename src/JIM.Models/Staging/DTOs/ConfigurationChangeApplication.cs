// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// Where the configuration changes a synchronisation run claimed stand (#2615), as the run's Activity page reports them.
/// </summary>
public enum ConfigurationChangeApplication
{
    /// <summary>
    /// The run completed and applied them. No later synchronisation will process the type in full because of them.
    /// </summary>
    Applied,

    /// <summary>
    /// The run failed or was cancelled before it applied them. They stay awaiting synchronisation, and the next run claims
    /// them again.
    /// </summary>
    NotApplied,

    /// <summary>
    /// The run has not finished, so the changes are neither applied nor abandoned yet.
    /// </summary>
    InProgress
}
