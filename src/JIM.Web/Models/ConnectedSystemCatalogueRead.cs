// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// The result of reading a Connected System's catalogue for the portal: what was read, or why nothing could be.
/// A failure is an expected outcome here (the database is unreachable, or the settings on screen are incomplete),
/// so it is carried as a value the editor shows rather than thrown.
/// </summary>
/// <param name="Value">What was read; null when <paramref name="Failure"/> is set, and for an object the account cannot see.</param>
/// <param name="Failure">Why nothing could be read, in words an administrator can act on; null on success.</param>
public sealed record ConnectedSystemCatalogueRead<T>(T? Value, string? Failure)
{
    /// <summary>Whether the read succeeded.</summary>
    public bool Succeeded => Failure == null;
}
