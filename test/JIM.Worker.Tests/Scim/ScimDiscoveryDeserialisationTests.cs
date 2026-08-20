// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Messages;
using JIM.Scim.Serialisation;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// Deserialisation of the three SCIM 2.0 discovery documents. The payloads here are real: the
/// ServiceProviderConfig and Schema samples come from RFC 7643 section 8.5 and section 7, and the
/// abridged Laravel payload reproduces what the containerised test provider actually returns
/// (see engineering/notes/SCIM_TEST_PROVIDER_ANALYSIS.md).
/// </summary>
public class ScimDiscoveryDeserialisationTests
{
    [Test]
    public void ServiceProviderConfig_RfcExample_ReadsEveryAdvertisedFeature()
    {
        const string json = """
        {
          "schemas": ["urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig"],
          "documentationUri": "http://example.com/help/scim.html",
          "patch": { "supported": true },
          "bulk": { "supported": true, "maxOperations": 1000, "maxPayloadSize": 1048576 },
          "filter": { "supported": true, "maxResults": 200 },
          "changePassword": { "supported": true },
          "sort": { "supported": true },
          "etag": { "supported": true },
          "authenticationSchemes": [
            {
              "name": "OAuth Bearer Token",
              "description": "Authentication scheme using the OAuth Bearer Token Standard",
              "specUri": "http://www.rfc-editor.org/info/rfc6750",
              "type": "oauthbearertoken",
              "primary": true
            },
            {
              "name": "HTTP Basic",
              "description": "Authentication scheme using the HTTP Basic Standard",
              "specUri": "http://www.rfc-editor.org/info/rfc2617",
              "type": "httpbasic"
            }
          ],
          "meta": {
            "location": "https://example.com/v2/ServiceProviderConfig",
            "resourceType": "ServiceProviderConfig",
            "created": "2010-01-23T04:56:22Z",
            "lastModified": "2011-05-13T04:42:34Z",
            "version": "W/\"3694e05e9dff594\""
          }
        }
        """;

        var config = JsonSerializer.Deserialize<ScimServiceProviderConfig>(json, ScimJson.Options);

        Assert.That(config, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(config!.Patch?.Supported, Is.True);
            Assert.That(config.Bulk?.Supported, Is.True);
            Assert.That(config.Bulk?.MaxOperations, Is.EqualTo(1000));
            Assert.That(config.Bulk?.MaxPayloadSize, Is.EqualTo(1048576));
            Assert.That(config.Filter?.Supported, Is.True);
            Assert.That(config.Filter?.MaxResults, Is.EqualTo(200));
            Assert.That(config.ChangePassword?.Supported, Is.True);
            Assert.That(config.Sort?.Supported, Is.True);
            Assert.That(config.ETag?.Supported, Is.True);
            Assert.That(config.AuthenticationSchemes, Has.Count.EqualTo(2));
            Assert.That(config.AuthenticationSchemes[0].Type, Is.EqualTo("oauthbearertoken"));
            Assert.That(config.AuthenticationSchemes[0].Primary, Is.True);
            Assert.That(config.AuthenticationSchemes[1].Primary, Is.False);
            Assert.That(config.Meta?.Version, Is.EqualTo("W/\"3694e05e9dff594\""));
        }
    }

    [Test]
    public void ServiceProviderConfig_FeatureBlocksAbsent_LeavesThemNullRatherThanAssumingSupport()
    {
        // A provider that omits a block has not asserted support for it. Defaulting a missing block to
        // "supported" would have the connector send PATCH or Bulk requests the provider cannot answer.
        const string json = """
        {
          "schemas": ["urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig"],
          "patch": { "supported": true }
        }
        """;

        var config = JsonSerializer.Deserialize<ScimServiceProviderConfig>(json, ScimJson.Options);

        Assert.That(config, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(config!.Patch?.Supported, Is.True);
            Assert.That(config.Bulk, Is.Null);
            Assert.That(config.Filter, Is.Null);
            Assert.That(config.ETag, Is.Null);
            Assert.That(config.AuthenticationSchemes, Is.Empty);
        }
    }

    [Test]
    public void ServiceProviderConfig_UnknownVendorBlock_IsIgnoredRatherThanFailing()
    {
        // The Laravel test provider advertises a non-standard "pagination" block. Unknown members must
        // not fail the read, or a single vendor extension would make discovery impossible.
        const string json = """
        {
          "schemas": ["urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig"],
          "patch": { "supported": true },
          "pagination": {
            "cursor": true,
            "index": true,
            "defaultPaginationMethod": "index",
            "defaultPageSize": 10,
            "maxPageSize": 100,
            "cursorTimeout": 3600
          }
        }
        """;

        var config = JsonSerializer.Deserialize<ScimServiceProviderConfig>(json, ScimJson.Options);

        Assert.That(config?.Patch?.Supported, Is.True);
    }

    [Test]
    public void ServiceProviderConfig_MixedCasePropertyNames_AreRead()
    {
        // RFC 7643 section 2.1: attribute names are case insensitive.
        const string json = """{ "Patch": { "Supported": true }, "ETAG": { "supported": true } }""";

        var config = JsonSerializer.Deserialize<ScimServiceProviderConfig>(json, ScimJson.Options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(config?.Patch?.Supported, Is.True);
            Assert.That(config?.ETag?.Supported, Is.True);
        }
    }

    [Test]
    public void ResourceTypes_ListResponse_ReadsEndpointsAndSchemaExtensions()
    {
        const string json = """
        {
          "schemas": ["urn:ietf:params:scim:api:messages:2.0:ListResponse"],
          "totalResults": 2,
          "itemsPerPage": 2,
          "startIndex": 1,
          "Resources": [
            {
              "schemas": ["urn:ietf:params:scim:schemas:core:2.0:ResourceType"],
              "id": "User",
              "name": "User",
              "endpoint": "/Users",
              "description": "User Account",
              "schema": "urn:ietf:params:scim:schemas:core:2.0:User",
              "schemaExtensions": [
                {
                  "schema": "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User",
                  "required": true
                }
              ]
            },
            {
              "schemas": ["urn:ietf:params:scim:schemas:core:2.0:ResourceType"],
              "id": "Group",
              "name": "Group",
              "endpoint": "/Groups",
              "schema": "urn:ietf:params:scim:schemas:core:2.0:Group"
            }
          ]
        }
        """;

        var list = JsonSerializer.Deserialize<ScimListResponse<ScimResourceType>>(json, ScimJson.Options);

        Assert.That(list, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(list!.TotalResults, Is.EqualTo(2));
            Assert.That(list.Resources, Has.Count.EqualTo(2));
            Assert.That(list.Resources[0].Name, Is.EqualTo("User"));
            Assert.That(list.Resources[0].Endpoint, Is.EqualTo("/Users"));
            Assert.That(list.Resources[0].Schema, Is.EqualTo(ScimUrns.User));
            Assert.That(list.Resources[0].SchemaExtensions, Has.Count.EqualTo(1));
            Assert.That(list.Resources[0].SchemaExtensions[0].Schema, Is.EqualTo(ScimUrns.EnterpriseUser));
            Assert.That(list.Resources[0].SchemaExtensions[0].Required, Is.True);
            Assert.That(list.Resources[1].SchemaExtensions, Is.Empty);
        }
    }

    [Test]
    public void ListResponse_ResourcesMemberSpeltLowerCase_IsStillRead()
    {
        // RFC 7644 capitalises "Resources", but providers in the wild send "resources"; case-insensitive
        // matching covers both, and an unread Resources member would silently look like an empty page.
        const string json = """{ "totalResults": 1, "resources": [ { "id": "User", "name": "User" } ] }""";

        var list = JsonSerializer.Deserialize<ScimListResponse<ScimResourceType>>(json, ScimJson.Options);

        Assert.That(list?.Resources, Has.Count.EqualTo(1));
    }

    [Test]
    public void ListResponse_NoResourcesMember_YieldsEmptyRatherThanNull()
    {
        // RFC 7644 section 3.4.2: a query matching nothing omits Resources entirely.
        const string json = """{ "schemas": ["urn:ietf:params:scim:api:messages:2.0:ListResponse"], "totalResults": 0 }""";

        var list = JsonSerializer.Deserialize<ScimListResponse<ScimResourceType>>(json, ScimJson.Options);

        Assert.That(list, Is.Not.Null);
        Assert.That(list!.Resources, Is.Empty);
    }

    [Test]
    public void Schema_UserCoreExtract_ReadsAttributeFacetsAndSubAttributes()
    {
        const string json = """
        {
          "id": "urn:ietf:params:scim:schemas:core:2.0:User",
          "name": "User",
          "description": "User Account",
          "attributes": [
            {
              "name": "userName",
              "type": "string",
              "multiValued": false,
              "description": "Unique identifier for the User.",
              "required": true,
              "caseExact": false,
              "mutability": "readWrite",
              "returned": "default",
              "uniqueness": "server"
            },
            {
              "name": "name",
              "type": "complex",
              "multiValued": false,
              "required": false,
              "subAttributes": [
                { "name": "givenName", "type": "string", "multiValued": false, "required": false, "mutability": "readWrite" },
                { "name": "familyName", "type": "string", "multiValued": false, "required": false, "mutability": "readWrite" }
              ],
              "mutability": "readWrite"
            },
            {
              "name": "emails",
              "type": "complex",
              "multiValued": true,
              "required": false,
              "subAttributes": [
                { "name": "value", "type": "string", "multiValued": false, "required": false, "mutability": "readWrite" },
                {
                  "name": "type",
                  "type": "string",
                  "multiValued": false,
                  "required": false,
                  "canonicalValues": ["work", "home", "other"],
                  "mutability": "readWrite"
                },
                { "name": "primary", "type": "boolean", "multiValued": false, "required": false, "mutability": "readWrite" }
              ],
              "mutability": "readWrite"
            },
            {
              "name": "groups",
              "type": "complex",
              "multiValued": true,
              "mutability": "readOnly",
              "subAttributes": [
                { "name": "value", "type": "string", "multiValued": false, "mutability": "readOnly" },
                { "name": "$ref", "type": "reference", "referenceTypes": ["User", "Group"], "multiValued": false, "mutability": "readOnly" }
              ]
            }
          ]
        }
        """;

        var schema = JsonSerializer.Deserialize<ScimSchema>(json, ScimJson.Options);

        Assert.That(schema, Is.Not.Null);
        var userName = schema!.Attributes.Single(a => a.Name == "userName");
        var name = schema.Attributes.Single(a => a.Name == "name");
        var emails = schema.Attributes.Single(a => a.Name == "emails");
        var groups = schema.Attributes.Single(a => a.Name == "groups");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(schema.Id, Is.EqualTo(ScimUrns.User));
            Assert.That(userName.Type, Is.EqualTo("string"));
            Assert.That(userName.Required, Is.True);
            Assert.That(userName.MultiValued, Is.False);
            Assert.That(userName.Mutability, Is.EqualTo("readWrite"));
            Assert.That(userName.Uniqueness, Is.EqualTo("server"));
            Assert.That(name.SubAttributes.Select(s => s.Name), Is.EquivalentTo(new[] { "givenName", "familyName" }));
            Assert.That(emails.MultiValued, Is.True);
            Assert.That(emails.SubAttributes.Single(s => s.Name == "type").CanonicalValues,
                Is.EquivalentTo(new[] { "work", "home", "other" }));
            Assert.That(groups.Mutability, Is.EqualTo("readOnly"));
            Assert.That(groups.SubAttributes.Single(s => s.Name == "$ref").ReferenceTypes,
                Is.EquivalentTo(new[] { "User", "Group" }));
        }
    }

    [Test]
    public void Schema_AttributeWithNoSubAttributesOrCanonicalValues_YieldsEmptyCollections()
    {
        // Collections default to empty so consumers never null-check before enumerating a facet the
        // provider simply did not send.
        const string json = """
        { "id": "urn:ietf:params:scim:schemas:core:2.0:Group", "name": "Group",
          "attributes": [ { "name": "displayName", "type": "string" } ] }
        """;

        var schema = JsonSerializer.Deserialize<ScimSchema>(json, ScimJson.Options);
        var displayName = schema!.Attributes.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(displayName.SubAttributes, Is.Empty);
            Assert.That(displayName.CanonicalValues, Is.Empty);
            Assert.That(displayName.ReferenceTypes, Is.Empty);
            Assert.That(displayName.MultiValued, Is.False);
            Assert.That(displayName.Required, Is.False);
        }
    }

    [Test]
    public void Schemas_ReturnedAsBareArrayInsteadOfListResponse_IsHandledByTheCaller()
    {
        // RFC 7644 requires /Schemas to return a ListResponse, but providers exist that return a bare
        // array. Both shapes must deserialise, which is why the connector probes for both.
        const string json = """[ { "id": "urn:ietf:params:scim:schemas:core:2.0:User", "name": "User" } ]""";

        var schemas = JsonSerializer.Deserialize<List<ScimSchema>>(json, ScimJson.Options);

        Assert.That(schemas, Has.Count.EqualTo(1));
        Assert.That(schemas![0].Id, Is.EqualTo(ScimUrns.User));
    }

    [Test]
    public void Meta_LastModified_IsReadAsUtc()
    {
        const string json = """{ "meta": { "lastModified": "2026-02-04T13:53:42+00:00", "resourceType": "User" } }""";

        var config = JsonSerializer.Deserialize<ScimServiceProviderConfig>(json, ScimJson.Options);

        Assert.That(config?.Meta?.LastModified, Is.EqualTo(new DateTimeOffset(2026, 2, 4, 13, 53, 42, TimeSpan.Zero)));
    }
}
