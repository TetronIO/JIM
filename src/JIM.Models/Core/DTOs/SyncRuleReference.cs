// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// Lightweight reference to a Synchronisation Rule, used in validation error responses
/// to identify which Synchronisation Rules reference a given metaverse attribute.
/// </summary>
public class SyncRuleReference
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
