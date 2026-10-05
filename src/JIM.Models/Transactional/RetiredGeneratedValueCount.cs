// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// How many values the retired values register holds for one attribute (Unique Value Generation, #242, Phase 6):
/// what an Attribute Flow list loads once per page to show each generated row's "N retired" chip. Exactly one of
/// the two attribute ids is set.
/// </summary>
public sealed class RetiredGeneratedValueCount
{
    public int? MetaverseAttributeId { get; init; }

    public int? ConnectedSystemObjectTypeAttributeId { get; init; }

    public int Count { get; init; }
}
