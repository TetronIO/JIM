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
/// Federated credentials: the app registration trusts a workload identity's token instead of holding a secret or a
/// certificate, from an Azure user-assigned managed identity or a token file a platform such as Kubernetes provides.
/// </summary>
[TestFixture]
public class GraphFederatedCredentialTests
{
    private const string ManagedIdentityClientId = "3b9f6a1e-2c4d-4e8f-9a0b-1c2d3e4f5a6b";
    private static readonly Uri TokenEndpoint = new("https://login.microsoftonline.com/tenant/oauth2/v2.0/token");
    private readonly List<string> _tempFiles = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var file in _tempFiles.Where(File.Exists))
            File.Delete(file);
    }

    [Test]
    public async Task ManagedIdentity_AsksTheInstanceMetadataServiceForATokenForTheExchangeAudienceAsync()
    {
        var handler = IdentityHandler("mi-token");
        var source = new GraphManagedIdentityTokenSource(new HttpClient(handler), ManagedIdentityClientId, GraphCloud.Global.FederatedTokenAudience, NoEnvironment);

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

    [Test]
    public async Task ManagedIdentity_OnAPlatformThatNamesItsOwnEndpoint_AsksThatEndpointAsync()
    {
        // App Service, Container Apps and Functions give a managed identity a local endpoint and a header proving the
        // caller is on the host, in place of the instance metadata service.
        var handler = IdentityHandler("platform-token");
        var environment = Environment(("IDENTITY_ENDPOINT", "http://localhost:42356/msi/token"), ("IDENTITY_HEADER", "header-secret"));
        var source = new GraphManagedIdentityTokenSource(new HttpClient(handler), ManagedIdentityClientId, GraphCloud.Global.FederatedTokenAudience, environment);

        var parameters = await ParametersAsync(source);

        var request = handler.Requests.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parameters["client_assertion"], Is.EqualTo("platform-token"));
            Assert.That(request.RequestUri!.GetLeftPart(UriPartial.Path), Is.EqualTo("http://localhost:42356/msi/token"));
            Assert.That(HttpUtility.ParseQueryString(request.RequestUri.Query)["api-version"], Is.EqualTo("2019-08-01"));
            Assert.That(request.Headers.GetValues("X-IDENTITY-HEADER").Single(), Is.EqualTo("header-secret"));
        }
    }

    [Test]
    public void ManagedIdentity_PlatformEndpointNotOnThisHost_IsRefused()
    {
        // The endpoint comes from the environment, and the token it returns is a credential, so only this host is trusted
        // to answer over plain HTTP.
        var environment = Environment(("IDENTITY_ENDPOINT", "http://identity.example.com/token"), ("IDENTITY_HEADER", "header-secret"));
        var source = new GraphManagedIdentityTokenSource(new HttpClient(IdentityHandler("x")), ManagedIdentityClientId, GraphCloud.Global.FederatedTokenAudience, environment);

        var ex = Assert.ThrowsAsync<HttpAuthenticationException>(async () => await ParametersAsync(source));

        Assert.That(ex!.Message, Does.Contain("IDENTITY_ENDPOINT"));
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
        var source = new GraphManagedIdentityTokenSource(new HttpClient(handler), ManagedIdentityClientId, GraphCloud.Global.FederatedTokenAudience, NoEnvironment);

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
        var source = new GraphManagedIdentityTokenSource(new HttpClient(handler), ManagedIdentityClientId, GraphCloud.Global.FederatedTokenAudience, NoEnvironment);

        var ex = Assert.ThrowsAsync<HttpAuthenticationException>(async () => await ParametersAsync(source));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain(GraphConnectorConstants.SettingManagedIdentityClientId));
            Assert.That(ex.Message, Does.Contain("Identity not found"));
        }
    }

    [Test]
    public async Task TokenFile_IsReadAfreshForEveryTokenRequestAsync()
    {
        // The platform replaces the file before the token in it expires.
        var path = TempFile("first-token\n");
        var source = new GraphTokenFileSource(path, NoEnvironment);

        var first = await ParametersAsync(source);
        await File.WriteAllTextAsync(path, "second-token");
        var second = await ParametersAsync(source);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first["client_assertion"], Is.EqualTo("first-token"));
            Assert.That(second["client_assertion"], Is.EqualTo("second-token"));
        }
    }

    [Test]
    public async Task TokenFile_NoPathSet_UsesTheFileTheWorkloadIdentityPlatformNamesAsync()
    {
        var path = TempFile("platform-file-token");

        var parameters = await ParametersAsync(new GraphTokenFileSource(null, Environment(("AZURE_FEDERATED_TOKEN_FILE", path))));

        Assert.That(parameters["client_assertion"], Is.EqualTo("platform-file-token"));
    }

    [Test]
    public void TokenFile_Missing_NamesThePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jim-missing-{Path.GetRandomFileName()}");

        var ex = Assert.ThrowsAsync<HttpAuthenticationException>(async () => await ParametersAsync(new GraphTokenFileSource(path, NoEnvironment)));

        Assert.That(ex!.Message, Does.Contain(path));
    }

    [Test]
    public void TokenFile_NoPathAnywhere_SaysWhereToSetOne()
    {
        var ex = Assert.ThrowsAsync<HttpAuthenticationException>(async () => await ParametersAsync(new GraphTokenFileSource(null, NoEnvironment)));

        Assert.That(ex!.Message, Does.Contain(GraphConnectorConstants.SettingTokenFilePath));
    }

    [Test]
    public async Task Factory_FederatedCredentialWithAManagedIdentity_SendsItsTokenAsTheClientAssertionAsync()
    {
        var identity = IdentityHandler("mi-token");
        var tokens = new StubHttpMessageHandler(_ => GraphTestTokens.TokenResponse());
        var settings = Settings(
            (GraphConnectorConstants.SettingAuthenticationMethod, GraphConnectorConstants.AuthMethodFederatedCredential),
            (GraphConnectorConstants.SettingFederatedCredentialSource, GraphConnectorConstants.FederatedSourceManagedIdentity),
            (GraphConnectorConstants.SettingManagedIdentityClientId, ManagedIdentityClientId));

        var authentication = GraphHttpClientFactory.CreateAuthentication(settings, null, new HttpClient(tokens), out var owned, identity, NoEnvironment);
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
    public void Settings_OfferFederatedCredentialWithItsSources()
    {
        var settings = new GraphConnector().GetSettings();
        var method = settings.Single(s => s.Name == GraphConnectorConstants.SettingAuthenticationMethod);
        var source = settings.Single(s => s.Name == GraphConnectorConstants.SettingFederatedCredentialSource);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(method.DropDownValues, Does.Contain(GraphConnectorConstants.AuthMethodFederatedCredential));
            Assert.That(source.DropDownValues, Is.EqualTo(new[] { GraphConnectorConstants.FederatedSourceManagedIdentity, GraphConnectorConstants.FederatedSourceTokenFile }));
            Assert.That(source.RequiredWhenValue, Is.EqualTo(GraphConnectorConstants.AuthMethodFederatedCredential));
            Assert.That(settings.Single(s => s.Name == GraphConnectorConstants.SettingManagedIdentityClientId).RequiredWhenValue, Is.EqualTo(GraphConnectorConstants.FederatedSourceManagedIdentity));
        }
    }

    [Test]
    public void Validation_ManagedIdentityClientIdNotAGuid_IsAnError()
    {
        var settings = Settings(
            (GraphConnectorConstants.SettingAuthenticationMethod, GraphConnectorConstants.AuthMethodFederatedCredential),
            (GraphConnectorConstants.SettingFederatedCredentialSource, GraphConnectorConstants.FederatedSourceManagedIdentity),
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

    private static string? NoEnvironment(string name) => null;

    private static Func<string, string?> Environment(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(v => v.Name == name).Value;

    private string TempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jim-token-{Path.GetRandomFileName()}");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

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
