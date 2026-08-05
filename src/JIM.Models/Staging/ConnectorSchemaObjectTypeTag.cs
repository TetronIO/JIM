// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Classification metadata a Connector reports for an Object Type during schema discovery, i.e. that an LDAP
/// object type is an auxiliary class. Persisted as a <see cref="ConnectedSystemObjectTypeTag"/> by schema import.
/// See <see cref="ObjectTypeTags"/> for the well-known keys and values.
/// </summary>
public class ConnectorSchemaObjectTypeTag(string key, string value)
{
    /// <summary>
    /// What is being classified, i.e. <see cref="ObjectTypeTags.Keys.ClassKind"/>.
    /// </summary>
    public string Key { get; set; } = key;

    /// <summary>
    /// The classification itself, i.e. <see cref="ObjectTypeTags.Values.ClassKindAuxiliary"/>.
    /// </summary>
    public string Value { get; set; } = value;

    public override string ToString()
    {
        return $"{Key}={Value}";
    }
}
