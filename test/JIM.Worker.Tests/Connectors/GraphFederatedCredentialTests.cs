// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using System.Web;
using JIM.Connectors.Graph;
using JIM.Connectors.Http;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Federated credentials: the app registration trusts the token of a user-assigned managed identity of the Azure virtual
/// machine JIM runs on, instead of JIM holding a secret or a certificate.
/// </summary>
[TestFixture]
public class GraphFederatedCredentialTests
{
    private const string ManagedIdentityClientId = "3b9f6a1e-2c4d-4e8f-9a0b-1c2d3e4f5a6b";
    private static readonly Uri TokenEndpoint = new("https://login.microsoftonline.com/tenant/oauth2/v2.0/token");
    [Test]
    public async Task ManagedIdentity_AsksTheInstanceMetadataServiceForATokenForTheExchangeAudienceAsync()
    {
        var handler = IdentityHandler("mi-token");
        var source = new GraphManagedIdentityTokenSource(new HttpClient(handler), ManagedIdentityClientId, GraphCloud.Global.FederatedTokenAudience);

        var parameters = await ParametersAsync(source);

        var request = handler.Requests.Single();
        var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parameters["client_assertion_type"], Is.EqualTo("urn:ietf:params:oauth:client-assertion-type:jwt-bearer"));
            Assert.That(parameters["client_assertion"], Is.EqualTo("mi-token"));
            Assert.That(request.RequestUri!.GetLeftPart(UriPartial.Path), Is.EqualTo("http://169.254.169.254/metadata/identity/oauth2/token"));
            Assert.That(query["resource"], Is.EqualTo("api://AzureADTokenExchange"));
            Assert.That(query["client_id"], Is.EqualTo(ManagedIdentityClientId));
            Assert.That(request.Headers.GetValues("Metadata").Single(), Is.EqualTo("true"));
        }
    }

    [TestCase("US Government L4 (GCC High)", "api://AzureADTokenExchangeUSGov")]
    [TestCase("US Government L5 (DoD)", "api://AzureADTokenExchangeUSGov")]
    [TestCase("China (operated by 21Vianet)", "api://AzureADTokenExchangeChina")]
    [TestCase("Global", "api://AzureADTokenExchange")]
    public void FederatedTokenAudience_FollowsTheCloud(string cloudName, string expected)
    {
        GraphCloud.TryResolve(cloudName, out var cloud);

        Assert.That(cloud.FederatedTokenAudience, Is.EqualTo(expected));
    }

    [Test]
    public void ManagedIdentity_ServiceUnreachable_SaysJimIsNotRunningWithAManagedIdentity()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Connection refused"));
        var source = new GraphManagedIdentityTokenSource(new HttpClient(handler), ManagedIdentityClientId, GraphCloud.Global.FederatedTokenAudience);

        var ex = Assert.ThrowsAsync<HttpAuthenticationException>(async () => await ParametersAsync(source));

        Assert.That(ex!.Message, Does.Contain("managed identity").IgnoreCase);
    }

    [Test]
    public void ManagedIdentity_Refused_NamesTheClientIdSetting()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"invalid_request","error_description":"Identity not found"}""", Encoding.UTF8, "application/json")
        });
        var source = new GraphManagedIdentityTokenSource(new HttpClient(handler), ManagedIdentityClientId, GraphCloud.Global.FederatedTokenAudience);

        var ex = Assert.ThrowsAsync<HttpAuthenticationException>(async () => await ParametersAsync(source));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain(GraphConnectorConstants.SettingManagedIdentityClientId));
            Assert.That(ex.Message, Does.Contain("Identity not found"));
        }
    }

    [Test]
    public async Task Factory_FederatedCredentialWithAManagedIdentity_SendsItsTokenAsTheClientAssertionAsync()
    {
        var identity = IdentityHandler("mi-token");
        var tokens = new StubHttpMessageHandler(_ => GraphTestTokens.TokenResponse());
        var settings = Settings(
            (GraphConnectorConstants.SettingAuthenticationMethod, GraphConnectorConstants.AuthMethodFederatedCredential),
            (GraphConnectorConstants.SettingManagedIdentityClientId, ManagedIdentityClientId));

        var authentication = GraphHttpClientFactory.CreateAuthentication(settings, null, new HttpClient(tokens), out var owned, identity);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users");
        await authentication.ApplyAsync(request, CancellationToken.None);
        owned?.Dispose();

        var form = HttpUtility.ParseQueryString(tokens.Requests.Single().Body!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(form["client_assertion"], Is.EqualTo("mi-token"));
            Assert.That(form["client_secret"], Is.Null);
        }
    }

    [Test]
    public void Settings_OfferFederatedCredentialWithAManagedIdentity()
    {
        var settings = new GraphConnector().GetSettings();
        var method = settings.Single(s => s.Name == GraphConnectorConstants.SettingAuthenticationMethod);
        var clientId = settings.Single(s => s.Name == GraphConnectorConstants.SettingManagedIdentityClientId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(method.DropDownValues, Does.Contain(GraphConnectorConstants.AuthMethodFederatedCredential));
            Assert.That(clientId.RequiredWhenSetting, Is.EqualTo(GraphConnectorConstants.SettingAuthenticationMethod));
            Assert.That(clientId.RequiredWhenValue, Is.EqualTo(GraphConnectorConstants.AuthMethodFederatedCredential));
        }
    }

    [Test]
    public void Validation_ManagedIdentityClientIdNotAGuid_IsAnError()
    {
        var settings = Settings(
            (GraphConnectorConstants.SettingAuthenticationMethod, GraphConnectorConstants.AuthMethodFederatedCredential),
            (GraphConnectorConstants.SettingManagedIdentityClientId, "my-identity"));

        var results = new GraphConnector().ValidateSettingValues(settings, new Serilog.LoggerConfiguration().CreateLogger());

        Assert.That(results.Where(r => !r.IsValid).Select(r => r.SettingValue?.Setting.Name), Does.Contain(GraphConnectorConstants.SettingManagedIdentityClientId));
    }

    [TestCase(700213)]
    [TestCase(70021)]
    [TestCase(700211)]
    [TestCase(700212)]
    public void TokenFailure_NoMatchingFederatedCredential_SaysToAddOne(int code)
    {
        var message = EntraTokenFailure.Describe(HttpStatusCode.BadRequest, $$"""{"error":"invalid_client","error_codes":[{{code}}]}""");

        Assert.That(message, Does.Contain("federated credential"));
    }

    private static async Task<Dictionary<string, string>> ParametersAsync(IGraphFederatedTokenSource source) =>
        (await new GraphFederatedCredential(source).GetTokenRequestParametersAsync(TokenEndpoint, DateTimeOffset.UtcNow, CancellationToken.None))
        .ToDictionary(p => p.Key, p => p.Value);

    private static StubHttpMessageHandler IdentityHandler(string token) => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""{"access_token":"{{token}}","expires_on":"1893456000","resource":"api://AzureADTokenExchange","token_type":"Bearer"}""", Encoding.UTF8, "application/json")
    });

    private static List<ConnectedSystemSettingValue> Settings(params (string Name, string Value)[] extra)
    {
        var settings = new List<ConnectedSystemSettingValue>
        {
            new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingTenantId }, StringValue = "7a1f0c2e-3b4d-4e5f-8a9b-0c1d2e3f4a5b" },
            new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingClientId }, StringValue = "1c2d3e4f-5a6b-4c7d-8e9f-a0b1c2d3e4f5" }
        };
        settings.AddRange(extra.Select(e => new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = e.Name }, StringValue = e.Value }));
        return settings;
    }
}
