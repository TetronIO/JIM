// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What applying Advanced Mode Container Scope text to a Connected System did.
/// </summary>
public sealed class ContainerScopeTextApplyResult
{
    /// <summary>
    /// Everything that stopped the text being applied, empty on success. Where this is not empty, the Connected
    /// System is exactly as it was: the text is applied in full or not at all.
    /// </summary>
    public required IReadOnlyList<ContainerScopeTextError> Errors { get; init; }

    /// <summary>
    /// The canonical text for the scope now in force, which is what the next read returns. Reported back on
    /// success so a caller can see at once whether what it wrote survived intact, rather than discovering it on
    /// the next run.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Whether the text was applied.
    /// </summary>
    public bool Applied => Errors.Count == 0;
}
