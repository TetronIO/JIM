// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Web.Models.Api;

/// <summary>
/// One advisory or blocking condition a Sync Preview surfaced. Whether it would block the real synchronisation
/// is positional: a message returned in <see cref="SyncPreviewResponse.Errors"/> would prevent it; one in
/// <see cref="SyncPreviewResponse.Warnings"/> would not.
/// </summary>
public class SyncPreviewMessageDto
{
    /// <summary>
    /// The machine-readable condition code a caller can branch on, without parsing <see cref="Detail"/>.
    /// </summary>
    public SyncPreviewMessageCode Code { get; set; }

    /// <summary>
    /// Human-readable detail of the condition.
    /// </summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// The Synchronisation Rule the condition arose under, when one is attributable.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// Snapshot of the attributed Synchronisation Rule's name.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// The Connected System the condition concerns, when one is attributable.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// The target attribute of the mapping the condition concerns, when one is attributable.
    /// </summary>
    public string? AttributeName { get; set; }

    public static SyncPreviewMessageDto FromModel(SyncPreviewMessage model) => new()
    {
        Code = model.Code,
        Detail = model.Detail,
        SyncRuleId = model.SyncRuleId,
        SyncRuleName = model.SyncRuleName,
        ConnectedSystemId = model.ConnectedSystemId,
        AttributeName = model.AttributeName
    };
}
