// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Builds the Pending Exports the SCIM export fixtures drive the connector with. Shared so the
/// per-object and bulk fixtures send the connector identical input: the point of the bulk tests is that
/// the same change produces the same effect whichever way it travels, which only holds if both fixtures
/// start from the same object.
/// </summary>
internal static class ScimExportTestObjects
{
    internal static ConnectedSystemObjectType ObjectType(string name)
    {
        return new ConnectedSystemObjectType { Id = name == "User" ? 1 : 2, Name = name, Selected = true };
    }

    internal static ConnectedSystemObjectTypeAttribute Attribute(string name, ConnectedSystemObjectType objectType, AttributeDataType type = AttributeDataType.Text)
    {
        return new ConnectedSystemObjectTypeAttribute
        {
            Name = name,
            Type = type,
            ConnectedSystemObjectType = objectType
        };
    }

    internal static PendingExportAttributeValueChange Change(
        string attributeName,
        ConnectedSystemObjectType objectType,
        string? value,
        PendingExportAttributeChangeType changeType = PendingExportAttributeChangeType.Update)
    {
        return new PendingExportAttributeValueChange
        {
            Attribute = Attribute(attributeName, objectType),
            StringValue = value,
            ChangeType = changeType
        };
    }

    internal static PendingExport Create(ConnectedSystemObjectType objectType, params PendingExportAttributeValueChange[] changes)
    {
        return new PendingExport
        {
            ChangeType = PendingExportChangeType.Create,
            AttributeValueChanges = changes.ToList()
        };
    }

    /// <summary>
    /// A Pending Export against an existing resource, whose External ID is the provider's own id for it.
    /// </summary>
    internal static PendingExport Against(
        string resourceId,
        ConnectedSystemObjectType objectType,
        PendingExportChangeType changeType,
        params PendingExportAttributeValueChange[] changes)
    {
        var externalIdAttribute = Attribute("id", objectType);
        externalIdAttribute.Id = 99;
        externalIdAttribute.IsExternalId = true;

        var connectedSystemObject = new ConnectedSystemObject
        {
            Type = objectType,
            TypeId = objectType.Id,
            ExternalIdAttributeId = externalIdAttribute.Id,
            AttributeValues =
            [
                new ConnectedSystemObjectAttributeValue { Attribute = externalIdAttribute, AttributeId = externalIdAttribute.Id, StringValue = resourceId }
            ]
        };

        return new PendingExport
        {
            ChangeType = changeType,
            ConnectedSystemObject = connectedSystemObject,
            AttributeValueChanges = changes.ToList()
        };
    }

    /// <summary>
    /// Gives the Connected System Object the entity tag a previous import would have brought back, which
    /// is what the connector sends as <c>If-Match</c>.
    /// </summary>
    internal static void WithImportedEntityTag(PendingExport pendingExport, ConnectedSystemObjectType objectType, string entityTag)
    {
        var versionAttribute = Attribute("meta.version", objectType);
        versionAttribute.Id = 98;

        pendingExport.ConnectedSystemObject!.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = versionAttribute,
            AttributeId = versionAttribute.Id,
            StringValue = entityTag
        });
    }
}
