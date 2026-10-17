// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Scim.Messages;

namespace JIM.Scim.Service;

/// <summary>
/// A resource divided the way the store holds it (<see cref="ScimReferenceEntries.Split"/>): the payload, and the
/// resources each reference attribute refers to; or why it cannot be.
/// </summary>
public sealed class ScimReferenceSplit
{
    /// <summary>
    /// The resource without its reference attributes.
    /// </summary>
    public JsonObject Payload { get; init; } = [];

    /// <summary>
    /// The resources each reference attribute refers to, keyed by the attribute's store name; every reference attribute
    /// of the type is present, empty where the resource holds none.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Guid>> References { get; init; } = new Dictionary<string, IReadOnlyList<Guid>>();

    /// <summary>
    /// Why a reference value cannot refer to a resource (D9), naming the attribute and never the value; null otherwise.
    /// </summary>
    public ScimError? Error { get; init; }
}
