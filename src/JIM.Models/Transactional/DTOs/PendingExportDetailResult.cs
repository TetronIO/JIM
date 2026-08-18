// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// Result object for the Pending Export detail page. Contains the Pending Export
/// with capped MVA attribute changes and per-attribute total counts.
/// </summary>
public class PendingExportDetailResult
{
    public PendingExport PendingExport { get; set; } = null!;

    /// <summary>
    /// Per-attribute total change counts. Only populated when the detail page
    /// uses capped MVA loading. Key is the attribute name; value is the total
    /// count of changes in the database for that attribute.
    /// </summary>
    public Dictionary<string, int> AttributeChangeTotalCounts { get; set; } = new();

    /// <summary>
    /// The reference changes among the loaded attribute value changes that have not been written
    /// yet, each with the reason (issue #1398). Empty when the Pending Export has no unresolved
    /// references. Covers the loaded (capped) changes only; a multi-valued attribute beyond the cap
    /// is paged through the attribute-changes endpoint.
    /// </summary>
    public List<PendingExportUnresolvedReference> UnresolvedReferences { get; set; } = new();
}
