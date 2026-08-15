// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models.Api;

/// <summary>
/// A Connected System's Container Scope stated as text (Advanced Mode).
/// </summary>
public class ConnectedSystemContainerScopeTextDto
{
    /// <summary>
    /// One statement per line, in hierarchy order: <c>include</c> or <c>exclude</c>, an optional
    /// <c>one-level</c>, then the Container's path. Empty where no Container states anything.
    /// </summary>
    /// <example>include OU=Corp,DC=example,DC=com</example>
    public required string Text { get; set; }
}

/// <summary>
/// Replaces a Connected System's whole Container Scope with the statements in a piece of text.
/// </summary>
public class UpdateConnectedSystemContainerScopeTextRequest
{
    /// <summary>
    /// The statements to apply, one per line: <c>include</c> (or <c>+</c>) or <c>exclude</c> (or <c>-</c>), an
    /// optional <c>one-level</c>, then the Container's path. Blank lines and lines beginning with <c>#</c> are
    /// ignored.
    ///
    /// This states the whole of Container Scope rather than a change to it, so a Container the text does not name
    /// states nothing: empty text clears every selection and exclusion. Partition selection is left alone, except
    /// that naming a Container selects the partition holding it.
    ///
    /// Applied all-or-nothing. A path naming no Container, a Container named twice, and a statement an ancestor
    /// already makes are each refused with the line that caused them, and nothing is changed.
    /// </summary>
    /// <example>include OU=Corp,DC=example,DC=com</example>
    public required string Text { get; set; }
}
