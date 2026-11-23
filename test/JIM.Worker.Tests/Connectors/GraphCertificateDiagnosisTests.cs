// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Authentication;
using JIM.Connectors.Graph;
using JIM.Models.Connectors;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// What an administrator is told when the certificate Microsoft Entra ID or Microsoft Graph presents is refused.
/// Their certificates chain to a public certificate authority, so in practice that means a TLS-inspecting proxy is
/// presenting its own; showing that certificate lets the administrator trust it, as for any other Connector.
/// </summary>
[TestFixture]
public class GraphCertificateDiagnosisTests
{
    private const string TenantId = "7a1f0c2e-3b4d-4e5f-8a9b-0c1d2e3f4a5b";
    private const string ClientId = "1c2d3e4f-5a6b-4c7d-8e9f-a0b1c2d3e4f5";
    private const string Thumbprint = "AABBCCDDEEFF00112233445566778899AABBCCDD";

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
    public void ResolveSecureEndpoint_GlobalCloud_IsTheTokenServiceReachedAsJimReachesIt()
    {
        var endpoint = new GraphConnector().ResolveSecureEndpoint(Settings());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(endpoint, Is.Not.Null);
            Assert.That(endpoint!.Host, Is.EqualTo("login.microsoftonline.com"));
            Assert.That(endpoint!.Port, Is.EqualTo(443));
            Assert.That(endpoint!.HandshakeFraming, Is.EqualTo(SecureHandshakeFraming.Https), "read through the outbound proxy, as the Connector connects");
            Assert.That(endpoint!.Timeout, Is.EqualTo(TimeSpan.FromSeconds(GraphConnectorConstants.DefaultConnectionTimeoutSeconds)));
        }
    }

    [Test]
    public void ResolveSecureEndpoint_NationalCloud_IsThatCloudsTokenService()
    {
        var endpoint = new GraphConnector().ResolveSecureEndpoint(Settings(cloud: GraphConnectorConstants.CloudUsGovernmentL4));

        Assert.That(endpoint!.Host, Is.EqualTo("login.microsoftonline.us"));
    }

    [Test]
    public void ValidateSettingValues_TokenServiceCertificateRefused_ReportsTheCertificate()
    {
        var connector = new StubbedTransportGraphConnector(RefusingHandshake()) { ProbedDiagnostic = UntrustedIssuer() };

        var failure = connector.ValidateSettingValues(Settings(), _logger).Single();

        var rejection = failure.Exception as ServerCertificateRejectedException;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.IsValid, Is.False);
            Assert.That(rejection, Is.Not.Null, "the portal shows the certificate, with the option to trust it");
            Assert.That(rejection!.Diagnostic.Thumbprint, Is.EqualTo(Thumbprint));
            Assert.That(failure.ErrorMessage, Does.Contain("Admin > Certificates"));
            Assert.That(connector.ProbedHosts, Is.EqualTo(new[] { "login.microsoftonline.com" }));
        }
    }

    [Test]
    public void ValidateSettingValues_CertificateThatTurnsOutToBeFine_KeepsTheConnectionMessage()
    {
        var connector = new StubbedTransportGraphConnector(RefusingHandshake())
        {
            ProbedDiagnostic = new ServerCertificateDiagnostic { Host = "login.microsoftonline.com", Port = 443, FailureReason = ServerCertificateFailureReason.None }
        };

        var failure = connector.ValidateSettingValues(Settings(), _logger).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure.Exception, Is.Not.InstanceOf<ServerCertificateRejectedException>());
            Assert.That(failure.ErrorMessage, Does.Contain("token endpoint"));
        }
    }

    [Test]
    public void ImportAsync_GraphCertificateRefused_FailsTheRunWithTheCertificateWithoutRetrying()
    {
        // The token is issued and Graph itself is refused: the failure that reaches the Activity names the certificate.
        var graphHandshakes = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (GraphTestTokens.IsTokenRequest(request))
                return GraphTestTokens.TokenResponse();

            graphHandshakes++;
            throw HandshakeRefused();
        });
        var connector = new StubbedTransportGraphConnector(handler, maxRetries: 3) { ProbedDiagnostic = UntrustedIssuer() };
        connector.OpenImportConnection(Settings(), null, _logger);

        var exception = Assert.ThrowsAsync<ServerCertificateRejectedException>(async () =>
            await connector.ImportAsync(ConnectedSystem(), FullImport(), [], null, _logger, CancellationToken.None, new RecordingConnectorProgress()));
        connector.CloseImportConnection();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Diagnostic.Thumbprint, Is.EqualTo(Thumbprint));
            Assert.That(graphHandshakes, Is.EqualTo(1), "a refused certificate is not a transient fault");
        }
    }

    [Test]
    public void ExportAsync_TokenServiceCertificateRefused_FailsTheRunWithTheCertificate()
    {
        var connector = new StubbedTransportGraphConnector(RefusingHandshake()) { ProbedDiagnostic = UntrustedIssuer() };
        connector.OpenExportConnection(Settings(), null);

        var exception = Assert.ThrowsAsync<ServerCertificateRejectedException>(async () =>
            await connector.ExportAsync([DeleteExport()], CancellationToken.None, new RecordingConnectorProgress()));
        connector.CloseExportConnection();

        Assert.That(exception!.Diagnostic.Thumbprint, Is.EqualTo(Thumbprint));
    }

    [Test]
    public async Task RunPasswordPreflightAsync_TokenServiceCertificateRefused_FailsTheConnectionCheckNamingTheCertificateAsync()
    {
        var connector = new StubbedTransportGraphConnector(RefusingHandshake()) { ProbedDiagnostic = UntrustedIssuer() };

        var result = await connector.RunPasswordPreflightAsync(Settings(), [], _logger, CancellationToken.None);

        var connection = result.Checks.Single(c => c.Check == PasswordPreflightCheck.Connection);
        Assert.That(connection.Message, Does.Contain("Admin > Certificates"));
    }

    private static StubHttpMessageHandler RefusingHandshake() => new(_ => throw HandshakeRefused());

    private static HttpRequestException HandshakeRefused() =>
        new("The SSL connection could not be established, see inner exception.", new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback."));

    private static ServerCertificateDiagnostic UntrustedIssuer() => new()
    {
        Host = "login.microsoftonline.com",
        Port = 443,
        Subject = "CN=login.microsoftonline.com",
        Issuer = "CN=Corp Inspecting Proxy CA",
        Thumbprint = Thumbprint,
        FailureReason = ServerCertificateFailureReason.UntrustedIssuer,
        Remediation = "The issuing certificate authority is not trusted. Add it to the JIM certificate store (Admin > Certificates)."
    };

    private static ConnectedSystem ConnectedSystem() => new()
    {
        Name = "Entra ID",
        SettingValues = Settings(),
        ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Name = "user",
                Selected = true,
                Attributes = [new ConnectedSystemObjectTypeAttribute { Name = "id", Selected = true, IsExternalId = true, Type = AttributeDataType.Text }]
            }
        ]
    };

    private static ConnectedSystemRunProfile FullImport() => new() { Name = "Full Import", RunType = ConnectedSystemRunType.FullImport };

    private static PendingExport DeleteExport()
    {
        var userType = new ConnectedSystemObjectType { Id = 1, Name = "user", Selected = true };
        var id = new ConnectedSystemObjectTypeAttribute { Id = 99, Name = "id", IsExternalId = true, Type = AttributeDataType.Text, ConnectedSystemObjectType = userType };
        return new PendingExport
        {
            ChangeType = PendingExportChangeType.Delete,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Type = userType,
                TypeId = userType.Id,
                ExternalIdAttributeId = id.Id,
                AttributeValues = [new ConnectedSystemObjectAttributeValue { Attribute = id, AttributeId = id.Id, StringValue = "00000000-0000-0000-0000-000000000001" }]
            }
        };
    }

    private static List<ConnectedSystemSettingValue> Settings(string cloud = GraphConnectorConstants.CloudGlobal) =>
    [
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingTenantId }, StringValue = TenantId },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingClientId }, StringValue = ClientId },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingCloud }, StringValue = cloud },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingAuthenticationMethod }, StringValue = GraphConnectorConstants.AuthMethodClientSecret },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingClientSecret }, StringEncryptedValue = "client-secret-value" }
    ];
}
