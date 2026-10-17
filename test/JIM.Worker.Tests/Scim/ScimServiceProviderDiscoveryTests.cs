// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Messages;
using JIM.Scim.Schema;
using JIM.Scim.Serialisation;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// The discovery documents JIM's own service provider publishes (RFC 7643 sections 5 to 7), read back the way the
/// shipped SCIM 2.0 Client Connector reads any provider's. Client and server derive their attributes through one
/// composer, so what the server publishes must flatten to exactly what the client would compose from the core
/// schemas, less the write-only attributes the server refuses (D5).
/// </summary>
public class ScimServiceProviderDiscoveryTests
{
    private const string BaseUrl = "https://jim.example.com/scim/v2/42";

    [Test]
    public void ServiceProviderConfig_ReadByTheClient_AdvertisesExactlyWhatTheServerSupports()
    {
        var config = RoundTrip(ScimServiceProviderDiscovery.ServiceProviderConfig(BaseUrl, maxResults: 200));
        var capabilities = ScimProviderCapabilities.From(config);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(capabilities.SupportsPatch, Is.True);
            Assert.That(capabilities.SupportsFilter, Is.True);
            Assert.That(capabilities.FilterMaxResults, Is.EqualTo(200));
            Assert.That(capabilities.SupportsETag, Is.True);
            Assert.That(capabilities.SupportsBulk, Is.False);
            Assert.That(capabilities.SupportsSort, Is.False);
            Assert.That(capabilities.SupportsChangePassword, Is.False);
            Assert.That(capabilities.AuthenticationSchemes, Is.EqualTo(new[] { "oauthbearertoken" }));
            Assert.That(capabilities.Warnings, Is.Empty);
        }
    }

    [Test]
    public void ServiceProviderConfig_Meta_LocatesItUnderTheBaseUrl()
    {
        var config = ScimServiceProviderDiscovery.ServiceProviderConfig(BaseUrl + "/", maxResults: 200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.Schemas, Is.EqualTo(new[] { ScimUrns.ServiceProviderConfig }));
            Assert.That(config.Meta?.ResourceType, Is.EqualTo("ServiceProviderConfig"));
            Assert.That(config.Meta?.Location, Is.EqualTo(BaseUrl + "/ServiceProviderConfig"));
        }
    }

    [Test]
    public void ServiceProviderConfig_NonPositiveMaxResults_IsRefused()
    {
        Assert.That(() => ScimServiceProviderDiscovery.ServiceProviderConfig(BaseUrl, maxResults: 0), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void ResourceTypes_AreUserWithTheEnterpriseExtensionAndGroup()
    {
        var resourceTypes = ScimServiceProviderDiscovery.ResourceTypes(BaseUrl);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resourceTypes.Select(r => (r.Name, r.Endpoint, r.Schema)), Is.EqualTo(new[]
            {
                ("User", "/Users", ScimUrns.User),
                ("Group", "/Groups", ScimUrns.Group)
            }));
            Assert.That(resourceTypes[0].SchemaExtensions.Select(e => e.Schema), Is.EqualTo(new[] { ScimUrns.EnterpriseUser }));
            Assert.That(resourceTypes.Select(r => r.Meta?.Location), Is.EqualTo(new[] { BaseUrl + "/ResourceTypes/User", BaseUrl + "/ResourceTypes/Group" }));
            Assert.That(resourceTypes, Has.All.Property(nameof(ScimResourceType.Schemas)).EqualTo(new[] { ScimUrns.ResourceType }));
        }
    }

    [Test]
    public void Schemas_PublishNoWriteOnlyAttributeAtAnyLevel()
    {
        // password is write-only and the server refuses it (D5), so publishing it would invite a client to send it.
        var schemas = ScimServiceProviderDiscovery.Schemas(BaseUrl);

        var writeOnly = schemas.SelectMany(s => Descendants(s.Attributes))
            .Where(a => string.Equals(a.Mutability, ScimMutability.WriteOnly, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Name)
            .ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(writeOnly, Is.Empty);
            Assert.That(schemas.Single(s => s.Id == ScimUrns.User).Attributes.Select(a => a.Name), Does.Not.Contain("password"));
        }
    }

    [Test]
    public void Schemas_DoNotChangeTheCoreDefinitionsTheyAreBuiltFrom()
    {
        // Each call builds its own copies, so leaving password out of the published User schema must not reach the
        // definitions the client falls back on when a provider publishes no /Schemas.
        ScimServiceProviderDiscovery.Schemas(BaseUrl);

        Assert.That(ScimCoreSchemas.User().Attributes.Select(a => a.Name), Does.Contain("password"));
    }

    [Test]
    public void Schemas_Meta_LocatesEachByItsUrn()
    {
        var schemas = ScimServiceProviderDiscovery.Schemas(BaseUrl);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(schemas.Select(s => s.Id), Is.EquivalentTo(new[] { ScimUrns.User, ScimUrns.EnterpriseUser, ScimUrns.Group }));
            foreach (var schema in schemas)
            {
                Assert.That(schema.Meta?.ResourceType, Is.EqualTo("Schema"), schema.Id);
                Assert.That(schema.Meta?.Location, Is.EqualTo($"{BaseUrl}/Schemas/{schema.Id}"), schema.Id);
            }
        }
    }

    [Test]
    public void PublishedDocuments_ComposedByTheClient_GiveTheCoreAttributesLessTheWriteOnlyOnes()
    {
        var resourceTypes = RoundTripList(ScimServiceProviderDiscovery.ResourceTypes(BaseUrl));
        var schemas = RoundTripList(ScimServiceProviderDiscovery.Schemas(BaseUrl));

        var published = ScimResourceTypeComposer.Compose(resourceTypes, schemas);
        var core = ScimResourceTypeComposer.Compose(ScimCoreSchemas.ResourceTypes(), [ScimCoreSchemas.User(), ScimCoreSchemas.EnterpriseUser(), ScimCoreSchemas.Group()]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(published.Warnings, Is.Empty);
            Assert.That(published.FlattenedAttributes.Keys, Is.EquivalentTo(new[] { "User", "Group" }));
            foreach (var (resourceType, attributes) in core.FlattenedAttributes)
            {
                var expected = attributes.Where(a => a.Name != "password").Select(Describe).ToList();
                Assert.That(published.FlattenedAttributes[resourceType].Select(Describe).ToList(), Is.EqualTo(expected), resourceType);
            }
        }
    }

    [Test]
    public void Compose_ResourceTypeWithNoPublishedSchemaJimKnows_KeepsItWithTheCommonAttributesAndWarns()
    {
        var resourceType = new ScimResourceType { Name = "Device", Endpoint = "/Devices", Schema = "urn:example:Device" };

        var composition = ScimResourceTypeComposer.Compose([resourceType], []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(composition.FlattenedAttributes["Device"].Select(a => a.Name), Is.EquivalentTo(ScimCommonAttributes.For("urn:example:Device").Select(a => a.Name)));
            Assert.That(composition.Warnings, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Compose_ResourceTypeWithNoName_IsLeftOut()
    {
        var composition = ScimResourceTypeComposer.Compose([new ScimResourceType { Schema = ScimUrns.User }], []);

        Assert.That(composition.FlattenedAttributes, Is.Empty);
    }

    private static string Describe(ScimFlattenedAttribute attribute) =>
        $"{attribute.Name}|{attribute.ScimPath}|{attribute.Type}|{attribute.AttributePlurality}|{attribute.Writability}|{attribute.CaseExact}|{attribute.Access}|{attribute.ExtensionUrn}";

    private static IEnumerable<ScimSchemaAttribute> Descendants(IEnumerable<ScimSchemaAttribute> attributes) =>
        attributes.SelectMany(a => new[] { a }.Concat(Descendants(a.SubAttributes)));

    private static T RoundTrip<T>(T document) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(document, ScimJson.Options), ScimJson.Options)!;

    /// <summary>
    /// A collection as the server serves it, in a ListResponse, read back as the client reads one.
    /// </summary>
    private static List<T> RoundTripList<T>(List<T> resources)
    {
        var response = new ScimListResponse<T> { Resources = resources, TotalResults = resources.Count };
        return RoundTrip(response).Resources;
    }
}
