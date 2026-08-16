// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// One line of Advanced Mode Container Scope text: what it says, about which Container, and how far it reaches.
/// </summary>
/// <param name="LineNumber">
/// The 1-based line the statement was read from, so anything reported about it can be found in the text the
/// administrator wrote rather than in a normalised copy of it.
/// </param>
/// <param name="Kind">Whether the Container is brought into scope or carved out of the branch around it.</param>
/// <param name="Path">
/// The Container's identifier in the Connected System's own terms, exactly as authored. It is resolved against the
/// discovered hierarchy when the text is applied, and naming no Container is an error rather than a no-op.
/// </param>
/// <param name="Scope">How far the statement reaches, which is the same question a Container's own scope answers.</param>
public sealed record ContainerScopeStatement(
    int LineNumber,
    ContainerScopeStatementKind Kind,
    string Path,
    ConnectedSystemContainerScope Scope);
