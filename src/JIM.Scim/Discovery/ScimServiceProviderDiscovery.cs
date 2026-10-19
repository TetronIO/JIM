// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Scim.Resources;
using JIM.Scim.Schema;

namespace JIM.Scim.Discovery;

/// <summary>
/// The discovery documents JIM's own SCIM service provider publishes (RFC 7643 sections 5 to 7):
/// <c>/ServiceProviderConfig</c>, <c>/ResourceTypes</c> and <c>/Schemas</c>.
/// <para>
/// The schemas are the RFC's core definitions, the same ones the client connector falls back on, less every
/// write-only attribute: the service provider refuses a pushed <c>password</c> (D5 of the design), so it does not
/// advertise one. Composed through <see cref="ScimResourceTypeComposer"/>, what is published gives exactly the
/// attributes the service provider reads and writes.
/// </para>
/// </summary>
public static class ScimServiceProviderDiscovery
{
    private const string ServiceProviderConfigResourceType = "ServiceProviderConfig";
    private const string ResourceTypeResourceType = "ResourceType";
    private const string SchemaResourceType = "Schema";

    /// <summary>
    /// What the service provider supports: PATCH, filtering up to <paramref name="maxResults"/> resources a page, and
    /// entity tags. Bulk, sorting and password changes are not supported; authentication is a bearer token.
    /// </summary>
    /// <param name="baseUrl">The service provider's base URL, with or without a trailing slash.</param>
    /// <param name="maxResults">The most resources one page of a query returns.</param>
    public static ScimServiceProviderConfig ServiceProviderConfig(string baseUrl, int maxResults)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);

        return new ScimServiceProviderConfig
        {
            Schemas = [ScimUrns.ServiceProviderConfig],
            Patch = new ScimSupportedFeature { Supported = true },
            Bulk = new ScimBulkFeature { Supported = false, MaxOperations = 0, MaxPayloadSize = 0 },
            Filter = new ScimFilterFeature { Supported = true, MaxResults = maxResults },
            ChangePassword = new ScimSupportedFeature { Supported = false },
            Sort = new ScimSupportedFeature { Supported = false },
            ETag = new ScimSupportedFeature { Supported = true },
            AuthenticationSchemes =
            [
                new ScimAuthenticationScheme
                {
                    Type = "oauthbearertoken",
                    Name = "OAuth Bearer Token",
                    Description = "An API key scoped to this Connected System, sent as Authorization: Bearer.",
                    SpecUri = "https://www.rfc-editor.org/info/rfc6750",
                    Primary = true
                }
            ],
            Meta = new ScimMeta
            {
                ResourceType = ServiceProviderConfigResourceType,
                Location = Location(baseUrl, ScimEndpoints.ServiceProviderConfig)
            }
        };
    }

    /// <summary>
    /// The core resource types: User, with the enterprise extension, at <c>/Users</c>, and Group at <c>/Groups</c>.
    /// </summary>
    public static List<ScimResourceType> ResourceTypes(string baseUrl) => ResourceTypes(baseUrl, ScimCoreSchemas.ResourceTypes());

    /// <summary>
    /// A store's resource types as published: each given its schema URN and its location under <paramref name="baseUrl"/>.
    /// The documents given are completed in place and returned, so pass copies the caller owns.
    /// </summary>
    public static List<ScimResourceType> ResourceTypes(string baseUrl, IEnumerable<ScimResourceType> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var resourceTypes = stored.ToList();
        foreach (var resourceType in resourceTypes)
        {
            resourceType.Schemas = [ScimUrns.ResourceType];
            resourceType.Meta = new ScimMeta
            {
                ResourceType = ResourceTypeResourceType,
                Location = Location(baseUrl, $"{ScimEndpoints.ResourceTypes}/{resourceType.Id}")
            };
        }

        return resourceTypes;
    }

    /// <summary>
    /// The core schemas the core resource types are composed of, with no write-only attribute at any level.
    /// </summary>
    public static List<ScimSchema> Schemas(string baseUrl) =>
        // Each core schema is built afresh by its factory, so trimming it cannot reach anyone else's copy.
        Schemas(baseUrl, [ScimCoreSchemas.User(), ScimCoreSchemas.EnterpriseUser(), ScimCoreSchemas.Group()]);

    /// <summary>
    /// A store's schemas as published: no write-only attribute at any level, and each located under
    /// <paramref name="baseUrl"/>. The documents given are trimmed in place and returned, so pass copies the caller owns.
    /// </summary>
    public static List<ScimSchema> Schemas(string baseUrl, IEnumerable<ScimSchema> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var schemas = stored.ToList();
        foreach (var schema in schemas)
        {
            RemoveWriteOnly(schema.Attributes);
            schema.Meta = new ScimMeta
            {
                ResourceType = SchemaResourceType,
                Location = Location(baseUrl, $"{ScimEndpoints.Schemas}/{schema.Id}")
            };
        }

        return schemas;
    }

    private static void RemoveWriteOnly(List<ScimSchemaAttribute> attributes)
    {
        attributes.RemoveAll(a => string.Equals(a.Mutability, ScimMutability.WriteOnly, StringComparison.OrdinalIgnoreCase));
        foreach (var attribute in attributes)
            RemoveWriteOnly(attribute.SubAttributes);
    }

    private static string Location(string baseUrl, string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        return baseUrl.TrimEnd('/') + "/" + endpoint.TrimStart('/');
    }
}
