// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using JIM.Connectors.Graph;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class GraphConnectorSchemaTests
{
    private const string NoExtensions = """{"value":[]}""";

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

    [Test]
    public async Task GetSchemaAsync_OffersUsersAndGroupsAsync()
    {
        var schema = await GetSchemaAsync(Extensions(NoExtensions));

        Assert.That(schema.ObjectTypes.Select(t => t.Name), Is.EqualTo(new[] { "user", "group" }));
    }

    [TestCase("user")]
    [TestCase("group")]
    public async Task GetSchemaAsync_ExternalId_IsTheReadOnlyIdAsync(string objectType)
    {
        var type = (await GetSchemaAsync(Extensions(NoExtensions))).ObjectTypes.Single(t => t.Name == objectType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(type.RecommendedExternalIdAttribute.Name, Is.EqualTo("id"));
            Assert.That(type.RecommendedExternalIdAttribute.Writability, Is.EqualTo(AttributeWritability.ReadOnly));
            Assert.That(type.Attributes, Does.Contain(type.RecommendedExternalIdAttribute));
        }
    }

    [TestCase("user")]
    [TestCase("group")]
    public async Task GetSchemaAsync_AttributeNames_AreUniqueAsync(string objectType)
    {
        var type = (await GetSchemaAsync(Extensions(NoExtensions))).ObjectTypes.Single(t => t.Name == objectType);

        Assert.That(type.Attributes.Select(a => a.Name), Is.Unique);
    }

    [TestCase("user")]
    [TestCase("group")]
    public async Task GetSchemaAsync_SourceOfAuthority_IsAlwaysImportedReadOnlyAndConnectorDefinedAsync(string objectType)
    {
        var attribute = Attribute(await GetSchemaAsync(Extensions(NoExtensions)), objectType, "sourceOfAuthority");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attribute.AlwaysImported, Is.True);
            Assert.That(attribute.IsConnectorDefined, Is.True);
            Assert.That(attribute.Writability, Is.EqualTo(AttributeWritability.ReadOnly));
        }
    }

    [Test]
    public async Task GetSchemaAsync_User_HasTheFifteenExchangeCustomAttributesAsync()
    {
        var user = (await GetSchemaAsync(Extensions(NoExtensions))).ObjectTypes.Single(t => t.Name == "user");

        Assert.That(user.Attributes.Count(a => a.Name.StartsWith("extensionAttribute", StringComparison.Ordinal)), Is.EqualTo(15));
    }

    [Test]
    public async Task GetSchemaAsync_GroupMembers_IsAMultiValuedWritableReferenceAsync()
    {
        var members = Attribute(await GetSchemaAsync(Extensions(NoExtensions)), "group", "members");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(members.Type, Is.EqualTo(AttributeDataType.Reference));
            Assert.That(members.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
            Assert.That(members.Writability, Is.EqualTo(AttributeWritability.Writable));
            Assert.That(members.ReferencesObjectTypeName, Is.Null, "A member can be a user or a group.");
        }
    }

    [Test]
    public async Task GetSchemaAsync_UserManager_IsASingleValuedReferenceToAUserAsync()
    {
        var manager = Attribute(await GetSchemaAsync(Extensions(NoExtensions)), "user", "manager");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.Type, Is.EqualTo(AttributeDataType.Reference));
            Assert.That(manager.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
            Assert.That(manager.ReferencesObjectTypeName, Is.EqualTo("user"));
        }
    }

    [TestCase("user", "userPrincipalName")]
    [TestCase("user", "displayName")]
    [TestCase("user", "mailNickname")]
    [TestCase("user", "accountEnabled")]
    [TestCase("group", "displayName")]
    [TestCase("group", "mailNickname")]
    [TestCase("group", "mailEnabled")]
    [TestCase("group", "securityEnabled")]
    public async Task GetSchemaAsync_PropertyGraphRequiresOnCreate_IsRequiredAsync(string objectType, string name)
    {
        Assert.That(Attribute(await GetSchemaAsync(Extensions(NoExtensions)), objectType, name).Required, Is.True);
    }

    [Test]
    public async Task GetSchemaAsync_DirectoryExtensions_AreAddedToTheObjectTypesTheyTargetAsync()
    {
        var schema = await GetSchemaAsync(Extensions("""
            {"value":[
              {"name":"extension_0c1d2e3f_employeeNumber","dataType":"String","isMultiValued":false,"targetObjects":["User"]},
              {"name":"extension_0c1d2e3f_costCentres","dataType":"String","isMultiValued":true,"targetObjects":["User","Group"]},
              {"name":"extension_0c1d2e3f_badgeNumber","dataType":"LargeInteger","isMultiValued":false,"targetObjects":["User"]},
              {"name":"extension_0c1d2e3f_deviceThing","dataType":"String","isMultiValued":false,"targetObjects":["Device"]}
            ]}
            """));

        var user = schema.ObjectTypes.Single(t => t.Name == "user");
        var group = schema.ObjectTypes.Single(t => t.Name == "group");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Attribute(schema, "user", "extension_0c1d2e3f_employeeNumber").Type, Is.EqualTo(AttributeDataType.Text));
            Assert.That(Attribute(schema, "user", "extension_0c1d2e3f_costCentres").AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
            Assert.That(Attribute(schema, "user", "extension_0c1d2e3f_badgeNumber").Type, Is.EqualTo(AttributeDataType.LongNumber));
            Assert.That(group.Attributes.Select(a => a.Name), Does.Contain("extension_0c1d2e3f_costCentres"));
            Assert.That(group.Attributes.Select(a => a.Name), Does.Not.Contain("extension_0c1d2e3f_employeeNumber"));
            Assert.That(user.Attributes.Select(a => a.Name).Concat(group.Attributes.Select(a => a.Name)), Does.Not.Contain("extension_0c1d2e3f_deviceThing"));
            Assert.That(schema.Warnings, Is.Empty);
        }
    }

    [TestCase("Binary", AttributeDataType.Binary)]
    [TestCase("Boolean", AttributeDataType.Boolean)]
    [TestCase("DateTime", AttributeDataType.DateTime)]
    [TestCase("Integer", AttributeDataType.Number)]
    [TestCase("LargeInteger", AttributeDataType.LongNumber)]
    [TestCase("String", AttributeDataType.Text)]
    public async Task GetSchemaAsync_DirectoryExtensionDataType_MapsToJimsTypeAsync(string dataType, AttributeDataType expected)
    {
        var schema = await GetSchemaAsync(Extensions($$"""{"value":[{"name":"extension_a_x","dataType":"{{dataType}}","isMultiValued":false,"targetObjects":["User"]}]}"""));

        Assert.That(Attribute(schema, "user", "extension_a_x").Type, Is.EqualTo(expected));
    }

    [Test]
    public async Task GetSchemaAsync_DeletedOrUnknownTypeExtension_IsLeftOutAndAnUnknownTypeIsReportedAsync()
    {
        var schema = await GetSchemaAsync(Extensions("""
            {"value":[
              {"name":"extension_a_gone","dataType":"String","isMultiValued":false,"targetObjects":["User"],"deletedDateTime":"2026-01-01T00:00:00Z"},
              {"name":"extension_a_odd","dataType":"Quaternion","isMultiValued":false,"targetObjects":["User"]}
            ]}
            """));

        var names = schema.ObjectTypes.Single(t => t.Name == "user").Attributes.Select(a => a.Name).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(names, Does.Not.Contain("extension_a_gone"));
            Assert.That(names, Does.Not.Contain("extension_a_odd"));
            Assert.That(schema.Warnings.Single(), Does.Contain("extension_a_odd").And.Contain("Quaternion"));
        }
    }

    [Test]
    public async Task GetSchemaAsync_DiscoveryForbidden_KeepsTheBuiltInSchemaAndWarnsNamingThePermissionAsync()
    {
        // Discovering directory extensions needs Directory.Read.All, which the least-privilege permissions do not include.
        var schema = await GetSchemaAsync(request => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}""", Encoding.UTF8, "application/json")
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(schema.ObjectTypes, Has.Count.EqualTo(2));
            Assert.That(schema.Warnings.Single(), Does.Contain("Directory.Read.All"));
        }
    }

    [Test]
    public void GetSchemaAsync_DiscoveryFailsOtherwise_Throws()
    {
        // Anything but a refused permission is not a schema without extensions; it is a failure to report.
        Assert.ThrowsAsync<GraphRequestException>(async () => await GetSchemaAsync(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
    }

    [Test]
    public async Task GetSchemaAsync_AsksForEveryExtensionWithOneRequestAsync()
    {
        var handler = new StubHttpMessageHandler(request => GraphTestTokens.IsTokenRequest(request)
            ? GraphTestTokens.TokenResponse(GraphTestTokens.AllPermissions)
            : Json(NoExtensions));

        await new StubbedTransportGraphConnector(handler).GetSchemaAsync(Settings(), _logger);

        var graphRequests = handler.Requests.Where(r => !r.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(graphRequests, Has.Count.EqualTo(1));
            Assert.That(graphRequests[0].Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(graphRequests[0].RequestUri!.AbsolutePath, Is.EqualTo("/v1.0/directoryObjects/getAvailableExtensionProperties"));
        }
    }

    private Task<ConnectorSchema> GetSchemaAsync(Func<HttpRequestMessage, HttpResponseMessage> graph)
    {
        var handler = new StubHttpMessageHandler(request => GraphTestTokens.IsTokenRequest(request)
            ? GraphTestTokens.TokenResponse(GraphTestTokens.AllPermissions)
            : graph(request));

        return new StubbedTransportGraphConnector(handler).GetSchemaAsync(Settings(), _logger);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Extensions(string json) => _ => Json(json);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ConnectorSchemaAttribute Attribute(ConnectorSchema schema, string objectType, string name)
    {
        var attribute = schema.ObjectTypes.Single(t => t.Name == objectType).Attributes.SingleOrDefault(a => a.Name == name);
        Assert.That(attribute, Is.Not.Null, $"Expected {objectType} to have an attribute named '{name}'.");
        return attribute!;
    }

    private static List<ConnectedSystemSettingValue> Settings() =>
    [
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingTenantId }, StringValue = "7a1f0c2e-3b4d-4e5f-8a9b-0c1d2e3f4a5b" },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingClientId }, StringValue = "1c2d3e4f-5a6b-4c7d-8e9f-a0b1c2d3e4f5" },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingAuthenticationMethod }, StringValue = GraphConnectorConstants.AuthMethodClientSecret },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingClientSecret }, StringEncryptedValue = "client-secret-value" }
    ];
}
