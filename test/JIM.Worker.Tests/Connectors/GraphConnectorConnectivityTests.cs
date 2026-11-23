// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using JIM.Connectors.Graph;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Saving a Microsoft Graph Connected System's settings tests them against the tenant: a token is acquired, its
/// application permissions are read, and Graph is asked for one object, so a misconfiguration is reported when it is
/// made rather than by the first import.
/// </summary>
[TestFixture]
public class GraphConnectorConnectivityTests
{
    private const string TenantId = "7a1f0c2e-3b4d-4e5f-8a9b-0c1d2e3f4a5b";
    private const string ClientId = "1c2d3e4f-5a6b-4c7d-8e9f-a0b1c2d3e4f5";

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
    public void ValidateSettingValues_TenantReachableWithEveryPermission_ReturnsNoResults()
    {
        var handler = StubbedTransportGraphConnector.HealthyTransport();

        var results = new StubbedTransportGraphConnector(handler).ValidateSettingValues(Settings(), _logger);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Is.Empty);
            Assert.That(handler.Requests.Select(r => r.RequestUri!.AbsolutePath), Is.EqualTo(new[] { $"/{TenantId}/oauth2/v2.0/token", "/v1.0/users" }));
            Assert.That(handler.Requests[1].RequestUri!.Query, Does.Contain("$select=id").And.Contain("$top=1"));
        }
    }

    [Test]
    public void ValidateSettingValues_CertificateStillBeingAppliedThroughout_ReportsItAfterTheConnectionTestsWaits()
    {
        var handler = new StubHttpMessageHandler(_ => GraphTestTokens.TokenRefusal(700027));
        var connector = new StubbedTransportGraphConnector(handler);

        var results = connector.ValidateSettingValues(Settings(), _logger);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().ErrorMessage, Does.Contain("AADSTS700027"));
            Assert.That(connector.TokenWaits, Is.EqualTo(EntraTokenFailure.StillBeingAppliedRetryFor(GraphClientPurpose.ConnectionTest).Delays));
        }
    }

    [Test]
    public void ValidateSettingValues_TokenRefused_IsAnErrorDescribingEntraIdsReason()
    {
        var handler = new StubHttpMessageHandler(_ => GraphTestTokens.TokenRefusal(7000215));

        var results = new StubbedTransportGraphConnector(handler).ValidateSettingValues(Settings(), _logger);

        AssertSingleError(results, "AADSTS7000215");
    }

    [Test]
    public void ValidateSettingValues_TokenWithoutApplicationPermissions_IsAnErrorAndGraphIsNotCalled()
    {
        var handler = new StubHttpMessageHandler(request => GraphTestTokens.IsTokenRequest(request)
            ? GraphTestTokens.TokenResponse()
            : new HttpResponseMessage(HttpStatusCode.OK));

        var results = new StubbedTransportGraphConnector(handler).ValidateSettingValues(Settings(), _logger);

        using (Assert.EnterMultipleScope())
        {
            AssertSingleError(results, "admin consent");
            Assert.That(handler.CallCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void ValidateSettingValues_ReadOnlyPermissions_WarnsWithoutRefusing()
    {
        var handler = StubbedTransportGraphConnector.HealthyTransport("User.Read.All", "Group.Read.All");

        var results = new StubbedTransportGraphConnector(handler).ValidateSettingValues(Settings(), _logger);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].IsWarning, Is.True);
            Assert.That(results[0].ErrorMessage, Does.Contain("User.ReadWrite.All"));
        }
    }

    [Test]
    public void ValidateSettingValues_GroupsOnly_ReadsAGroupToProveAccess()
    {
        var handler = StubbedTransportGraphConnector.HealthyTransport("Group.ReadWrite.All");

        new StubbedTransportGraphConnector(handler).ValidateSettingValues(Settings(), _logger);

        Assert.That(handler.Requests.Last().RequestUri!.AbsolutePath, Is.EqualTo("/v1.0/groups"));
    }

    [Test]
    public void ValidateSettingValues_GraphRefusesTheRead_IsAnErrorCarryingGraphsReason()
    {
        var handler = new StubHttpMessageHandler(request => GraphTestTokens.IsTokenRequest(request)
            ? GraphTestTokens.TokenResponse(GraphTestTokens.AllPermissions)
            : new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}""", Encoding.UTF8, "application/json")
            });

        var results = new StubbedTransportGraphConnector(handler).ValidateSettingValues(Settings(), _logger);

        AssertSingleError(results, "Authorization_RequestDenied");
    }

    [Test]
    public void ValidateSettingValues_ShapeErrors_SkipTheConnectivityTest()
    {
        // Nothing is sent, the credential included, until the settings themselves are well formed.
        var handler = StubbedTransportGraphConnector.HealthyTransport();
        var settings = Settings();
        settings.Single(s => s.Setting.Name == GraphConnectorConstants.SettingClientId).StringValue = "not-a-guid";

        var results = new StubbedTransportGraphConnector(handler).ValidateSettingValues(settings, _logger);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(handler.CallCount, Is.Zero);
        }
    }

    [Test]
    public void ValidateSettingValues_RequiredValueMissing_SkipsTheConnectivityTest()
    {
        var handler = StubbedTransportGraphConnector.HealthyTransport();
        var settings = Settings();
        settings.Single(s => s.Setting.Name == GraphConnectorConstants.SettingClientSecret).StringEncryptedValue = null;

        var results = new StubbedTransportGraphConnector(handler).ValidateSettingValues(settings, _logger);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Is.Empty);
            Assert.That(handler.CallCount, Is.Zero);
        }
    }

    private static void AssertSingleError(List<ConnectorSettingValueValidationResult> results, string expectedInMessage)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results.Single().IsValid, Is.False);
            Assert.That(results.Single().SettingValue?.Setting.Name, Is.EqualTo(GraphConnectorConstants.SettingTenantId));
            Assert.That(results.Single().ErrorMessage, Does.Contain(expectedInMessage));
        }
    }

    private static List<ConnectedSystemSettingValue> Settings() =>
    [
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingTenantId }, StringValue = TenantId },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingClientId }, StringValue = ClientId },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingCloud }, StringValue = GraphConnectorConstants.CloudGlobal },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingAuthenticationMethod }, StringValue = GraphConnectorConstants.AuthMethodClientSecret },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingClientSecret }, StringEncryptedValue = "client-secret-value" }
    ];
}
