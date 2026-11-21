// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Connectors.Graph;

/// <summary>
/// One attribute of the Microsoft Graph Connector's schema, and where its values come from in Graph: the property to
/// <c>$select</c>, and, for a value inside a complex property, the member to read.
/// </summary>
/// <param name="Name">The attribute's name in JIM, which is Graph's property name wherever Graph has one.</param>
/// <param name="SelectProperty">The Graph property to <c>$select</c>; null for a relationship (read separately) or a value the Connector derives.</param>
/// <param name="Member">For a value inside a complex property (<c>onPremisesExtensionAttributes/extensionAttribute1</c>), the member to read.</param>
/// <param name="ReferencesObjectType">For a reference, the Object Type it points at; null where it can point at either.</param>
/// <param name="IsConnectorDefined">A value the Connector derives rather than one Graph holds, such as <c>sourceOfAuthority</c>.</param>
internal sealed record GraphSchemaAttribute(
    string Name,
    AttributeDataType Type,
    AttributeWritability Writability,
    string Description,
    string? SelectProperty,
    AttributePlurality Plurality = AttributePlurality.SingleValued,
    string? Member = null,
    string? ReferencesObjectType = null,
    bool Required = false,
    bool IsOperational = false,
    bool AlwaysImported = false,
    bool IsConnectorDefined = false)
{
    public ConnectorSchemaAttribute ToConnectorSchemaAttribute() => new(Name, Type, Plurality, Required, writability: Writability)
    {
        Description = Description,
        ReferencesObjectTypeName = ReferencesObjectType,
        IsOperational = IsOperational,
        AlwaysImported = AlwaysImported,
        IsConnectorDefined = IsConnectorDefined
    };
}
