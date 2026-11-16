// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// The object a Full Import's end-of-run reference sweep resolved an outstanding reference value to (#2521): the value is
/// given the referenced object's id, and so is the matching value of the change record the run wrote for it.
/// </summary>
/// <param name="AttributeValueId">The outstanding value's id.</param>
/// <param name="ReferencedConnectedSystemObjectId">The object the value names.</param>
/// <param name="ExecutionItemId">The run's execution item for the holding object, whose change record recorded the value
/// without its object's id; null where the object has none.</param>
/// <param name="AttributeId">The Reference attribute the value belongs to.</param>
/// <param name="UnresolvedReferenceValue">The identifier the value names its object by, as the change record holds it.</param>
public sealed record OutstandingReferenceResolution(
    Guid AttributeValueId,
    Guid ReferencedConnectedSystemObjectId,
    Guid? ExecutionItemId,
    int AttributeId,
    string UnresolvedReferenceValue);
