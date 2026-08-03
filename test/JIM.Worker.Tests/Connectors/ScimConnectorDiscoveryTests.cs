// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using JIM.Connectors.SCIM;
using JIM.Models.Staging;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The connector's schema retrieval and its live connectivity test, both driven through a stubbed
/// transport so no network is involved.
/// </summary>
[TestFixture]
public class ScimConnectorDiscoveryTests
{
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

    private static ConnectedSystemSettingValue Setting(string name, string? stringValue = null, int? intValue = null)
    {
        return new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = name },
            StringValue = stringValue,
            IntValue = intValue
        };
    }

    private static List<ConnectedSystemSettingValue> Settings(string baseUrl = "https://provider.example.com/scim/v2")
    {
        return
        [
            Setting(ScimConnectorConstants.SettingBaseUrl, baseUrl),
            Setting(ScimConnectorConstants.SettingAuthenticationMethod, ScimConnectorConstants.AuthMethodStaticBearerToken),
            Setting(ScimConnectorConstants.SettingBearerToken, "token")
        ];
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/scim+json")
        };
    }

    private static HttpResponseMessage Status(HttpStatusCode status)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(string.Empty) };
    }

    private static StubHttpMessageHandler ConformantProvider()
    {
        return new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/ServiceProviderConfig", StringComparison.Ordinal))
                return Json("""{ "patch": { "supported": true }, "filter": { "supported": true } }""");
            if (path.EndsWith("/ResourceTypes", StringComparison.Ordinal))
                return Json("""
                { "totalResults": 1, "Resources": [
                    { "id": "User", "name": "User", "endpoint": "/Users", "schema": "urn:ietf:params:scim:schemas:core:2.0:User" } ] }
                """);
            if (path.EndsWith("/Schemas", StringComparison.Ordinal))
                return Json("""
                { "totalResults": 1, "Resources": [
                    { "id": "urn:ietf:params:scim:schemas:core:2.0:User", "name": "User",
                      "attributes": [ { "name": "userName", "type": "string", "required": true } ] } ] }
                """);
            return Status(HttpStatusCode.NotFound);
        });
    }

    #region GetSchemaAsync
    [Test]
    public async Task GetSchemaAsync_ConformantProvider_ReturnsTheDiscoveredObjectTypesAsync()
    {
        using var handler = ConformantProvider();
        var connector = new StubbedTransportScimConnector(handler);

        var schema = await connector.GetSchemaAsync(Settings(), _logger);

        Assert.That(schema.ObjectTypes.Select(o => o.Name), Is.EqualTo(new[] { "User" }));
        Assert.That(schema.ObjectTypes[0].Attributes.Select(a => a.Name), Is.SupersetOf(new[] { "id", "userName" }));
    }

    [Test]
    public async Task GetSchemaAsync_ProviderPublishesNothing_StillReturnsTheCoreSchemaAsync()
    {
        // Every discovery document is optional in practice. A provider that serves resources but no
        // discovery documents is still usable against the RFC's own schema definitions.
        using var handler = new StubHttpMessageHandler(_ => Status(HttpStatusCode.NotFound));
        var connector = new StubbedTransportScimConnector(handler);

        var schema = await connector.GetSchemaAsync(Settings(), _logger);

        Assert.That(schema.ObjectTypes.Select(o => o.Name), Is.EqualTo(new[] { "User", "Group" }));
    }

    [Test]
    public async Task GetSchemaAsync_ProviderPublishesNothing_ReportsTheShortfallOnTheSchemaAsync()
    {
        // Falling back to the core schemas is a workaround, not a clean result; the schema carries the
        // warnings so the import's Activity and refresh result can put them in front of the administrator.
        using var handler = new StubHttpMessageHandler(_ => Status(HttpStatusCode.NotFound));
        var connector = new StubbedTransportScimConnector(handler);

        var schema = await connector.GetSchemaAsync(Settings(), _logger);

        Assert.That(schema.Warnings, Is.Not.Empty);
    }

    [Test]
    public async Task GetSchemaAsync_ConformantProvider_ReportsNoWarningsAsync()
    {
        using var handler = ConformantProvider();
        var connector = new StubbedTransportScimConnector(handler);

        var schema = await connector.GetSchemaAsync(Settings(), _logger);

        Assert.That(schema.Warnings, Is.Empty);
    }

    [Test]
    public void GetSchemaAsync_ProviderFails_PropagatesRatherThanReturningAnEmptySchema()
    {
        // Persisting an empty schema over a good one would unmap every Attribute Flow pointing at it.
        using var handler = new StubHttpMessageHandler(_ => Status(HttpStatusCode.InternalServerError));
        var connector = new StubbedTransportScimConnector(handler);

        Assert.That(async () => await connector.GetSchemaAsync(Settings(), _logger),
            Throws.TypeOf<ScimRequestException>());
    }
    #endregion

    #region connectivity test
    [Test]
    public void ValidateSettingValues_ProviderRespondsToDiscovery_ReportsNoProblem()
    {
        using var handler = ConformantProvider();
        var connector = new StubbedTransportScimConnector(handler);

        var results = connector.ValidateSettingValues(Settings(), _logger);

        Assert.That(results, Is.Empty);
    }

    [Test]
    public void ValidateSettingValues_NoDiscoveryEndpointAnswers_ReportsTheBaseUrlAsWrong()
    {
        // Three 404s in a row is the signature of a base URL pointing somewhere that is not a SCIM
        // service provider, which is exactly the typo worth catching at configuration time.
        using var handler = new StubHttpMessageHandler(_ => Status(HttpStatusCode.NotFound));
        var connector = new StubbedTransportScimConnector(handler);

        var results = connector.ValidateSettingValues(Settings(), _logger);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].IsValid, Is.False);
            Assert.That(results[0].ErrorMessage, Does.Contain("SCIM"));
            Assert.That(results[0].SettingValue?.Setting.Name, Is.EqualTo(ScimConnectorConstants.SettingBaseUrl));
        });
    }

    [Test]
    public void ValidateSettingValues_ProviderRejectsTheCredential_ReportsTheFailure()
    {
        using var handler = new StubHttpMessageHandler(_ => Status(HttpStatusCode.Unauthorized));
        var connector = new StubbedTransportScimConnector(handler);

        var results = connector.ValidateSettingValues(Settings(), _logger);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].IsValid, Is.False);
    }

    [Test]
    public void ValidateSettingValues_ConnectionFails_ReportsTheFailureRatherThanThrowing()
    {
        // A validation call that throws would surface as an unhandled error in the admin portal instead
        // of a message beside the setting.
        using var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("No such host is known."));
        var connector = new StubbedTransportScimConnector(handler);

        var results = connector.ValidateSettingValues(Settings(), _logger);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].IsValid, Is.False);
    }

    [Test]
    public void ValidateSettingValues_BaseUrlIsMalformed_ReportsTheShapeProblemWithoutAttemptingAConnection()
    {
        using var handler = new StubHttpMessageHandler(_ => Status(HttpStatusCode.OK));
        var connector = new StubbedTransportScimConnector(handler);

        var results = connector.ValidateSettingValues(Settings("not-a-url"), _logger);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].IsValid, Is.False);
            Assert.That(handler.CallCount, Is.Zero);
        });
    }

    [Test]
    public void ValidateSettingValues_NoBaseUrlSupplied_MakesNoConnectionAttempt()
    {
        // The generic validator already reports the missing value; connecting to nowhere would only add
        // a confusing second message.
        using var handler = new StubHttpMessageHandler(_ => Status(HttpStatusCode.OK));
        var connector = new StubbedTransportScimConnector(handler);

        var results = connector.ValidateSettingValues([Setting(ScimConnectorConstants.SettingBaseUrl)], _logger);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.Empty);
            Assert.That(handler.CallCount, Is.Zero);
        });
    }

    [Test]
    public void ValidateSettingValues_ProviderPublishesOnlySchemas_IsAcceptedAsReachable()
    {
        // Reaching any one discovery endpoint proves the URL, the transport and the credential all work.
        using var handler = new StubHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/Schemas", StringComparison.Ordinal)
                ? Json("""{ "totalResults": 0, "Resources": [] }""")
                : Status(HttpStatusCode.NotFound));
        var connector = new StubbedTransportScimConnector(handler);

        var results = connector.ValidateSettingValues(Settings(), _logger);

        Assert.That(results, Is.Empty);
    }
    #endregion
}
