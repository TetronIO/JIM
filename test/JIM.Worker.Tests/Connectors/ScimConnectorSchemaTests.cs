// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using JIM.Connectors.SCIM;
using JIM.Connectors.SCIM.Authentication;
using JIM.Models.Core;
using JIM.Scim;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Schema discovery: reading a service provider's ServiceProviderConfig, ResourceTypes and Schemas, and
/// turning them into a Connector Schema. Every fallback path matters here, because refusing to configure
/// a provider that omits an optional discovery document would rule out working providers.
/// </summary>
[TestFixture]
public class ScimConnectorSchemaTests
{
    private const string BaseUrl = "https://provider.example.com/scim/v2";

    private ILogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();
    }

    private const string ServiceProviderConfigJson = """
    {
      "schemas": ["urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig"],
      "patch": { "supported": true },
      "filter": { "supported": true, "maxResults": 200 },
      "etag": { "supported": true },
      "bulk": { "supported": false }
    }
    """;

    private const string ResourceTypesJson = """
    {
      "schemas": ["urn:ietf:params:scim:api:messages:2.0:ListResponse"],
      "totalResults": 2,
      "Resources": [
        {
          "id": "User", "name": "User", "endpoint": "/Users",
          "schema": "urn:ietf:params:scim:schemas:core:2.0:User",
          "schemaExtensions": [ { "schema": "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User", "required": false } ]
        },
        { "id": "Group", "name": "Group", "endpoint": "/Groups", "schema": "urn:ietf:params:scim:schemas:core:2.0:Group" }
      ]
    }
    """;

    private const string SchemasJson = """
    {
      "schemas": ["urn:ietf:params:scim:api:messages:2.0:ListResponse"],
      "totalResults": 3,
      "Resources": [
        {
          "id": "urn:ietf:params:scim:schemas:core:2.0:User", "name": "User",
          "attributes": [
            { "name": "userName", "type": "string", "required": true, "mutability": "readWrite" },
            { "name": "active", "type": "boolean", "mutability": "readWrite" },
            { "name": "name", "type": "complex", "subAttributes": [
              { "name": "givenName", "type": "string" }, { "name": "familyName", "type": "string" } ] }
          ]
        },
        {
          "id": "urn:ietf:params:scim:schemas:core:2.0:Group", "name": "Group",
          "attributes": [ { "name": "displayName", "type": "string", "required": true } ]
        },
        {
          "id": "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User", "name": "EnterpriseUser",
          "attributes": [ { "name": "department", "type": "string" } ]
        }
      ]
    }
    """;

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/scim+json")
        };
    }

    private static HttpResponseMessage NotFound()
    {
        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"schemas":["urn:ietf:params:scim:api:messages:2.0:Error"],"status":"404"}""",
                Encoding.UTF8, "application/scim+json")
        };
    }

    /// <summary>
    /// Serves the three discovery endpoints, with any of them replaceable by a test.
    /// </summary>
    private static StubHttpMessageHandler DiscoveryHandler(
        string? serviceProviderConfig = ServiceProviderConfigJson,
        string? resourceTypes = ResourceTypesJson,
        string? schemas = SchemasJson,
        HttpStatusCode? failureStatus = null)
    {
        return new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (failureStatus.HasValue)
                return new HttpResponseMessage(failureStatus.Value) { Content = new StringContent(string.Empty) };

            if (path.EndsWith("/ServiceProviderConfig", StringComparison.Ordinal))
                return serviceProviderConfig == null ? NotFound() : Json(serviceProviderConfig);
            if (path.EndsWith("/ResourceTypes", StringComparison.Ordinal))
                return resourceTypes == null ? NotFound() : Json(resourceTypes);
            if (path.EndsWith("/Schemas", StringComparison.Ordinal))
                return schemas == null ? NotFound() : Json(schemas);

            return NotFound();
        });
    }

    private ScimConnectorSchema CreateDiscovery(StubHttpMessageHandler handler)
    {
        var client = new ScimHttpClient(
            new HttpClient(handler),
            new Uri(BaseUrl),
            new ScimStaticBearerTokenAuthentication("token"),
            new ScimRetryPolicy(maxRetries: 0, baseDelay: TimeSpan.Zero, maxDelay: TimeSpan.Zero),
            _logger,
            delay: (_, _) => Task.CompletedTask);

        return new ScimConnectorSchema(client, _logger);
    }

    [Test]
    public async Task DiscoverAsync_FullyDiscoverableProvider_BuildsAnObjectTypePerResourceTypeAsync()
    {
        var discovery = CreateDiscovery(DiscoveryHandler());

        var result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.That(result.Schema.ObjectTypes.Select(o => o.Name), Is.EqualTo(new[] { "User", "Group" }));
    }

    [Test]
    public async Task DiscoverAsync_ReadsCapabilitiesFromTheServiceProviderConfigAsync()
    {
        var discovery = CreateDiscovery(DiscoveryHandler());

        var result = await discovery.DiscoverAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Capabilities.SupportsPatch, Is.True);
            Assert.That(result.Capabilities.SupportsFilter, Is.True);
            Assert.That(result.Capabilities.FilterMaxResults, Is.EqualTo(200));
            Assert.That(result.Capabilities.SupportsETag, Is.True);
            Assert.That(result.Capabilities.SupportsBulk, Is.False);
        }
    }

    [Test]
    public async Task DiscoverAsync_KeepsTheResourceTypeEndpointsForLaterEnumerationAsync()
    {
        // The endpoint is not always /Users: a provider is free to publish something else, and import
        // has to use what ResourceTypes says rather than assuming.
        var discovery = CreateDiscovery(DiscoveryHandler());

        var result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.That(result.ResourceTypes.Select(r => r.Endpoint), Is.EqualTo(new[] { "/Users", "/Groups" }));
    }

    [Test]
    public async Task DiscoverAsync_AddsTheCommonAttributesEveryScimResourceCarriesAsync()
    {
        // id, externalId and meta are defined by the specification rather than by any schema document,
        // so they never appear in /Schemas and must be added deliberately.
        var discovery = CreateDiscovery(DiscoveryHandler());

        var result = await discovery.DiscoverAsync(CancellationToken.None);
        var user = result.Schema.ObjectTypes.Single(o => o.Name == "User");

        Assert.That(user.Attributes.Select(a => a.Name),
            Is.SupersetOf(new[] { "id", "externalId", "meta.lastModified", "meta.version" }));
    }

    [Test]
    public async Task DiscoverAsync_RecommendsIdAsTheExternalIdAndMarksItReadOnlyAsync()
    {
        var discovery = CreateDiscovery(DiscoveryHandler());

        var result = await discovery.DiscoverAsync(CancellationToken.None);
        var user = result.Schema.ObjectTypes.Single(o => o.Name == "User");

        Assert.That(user.RecommendedExternalIdAttribute, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(user.RecommendedExternalIdAttribute.Name, Is.EqualTo("id"));
            Assert.That(user.RecommendedExternalIdAttribute.Writability, Is.EqualTo(AttributeWritability.ReadOnly));
        }
    }

    [Test]
    public async Task DiscoverAsync_FlattensComplexAttributesFromTheProvidersSchemaAsync()
    {
        var discovery = CreateDiscovery(DiscoveryHandler());

        var result = await discovery.DiscoverAsync(CancellationToken.None);
        var user = result.Schema.ObjectTypes.Single(o => o.Name == "User");

        Assert.That(user.Attributes.Select(a => a.Name), Is.SupersetOf(new[] { "userName", "active", "name.givenName", "name.familyName" }));
        Assert.That(user.Attributes.Single(a => a.Name == "active").Type, Is.EqualTo(AttributeDataType.Boolean));
    }

    [Test]
    public async Task DiscoverAsync_PrefixesExtensionSchemaAttributesAsync()
    {
        var discovery = CreateDiscovery(DiscoveryHandler());

        var result = await discovery.DiscoverAsync(CancellationToken.None);
        var user = result.Schema.ObjectTypes.Single(o => o.Name == "User");

        Assert.That(user.Attributes.Select(a => a.Name), Does.Contain("enterpriseUser.department"));
    }

    [Test]
    public async Task DiscoverAsync_ExtensionAttributesRecordTheirOwningSchemaUrnAsync()
    {
        // Administrators need to see which schema an attribute came from when deciding what to manage.
        var discovery = CreateDiscovery(DiscoveryHandler());

        var result = await discovery.DiscoverAsync(CancellationToken.None);
        var department = result.Schema.ObjectTypes.Single(o => o.Name == "User").Attributes.Single(a => a.Name == "enterpriseUser.department");

        Assert.That(department.ClassName, Is.EqualTo(ScimUrns.EnterpriseUser));
    }

    #region fallbacks
    [Test]
    public async Task DiscoverAsync_NoSchemasEndpoint_FallsBackToTheCoreSchemasAndWarnsAsync()
    {
        var discovery = CreateDiscovery(DiscoveryHandler(schemas: null));

        var result = await discovery.DiscoverAsync(CancellationToken.None);
        var user = result.Schema.ObjectTypes.Single(o => o.Name == "User");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(user.Attributes.Select(a => a.Name), Is.SupersetOf(new[] { "userName", "displayName", "name.givenName", "emails.work" }));
            Assert.That(result.Warnings, Has.Exactly(1).Contains("Schemas"));
        }
    }

    [Test]
    public async Task DiscoverAsync_NoResourceTypesEndpoint_FallsBackToUsersAndGroupsAndWarnsAsync()
    {
        var discovery = CreateDiscovery(DiscoveryHandler(resourceTypes: null));

        var result = await discovery.DiscoverAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Schema.ObjectTypes.Select(o => o.Name), Is.EqualTo(new[] { "User", "Group" }));
            Assert.That(result.ResourceTypes.Select(r => r.Endpoint), Is.EqualTo(new[] { "/Users", "/Groups" }));
            Assert.That(result.Warnings, Has.Exactly(1).Contains("ResourceTypes"));
        }
    }

    [Test]
    public async Task DiscoverAsync_NoServiceProviderConfigEndpoint_UsesTheProtocolFloorsAndWarnsAsync()
    {
        var discovery = CreateDiscovery(DiscoveryHandler(serviceProviderConfig: null));

        var result = await discovery.DiscoverAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Capabilities.DiscoveryAvailable, Is.False);
            Assert.That(result.Capabilities.SupportsPatch, Is.False);
            Assert.That(result.Warnings, Has.Some.Contains("ServiceProviderConfig"));
        }
    }

    [Test]
    public async Task DiscoverAsync_SchemasReturnedAsABareArray_IsStillReadAsync()
    {
        // RFC 7644 requires a ListResponse, but providers exist that return the array directly.
        const string bareArray = """
        [ { "id": "urn:ietf:params:scim:schemas:core:2.0:User", "name": "User",
            "attributes": [ { "name": "userName", "type": "string" } ] } ]
        """;
        var discovery = CreateDiscovery(DiscoveryHandler(schemas: bareArray));

        var result = await discovery.DiscoverAsync(CancellationToken.None);
        var user = result.Schema.ObjectTypes.Single(o => o.Name == "User");

        Assert.That(user.Attributes.Select(a => a.Name), Does.Contain("userName"));
    }

    [Test]
    public async Task DiscoverAsync_SchemaMissingForAResourceType_StillPublishesTheObjectTypeAndWarnsAsync()
    {
        // The object type is kept so the administrator can see the provider advertises it; hiding it
        // would make a provider gap look like a JIM one.
        const string resourceTypes = """
        { "totalResults": 1, "Resources": [
            { "id": "Device", "name": "Device", "endpoint": "/Devices", "schema": "urn:example:schemas:Device" } ] }
        """;
        var discovery = CreateDiscovery(DiscoveryHandler(resourceTypes: resourceTypes));

        var result = await discovery.DiscoverAsync(CancellationToken.None);
        var device = result.Schema.ObjectTypes.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(device.Name, Is.EqualTo("Device"));
            Assert.That(device.Attributes.Select(a => a.Name), Does.Contain("id"));
            Assert.That(result.Warnings, Has.Some.Contains("Device"));
        }
    }

    [Test]
    public async Task DiscoverAsync_ResourceTypeWithNoName_IsSkippedAsync()
    {
        const string resourceTypes = """
        { "totalResults": 2, "Resources": [
            { "id": "User", "name": "User", "endpoint": "/Users", "schema": "urn:ietf:params:scim:schemas:core:2.0:User" },
            { "id": "Nameless", "endpoint": "/Nameless" } ] }
        """;
        var discovery = CreateDiscovery(DiscoveryHandler(resourceTypes: resourceTypes));

        var result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.That(result.Schema.ObjectTypes.Select(o => o.Name), Is.EqualTo(new[] { "User" }));
    }

    [Test]
    public async Task DiscoverAsync_TwoExtensionsDerivingTheSamePrefix_DisambiguatesByUrnAsync()
    {
        // Two vendors naming their extension the same must not collapse into one set of attributes.
        const string resourceTypes = """
        { "totalResults": 1, "Resources": [ {
            "id": "User", "name": "User", "endpoint": "/Users",
            "schema": "urn:ietf:params:scim:schemas:core:2.0:User",
            "schemaExtensions": [
              { "schema": "urn:example:a:2.0:Custom" },
              { "schema": "urn:example:b:2.0:Custom" } ] } ] }
        """;
        const string schemas = """
        { "totalResults": 3, "Resources": [
            { "id": "urn:ietf:params:scim:schemas:core:2.0:User", "name": "User", "attributes": [ { "name": "userName", "type": "string" } ] },
            { "id": "urn:example:a:2.0:Custom", "name": "Custom", "attributes": [ { "name": "field", "type": "string" } ] },
            { "id": "urn:example:b:2.0:Custom", "name": "Custom", "attributes": [ { "name": "field", "type": "string" } ] } ] }
        """;
        var discovery = CreateDiscovery(DiscoveryHandler(resourceTypes: resourceTypes, schemas: schemas));

        var result = await discovery.DiscoverAsync(CancellationToken.None);
        var user = result.Schema.ObjectTypes.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(user.Attributes.Select(a => a.Name), Does.Contain("custom.field"));
            Assert.That(user.Attributes.Select(a => a.Name), Does.Contain("urn:example:b:2.0:Custom.field"));
        }
    }

    [Test]
    public void DiscoverAsync_ProviderFailsWithAServerError_ThrowsRatherThanReturningAnEmptySchema()
    {
        // A 500 is not "the endpoint is absent". Absorbing it would persist an empty schema over a good
        // one and silently unmap every Attribute Flow.
        var discovery = CreateDiscovery(DiscoveryHandler(failureStatus: HttpStatusCode.InternalServerError));

        Assert.That(async () => await discovery.DiscoverAsync(CancellationToken.None),
            Throws.TypeOf<ScimRequestException>());
    }

    [Test]
    public void DiscoverAsync_ProviderRejectsTheCredential_ThrowsSoTheAdministratorSeesTheRealCause()
    {
        var discovery = CreateDiscovery(DiscoveryHandler(failureStatus: HttpStatusCode.Forbidden));

        Assert.That(async () => await discovery.DiscoverAsync(CancellationToken.None),
            Throws.TypeOf<ScimRequestException>());
    }
    #endregion

    [Test]
    public async Task DiscoverAsync_QueriesEachDiscoveryEndpointExactlyOnceAsync()
    {
        var handler = DiscoveryHandler();
        var discovery = CreateDiscovery(handler);

        await discovery.DiscoverAsync(CancellationToken.None);

        Assert.That(handler.Requests.Select(r => r.RequestUri!.AbsolutePath), Is.EqualTo(new[]
        {
            "/scim/v2/ServiceProviderConfig", "/scim/v2/ResourceTypes", "/scim/v2/Schemas"
        }));
    }
}
