// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Scim.Messages;

namespace JIM.Scim.Schema;

/// <summary>
/// The outcome of reading a POST or PUT body (<see cref="ScimResourceIntake"/>): the resource to store, or the error to
/// return, never both.
/// </summary>
public sealed class ScimIntakeResult
{
    /// <summary>
    /// The attributes to store. Null where the body was refused.
    /// </summary>
    public JsonObject? Resource { get; init; }

    /// <summary>
    /// Why the body was refused (status 400 with a <c>scimType</c>); its detail names attributes, never values. Null where
    /// it was read.
    /// </summary>
    public ScimError? Error { get; init; }

    /// <summary>
    /// The interoperability leniency the body needed and the attributes dropped from it, one line each, for the caller to
    /// log. They name attributes, never values.
    /// </summary>
    public IReadOnlyList<string> Coercions { get; init; } = [];
}
