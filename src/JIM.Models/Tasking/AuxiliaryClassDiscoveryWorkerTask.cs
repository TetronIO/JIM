// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Tasking;

/// <summary>
/// Hands a Connected System's auxiliary class usage discovery to JIM.Worker.
/// </summary>
/// <remarks>
/// This is a read of a Connected System's objects, potentially all of them, so it belongs nowhere near a web
/// request. It changes no schema and no configuration: what it finds is recorded as suggestions that narrow what
/// the portal offers an administrator, who decides what JIM actually manages.
/// </remarks>
public class AuxiliaryClassDiscoveryWorkerTask : WorkerTask
{
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// How much of the Connected System to read.
    /// </summary>
    public AuxiliaryClassDiscoveryScope Scope { get; set; } = AuxiliaryClassDiscoveryScope.NotSet;

    /// <summary>
    /// For a quick sample, how many objects of each selected Object Type to read before moving on. Ignored by a
    /// full scan, which reads every object.
    /// </summary>
    public int? SampleSizePerObjectType { get; set; }
}
