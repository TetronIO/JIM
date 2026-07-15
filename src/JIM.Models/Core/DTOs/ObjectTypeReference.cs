// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// A single configuration object that references a Metaverse Object Type, surfaced in an
/// <see cref="ObjectTypeDeletionImpact"/> so callers can show exactly what a deletion would block on or cascade-remove.
/// </summary>
public class ObjectTypeReference
{
    public ObjectTypeReferenceKind Kind { get; set; }

    /// <summary>
    /// A human-readable description of the reference (e.g. the Synchronisation Rule or Predefined Search name), for
    /// display in the confirmation dialog and audit Activities.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
