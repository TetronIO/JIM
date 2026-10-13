// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Transactional;

/// <summary>
/// A successfully exported Pending Export attribute change that optimistic export apply could not record on its
/// Connected System Object (#2183), because its value is held in another data type's column than its attribute's.
/// Export staging converts expression results to the attribute's type (#2179), so only a fault elsewhere stages one.
/// The Connector wrote it, but a value built from the attribute's own column would hold nothing.
/// </summary>
/// <param name="PendingExportId">The Pending Export the change belongs to.</param>
/// <param name="ConnectedSystemObjectId">The Connected System Object it was exported for.</param>
/// <param name="AttributeName">The attribute's name.</param>
/// <param name="AttributeType">The attribute's data type.</param>
/// <param name="HeldDataType">The data type whose column holds the change's value, or null where it holds nothing.</param>
public sealed record OptimisticExportUnrecordedChange(
    Guid PendingExportId,
    Guid ConnectedSystemObjectId,
    string AttributeName,
    AttributeDataType AttributeType,
    AttributeDataType? HeldDataType);
