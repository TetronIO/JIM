// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// A hard stop: a reason the change cannot proceed at all. When a
/// <c>ConsequenceConfirmationDialog</c> is given any blocker, the confirm action is unavailable and
/// the only way out is to close the dialog and resolve the blocker first.
/// </summary>
public sealed class ConsequenceBlocker
{
    /// <summary>
    /// The headline stating what is blocking, typically including a count.
    /// </summary>
    public required string Headline { get; init; }

    /// <summary>
    /// Optional explanation of how to clear the blocker.
    /// </summary>
    public string? Body { get; init; }

    /// <summary>
    /// The specific objects standing in the way, where they can be enumerated usefully.
    /// </summary>
    public IReadOnlyList<ConsequenceItem> Items { get; init; } = [];

    /// <summary>
    /// Optional link taking the administrator to the blocking objects.
    /// </summary>
    public string? LinkHref { get; init; }

    /// <summary>
    /// Text for <see cref="LinkHref"/>. Ignored when no link is set.
    /// </summary>
    public string? LinkText { get; init; }
}
