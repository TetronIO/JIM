// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Exceptions;

/// <summary>
/// Thrown when creating or renaming a Metaverse Attribute to a name that already exists. The uniqueness comparison is
/// case-insensitive (<c>CostCentre</c> and <c>costCentre</c> cannot coexist), though names are always stored and
/// returned as-is. This is a server-side belt-and-braces guard; the UI/API additionally validate in real time.
/// </summary>
public class MetaverseAttributeNameConflictException : InvalidOperationException
{
    /// <summary>
    /// The name (as supplied by the caller) that conflicts with an existing attribute.
    /// </summary>
    public string ConflictingName { get; }

    public MetaverseAttributeNameConflictException(string conflictingName)
        : base($"A Metaverse Attribute named '{conflictingName}' already exists (names are compared case-insensitively).")
    {
        ConflictingName = conflictingName;
    }
}
