// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Models.Staging;

/// <summary>
/// A single piece of classification metadata attached to a Connected System Object Type during schema import,
/// i.e. that an LDAP object type is an auxiliary class. See <see cref="ObjectTypeTags"/> for the well-known
/// keys and values.
/// </summary>
/// <remarks>
/// Tags are connector-owned: a schema refresh replaces the tags for each object type with whatever the connector
/// reports, so a classification that changes at the Connected System is reflected rather than accumulated.
/// </remarks>
public class ConnectedSystemObjectTypeTag
{
    public int Id { get; set; }

    /// <summary>
    /// The Object Type this tag classifies. Never serialised: a tag is only ever reached as a child of its
    /// Object Type, so writing the parent back out would be a cycle. The OpenAPI schema generator has no cycle
    /// breaking here and walks it to System.Text.Json's 256-level depth limit, which fails the whole document
    /// and with it the jim.web image build.
    /// </summary>
    [JsonIgnore]
    public ConnectedSystemObjectType ConnectedSystemObjectType { get; set; } = null!;
    public int ConnectedSystemObjectTypeId { get; set; }

    /// <summary>
    /// What is being classified, i.e. <see cref="ObjectTypeTags.Keys.ClassKind"/>.
    /// </summary>
    public string Key { get; set; } = null!;

    /// <summary>
    /// The classification itself, i.e. <see cref="ObjectTypeTags.Values.ClassKindAuxiliary"/>.
    /// </summary>
    public string Value { get; set; } = null!;

    public override string ToString()
    {
        return $"{Key}={Value}";
    }
}
