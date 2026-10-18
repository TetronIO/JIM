// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Models.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Schema;
using JIM.Scim.Serialisation;

namespace JIM.Scim.Service;

/// <summary>
/// The definitions every Service Provider store starts with (D18): the User and Group resource types, and the User,
/// Group and Enterprise User schemas, as the RFC 7643 documents the store holds.
/// </summary>
/// <remarks>
/// <c>password</c> stays in the stored User schema: intake refuses it because the schema says it is write-only (D5), and
/// an attribute missing from the schema would instead be dropped as undeclared. Discovery leaves it out when publishing.
/// </remarks>
public static class ScimCoreDefinitions
{
    /// <summary>
    /// Fresh rows for a new store; the caller sets the Connected System.
    /// </summary>
    public static (List<ScimStoreSchema> Schemas, List<ScimStoreResourceType> ResourceTypes) Rows()
    {
        List<ScimSchema> schemas = [ScimCoreSchemas.User(), ScimCoreSchemas.Group(), ScimCoreSchemas.EnterpriseUser()];
        var resourceTypes = ScimCoreSchemas.ResourceTypes();
        foreach (var resourceType in resourceTypes)
            resourceType.Schemas = [ScimUrns.ResourceType];

        return (
            schemas.Select(s => new ScimStoreSchema { Urn = s.Id!, Definition = JsonSerializer.Serialize(s, ScimJson.Options) }).ToList(),
            resourceTypes.Select(t => new ScimStoreResourceType
            {
                Name = t.Name!,
                Endpoint = t.Endpoint!,
                Definition = JsonSerializer.Serialize(t, ScimJson.Options)
            }).ToList());
    }
}
