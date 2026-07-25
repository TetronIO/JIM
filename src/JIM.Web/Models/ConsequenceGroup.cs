// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// The set of side effects a change will have: things removed or altered beyond the object the
/// administrator is acting on. Unlike a <see cref="ConsequenceBlocker"/> these do not prevent the
/// change; they are what the administrator is being asked to accept.
/// </summary>
public sealed class ConsequenceGroup
{
    /// <summary>
    /// The headline stating what will happen, typically including a count.
    /// </summary>
    public required string Headline { get; init; }

    /// <summary>
    /// Optional clarification, for example what is deliberately left untouched.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>
    /// The individual side effects.
    /// </summary>
    public IReadOnlyList<ConsequenceItem> Items { get; init; } = [];
}
