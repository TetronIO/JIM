// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;

namespace JIM.Scim.Schema;

/// <summary>
/// The outcome of building a SCIM resource from JIM attribute values.
/// </summary>
public class ScimResourceWriteResult
{
    /// <summary>The resource, ready to send as a POST or PUT body.</summary>
    public JsonObject Resource { get; init; } = [];

    /// <summary>
    /// The names of attributes the provider's schema does not have, which therefore could not be
    /// written. Reported rather than dropped: an export that silently omits a value is a change JIM
    /// records as applied and the provider never received.
    /// </summary>
    public List<string> UnknownAttributes { get; init; } = [];
}
