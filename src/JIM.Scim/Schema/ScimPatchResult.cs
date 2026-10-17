// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Scim.Messages;

namespace JIM.Scim.Schema;

/// <summary>
/// The outcome of applying a PATCH request (<see cref="ScimPatchApplier"/>): the patched resource, or the error that
/// stopped it, never both. A PATCH is atomic (RFC 7644 section 3.5.2), so an error means nothing was applied.
/// </summary>
public sealed class ScimPatchResult
{
    /// <summary>
    /// The resource with every operation applied. Null where the request failed.
    /// </summary>
    public JsonObject? Resource { get; init; }

    /// <summary>
    /// Why the request failed, as the SCIM error to return (status 400 with a <c>scimType</c>). Its detail names
    /// attributes, never values. Null where the request succeeded.
    /// </summary>
    public ScimError? Error { get; init; }

    /// <summary>
    /// The interoperability leniency the request needed, one line each (a capitalised <c>op</c>, a Boolean sent as a
    /// string, a read-only attribute ignored), for the caller to log. They name attributes, never values.
    /// </summary>
    public IReadOnlyList<string> Coercions { get; init; } = [];
}
