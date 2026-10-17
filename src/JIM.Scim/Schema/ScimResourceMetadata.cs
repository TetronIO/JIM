// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Schema;

/// <summary>
/// What the Service Provider states about a resource rather than storing in it: its <c>id</c> and the members of
/// <c>meta</c> (RFC 7643 section 3.1). <see cref="ScimResourceProjection"/> adds them to every response.
/// </summary>
public sealed class ScimResourceMetadata
{
    /// <summary>
    /// The server-assigned, immutable identifier (D7).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The resource type's name, for example <c>User</c>.
    /// </summary>
    public required string ResourceType { get; init; }

    /// <summary>
    /// When the resource was created.
    /// </summary>
    public required DateTimeOffset Created { get; init; }

    /// <summary>
    /// When the resource last changed, which a client's Delta Import watermarks against.
    /// </summary>
    public required DateTimeOffset LastModified { get; init; }

    /// <summary>
    /// The resource's version, returned as the weak entity tag <c>W/"n"</c> in <c>meta.version</c> (D8).
    /// </summary>
    public required long Version { get; init; }

    /// <summary>
    /// The resource's URI. Left out of <c>meta</c> where null.
    /// </summary>
    public string? Location { get; init; }
}
