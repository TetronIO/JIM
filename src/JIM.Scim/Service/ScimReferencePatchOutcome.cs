// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Scim.Messages;

namespace JIM.Scim.Service;

/// <summary>
/// The result of resolving a membership-only PATCH (<see cref="ScimReferencePatchPlan.Resolve"/>): the changes to make,
/// or the error that refuses the request, never both.
/// </summary>
public sealed class ScimReferencePatchOutcome
{
    /// <summary>
    /// The changes, one per reference attribute that changes; empty where the request changes nothing.
    /// </summary>
    public IReadOnlyList<ScimReferenceChange> Changes { get; init; } = [];

    /// <summary>
    /// Why the request is refused; null where it applies.
    /// </summary>
    public ScimError? Error { get; init; }

    /// <summary>
    /// The interoperability leniency the request needed, one line each, naming attributes and never values.
    /// </summary>
    public IReadOnlyList<string> Coercions { get; init; } = [];

    /// <summary>
    /// The attributes the request named that the resource type does not declare (D17).
    /// </summary>
    public IReadOnlyList<string> IgnoredAttributes { get; init; } = [];
}
