// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Scim.Discovery;
using JIM.Scim.Schema;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Everything schema discovery learned about a service provider in one pass: the schema JIM will
/// present to administrators, what the provider can do, and where its resources live.
/// </summary>
public class ScimDiscoveryResult
{
    /// <summary>
    /// The Connected System schema, one object type per discovered resource type.
    /// </summary>
    public ConnectorSchema Schema { get; init; } = new();

    /// <summary>
    /// The provider's optional capabilities, at the protocol floors when it publishes no
    /// ServiceProviderConfig.
    /// </summary>
    public ScimProviderCapabilities Capabilities { get; init; } = ScimProviderCapabilities.From(null);

    /// <summary>
    /// The resource types, retained for their endpoints: a provider is free to publish resources
    /// somewhere other than <c>/Users</c> and <c>/Groups</c>, and import must follow what it says.
    /// </summary>
    public List<ScimResourceType> ResourceTypes { get; init; } = [];

    /// <summary>
    /// The flattened attributes behind each object type in <see cref="Schema"/>, keyed by object type
    /// name. Import reads resources through these: they carry the accessors that say where each value
    /// lives, which the Connector Schema deliberately does not model.
    /// </summary>
    public Dictionary<string, List<ScimFlattenedAttribute>> FlattenedAttributes { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Discovery shortfalls worth reporting to an administrator, including the capability warnings.
    /// Never silently absorbed: a provider gap must not read as a JIM one.
    /// </summary>
    public List<string> Warnings { get; init; } = [];
}
