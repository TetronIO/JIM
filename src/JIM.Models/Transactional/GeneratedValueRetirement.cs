// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// One value a write to the retired values register actually retired (Unique Value Generation, #242, Phase 6):
/// what a repository method that deletes assignments hands back, so the synchronisation that caused it can record a
/// <c>GeneratedValueRetired</c> outcome naming the attribute and the value. A value that was already retired, or
/// whose flow had "Never reuse a value" off, is not reported, because nothing was written for it.
/// </summary>
public sealed class GeneratedValueRetirement
{
    /// <summary>The Metaverse attribute, for an import flow; null for an export flow.</summary>
    public int? MetaverseAttributeId { get; init; }

    /// <summary>The Connected System Object Type attribute, for an export flow; null for an import flow.</summary>
    public int? ConnectedSystemObjectTypeAttributeId { get; init; }

    /// <summary>The attribute's display name.</summary>
    public string AttributeName { get; init; } = string.Empty;

    /// <summary>The value as it was issued.</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>Why it was retired.</summary>
    public RetiredGeneratedValueReason Reason { get; init; }

    /// <summary>The object that held it: a Metaverse Object id (import) or Connected System Object id (export).</summary>
    public Guid? FromObjectId { get; init; }
}
