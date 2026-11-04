// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// Optional body for the hierarchy import endpoint. Omitting it imports with <c>runProfileAction</c> set to
/// <c>Disable</c>.
/// </summary>
public class ImportConnectedSystemHierarchyRequest
{
    /// <summary>
    /// What to do when the refresh would remove a partition that Run Profiles target. <c>Disable</c>, the default,
    /// applies the refresh and disables each such Run Profile, recording the partition's name; the response's
    /// <c>affectedRunProfiles</c> names each one, and enabling one again needs a partition or all selected partitions.
    /// <c>Cancel</c> applies nothing from the refresh when any Run Profile targets a partition it would remove, and the
    /// response is a 409 naming them; a refresh that removes no such partition is applied either way.
    /// </summary>
    public RemovedPartitionRunProfileAction RunProfileAction { get; set; } = RemovedPartitionRunProfileAction.Disable;
}
