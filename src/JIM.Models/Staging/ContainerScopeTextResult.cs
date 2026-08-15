// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What reading a piece of Advanced Mode Container Scope text produced: the statements it makes, and everything
/// wrong with it.
/// </summary>
/// <remarks>
/// Both are returned together rather than the first error being thrown, because an administrator editing a scope of
/// any size needs every problem at once: fixing them one round trip at a time, against a hierarchy that may have
/// hundreds of Containers, is how a half-corrected text gets saved.
/// </remarks>
public sealed class ContainerScopeTextResult
{
    /// <summary>
    /// The statements the text makes, in the order they were written. Populated even where
    /// <see cref="Errors"/> is not empty, so a caller can show what was understood alongside what was not.
    /// </summary>
    public required IReadOnlyList<ContainerScopeStatement> Statements { get; init; }

    /// <summary>
    /// Everything that stops the text being applied. Empty is the only state in which it may be applied.
    /// </summary>
    public required IReadOnlyList<ContainerScopeTextError> Errors { get; init; }

    /// <summary>
    /// Whether the text can be applied.
    /// </summary>
    public bool IsValid => Errors.Count == 0;
}
