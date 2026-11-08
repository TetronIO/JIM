// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What Drift Detection asks the database about a multi-valued reference attribute a Delta Synchronisation loaded without
/// its values (#2818, <see cref="SynchronisationPartiallyLoadedAttribute"/>): which of the Metaverse Objects the
/// Connected System Object's values resolve to are not expected, and which expected ones none of them resolves to.
/// </summary>
/// <param name="ConnectedSystemObjectId">The Connected System Object.</param>
/// <param name="AttributeId">Its attribute, the export mapping's target.</param>
/// <param name="SourceMetaverseAttributeId">The Metaverse attribute the export mapping reads, which the expected values
/// are of; one attribute can be the target of mappings reading different ones.</param>
/// <param name="ExpectedValues">The Metaverse Object ids the attribute is expected to hold, each once, in the order the
/// Metaverse Object holds them: read from the Metaverse Object in memory, as Drift Detection reads it.</param>
public sealed record DriftHeldReferenceQuestion(
    Guid ConnectedSystemObjectId,
    int AttributeId,
    int SourceMetaverseAttributeId,
    IReadOnlyList<Guid> ExpectedValues);
