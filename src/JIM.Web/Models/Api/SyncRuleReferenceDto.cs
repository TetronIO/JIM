// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Web.Models.Api;

/// <summary>
/// A lightweight reference to a Synchronisation Rule, as returned within a Sync Preview response: id plus a
/// name snapshot, so a caller does not need a second lookup to display which rules participated.
/// </summary>
public class SyncRuleReferenceDto
{
    /// <summary>
    /// The Synchronisation Rule's id.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Snapshot of the rule's name at preview time.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public static SyncRuleReferenceDto FromModel(SyncPreviewSyncRuleReference model) => new()
    {
        Id = model.Id,
        Name = model.Name
    };
}
