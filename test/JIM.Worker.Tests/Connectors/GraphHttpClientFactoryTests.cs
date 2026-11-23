// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Graph;
using JIM.Connectors.Http;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class GraphHttpClientFactoryTests
{
    private const string TenantId = "7a1f0c2e-3b4d-4e5f-8a9b-0c1d2e3f4a5b";
    private const string ClientId = "1c2d3e4f-5a6b-4c7d-8e9f-a0b1c2d3e4f5";

    [Test]
    public async Task CreateAuthentication_ClientSecret_RequestsATokenForTheCloudsGraphFromItsTokenServiceAsync()
    {
        var handler = new StubHttpMessageHandler(_ => GraphTestTokens.TokenResponse("User.Read.All"));
        using var tokenClient = new HttpClient(handler);
        var settings = Settings(cloud: GraphConnectorConstants.CloudUsGovernmentL4);

        var authentication = GraphHttpClientFactory.CreateAuthentication(settings, null, tokenClient, out var owned);
        await ApplyAsync(authentication);

        var tokenRequest = handler.Requests.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tokenRequest.RequestUri!.AbsoluteUri, Is.EqualTo($"https://login.microsoftonline.us/{TenantId}/oauth2/v2.0/token"));
            Assert.That(tokenRequest.Body, Does.Contain("client_secret=client-secret-value"));
            Assert.That(tokenRequest.Body, Does.Contain($"client_id={ClientId}"));
            Assert.That(tokenRequest.Body, Does.Contain(Uri.EscapeDataString("https://graph.microsoft.us/.default")));
            Assert.That(owned, Is.Null);
        }
    }

    [Test]
    public async Task CreateAuthentication_Certificate_SendsASignedAssertionAndOwnsTheCertificateAsync()
    {
        var handler = new StubHttpMessageHandler(_ => GraphTestTokens.TokenResponse("User.Read.All"));
        using var tokenClient = new HttpClient(handler);
        var settings = Settings(authenticationMethod: GraphConnectorConstants.AuthMethodCertificate, clientSecret: null,
            certificate: GraphTestCertificates.RsaPfx(), certificatePassword: GraphTestCertificates.Password);

        var authentication = GraphHttpClientFactory.CreateAuthentication(settings, null, tokenClient, out var owned);
        await ApplyAsync(authentication);
        owned?.Dispose();

        var body = handler.Requests.Single().Body;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(body, Does.Contain("client_assertion="));
            Assert.That(body, Does.Contain(Uri.EscapeDataString("urn:ietf:params:oauth:client-assertion-type:jwt-bearer")));
            Assert.That(body, Does.Not.Contain("client_secret"));
            Assert.That(owned, Is.Not.Null);
        }
    }

    // By name: the purpose is internal, and an NUnit test method must be public.
    [TestCase(nameof(GraphClientPurpose.Run))]
    [TestCase(nameof(GraphClientPurpose.ConnectionTest))]
    public void CreateAuthentication_CertificateStillBeingApplied_WaitsThePurposesDelaysBeforeReportingIt(string purposeName)
    {
        var purpose = Enum.Parse<GraphClientPurpose>(purposeName);
        using var tokenClient = new HttpClient(new StubHttpMessageHandler(_ => GraphTestTokens.TokenRefusal(700027)));
        var settings = Settings(authenticationMethod: GraphConnectorConstants.AuthMethodCertificate, clientSecret: null,
            certificate: GraphTestCertificates.RsaPfx(), certificatePassword: GraphTestCertificates.Password);
        var waits = new List<TimeSpan>();

        var authentication = GraphHttpClientFactory.CreateAuthentication(settings, null, tokenClient, out var owned, purpose: purpose,
            delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        var exception = Assert.ThrowsAsync<HttpAuthenticationException>(() => ApplyAsync(authentication));
        owned?.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Does.Contain("AADSTS700027"));
            Assert.That(waits, Is.EqualTo(EntraTokenFailure.StillBeingAppliedRetryFor(purpose).Delays));
        }
    }

    [Test]
    public void CreateAuthentication_UnusableCertificate_ThrowsWithTheReason()
    {
        using var tokenClient = new HttpClient(new StubHttpMessageHandler(_ => GraphTestTokens.TokenResponse()));
        var settings = Settings(authenticationMethod: GraphConnectorConstants.AuthMethodCertificate, clientSecret: null,
            certificate: GraphTestCertificates.RsaPfx(), certificatePassword: "wrong-password");

        var exception = Assert.Throws<HttpAuthenticationException>(() => GraphHttpClientFactory.CreateAuthentication(settings, null, tokenClient, out _));

        Assert.That(exception!.Message, Does.Contain(GraphConnectorConstants.SettingCertificatePassword));
    }

    [Test]
    public async Task CreateAuthentication_EncryptedSecret_IsDecryptedAsync()
    {
        var handler = new StubHttpMessageHandler(_ => GraphTestTokens.TokenResponse("User.Read.All"));
        using var tokenClient = new HttpClient(handler);
        var protection = new Mock<ICredentialProtection>();
        protection.Setup(p => p.Unprotect("protected")).Returns("decrypted-secret");

        var authentication = GraphHttpClientFactory.CreateAuthentication(Settings(clientSecret: "protected"), protection.Object, tokenClient, out _);
        await ApplyAsync(authentication);

        Assert.That(handler.Requests.Single().Body, Does.Contain("client_secret=decrypted-secret"));
    }

    [Test]
    public void CreateAuthentication_RefusedToken_IsDescribedFromEntraIdsErrorCode()
    {
        using var tokenClient = new HttpClient(new StubHttpMessageHandler(_ => GraphTestTokens.TokenRefusal(7000215)));
        var authentication = GraphHttpClientFactory.CreateAuthentication(Settings(), null, tokenClient, out _);

        var exception = Assert.ThrowsAsync<HttpAuthenticationException>(async () => await ApplyAsync(authentication));

        Assert.That(exception!.Message, Does.Contain("AADSTS7000215"));
    }

    [TestCase(null, ClientId, "client-secret-value", GraphConnectorConstants.SettingTenantId)]
    [TestCase(TenantId, null, "client-secret-value", GraphConnectorConstants.SettingClientId)]
    [TestCase(TenantId, ClientId, null, GraphConnectorConstants.SettingClientSecret)]
    public void CreateAuthentication_MissingValue_ThrowsNamingTheSetting(string? tenantId, string? clientId, string? clientSecret, string expected)
    {
        using var tokenClient = new HttpClient(new StubHttpMessageHandler(_ => GraphTestTokens.TokenResponse()));

        var exception = Assert.Throws<HttpAuthenticationException>(() =>
            GraphHttpClientFactory.CreateAuthentication(Settings(tenantId: tenantId, clientId: clientId, clientSecret: clientSecret), null, tokenClient, out _));

        Assert.That(exception!.Message, Does.Contain(expected));
    }

    [Test]
    public void CreateAuthentication_UnknownCloud_Throws()
    {
        using var tokenClient = new HttpClient(new StubHttpMessageHandler(_ => GraphTestTokens.TokenResponse()));

        var exception = Assert.Throws<HttpAuthenticationException>(() =>
            GraphHttpClientFactory.CreateAuthentication(Settings(cloud: "Germany"), null, tokenClient, out _));

        Assert.That(exception!.Message, Does.Contain(GraphConnectorConstants.SettingCloud));
    }

    [TestCase(null, GraphConnectorConstants.DefaultMaximumWritesPerSecond)]
    [TestCase(4, 4)]
    [TestCase(20, 20)]
    [TestCase(0, 1)]
    [TestCase(50, 20)]
    public void CreatePacer_WritesPerSecond_IsTheSettingKeptWithinTheQuota(int? setting, double expected)
    {
        var settings = Settings();
        settings.Add(new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingMaximumWritesPerSecond }, IntValue = setting });

        Assert.That(GraphHttpClientFactory.CreatePacer(settings).WritesPerSecond, Is.EqualTo(expected));
    }

    private static async Task ApplyAsync(IHttpAuthenticationStrategy authentication)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users");
        await authentication.ApplyAsync(request, CancellationToken.None);
    }

    private static List<ConnectedSystemSettingValue> Settings(
        string? tenantId = TenantId,
        string? clientId = ClientId,
        string? cloud = GraphConnectorConstants.CloudGlobal,
        string authenticationMethod = GraphConnectorConstants.AuthMethodClientSecret,
        string? clientSecret = "client-secret-value",
        string? certificate = null,
        string? certificatePassword = null) =>
    [
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingTenantId }, StringValue = tenantId },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingClientId }, StringValue = clientId },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingCloud }, StringValue = cloud },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingAuthenticationMethod }, StringValue = authenticationMethod },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingClientSecret }, StringEncryptedValue = clientSecret },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingCertificate }, StringEncryptedValue = certificate },
        new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingCertificatePassword }, StringEncryptedValue = certificatePassword }
    ];
}
