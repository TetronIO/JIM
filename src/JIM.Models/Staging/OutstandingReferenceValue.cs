// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// A Connected System Object's reference value still held by its identifier alone: its
/// <see cref="ConnectedSystemObjectAttributeValue.UnresolvedReferenceValue"/> is set and it names no referenced object's id.
/// What a Full Import's end-of-run reference sweep reads of the values it left outstanding as it saved each unit (#2521).
/// </summary>
/// <param name="AttributeValueId">The value's id.</param>
/// <param name="ConnectedSystemObjectId">The object holding the value.</param>
/// <param name="ConnectedSystemObjectTypeId">The holding object's Object Type.</param>
/// <param name="AttributeId">The Reference attribute the value belongs to.</param>
/// <param name="UnresolvedReferenceValue">The identifier the value names its object by, as the Connector gave it.</param>
public sealed record OutstandingReferenceValue(
    Guid AttributeValueId,
    Guid ConnectedSystemObjectId,
    int ConnectedSystemObjectTypeId,
    int AttributeId,
    string UnresolvedReferenceValue);
