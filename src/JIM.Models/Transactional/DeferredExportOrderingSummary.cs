// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// What the export's deferred pass orders its exports by (#2905): how much each export writes, and which exports wait on
/// another export of the pass. Summary-tier: read from the export ids alone, without loading a single export or change.
/// </summary>
public sealed class DeferredExportOrderingSummary
{
    /// <summary>
    /// Per Pending Export of the pass, how many attribute value changes it holds: the work its batch does. An export
    /// absent from the dictionary holds none.
    /// </summary>
    public IReadOnlyDictionary<Guid, int> ChangeCounts { get; init; } = new Dictionary<Guid, int>();

    /// <summary>
    /// The exports of the pass that wait on another export of the pass, one entry per referrer and target.
    /// </summary>
    public IReadOnlyList<DeferredExportDependency> Dependencies { get; init; } = [];
}
