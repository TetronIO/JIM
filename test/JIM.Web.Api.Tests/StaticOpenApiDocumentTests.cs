// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JIM.TestSupport;
using JIM.Web.Authentication;
using JIM.Web.Extensions.Api;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// A released image serves the OpenAPI document written when the image was built, before any identity provider
/// existed, so its OAuth endpoints are placeholders. JIM replaces them with the identity provider's real ones as it
/// serves the document, so the API reference's Try It can sign in (#2544). It reads them only when the document is
/// asked for, never at start-up (#2363), and serves the document as written when it cannot.
/// </summary>
[TestFixture]
public class StaticOpenApiDocumentTests
{
    private const string PlaceholderAuthorizationUrl = "https://idp.example.com/realms/jim/protocol/openid-connect/auth";
    private const string PlaceholderTokenUrl = "https://idp.example.com/realms/jim/protocol/openid-connect/token";
    private const string RealAuthorizationUrl = "https://localhost/adfs/oauth2/authorize";
    private const string RealTokenUrl = "https://localhost/adfs/oauth2/token";

    private string _directory = null!;
    private string _documentPath = null!;
    private X509Certificate2 _root = null!;
    private X509Certificate2 _server = null!;
    private TlsTestServer _identityProvider = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _root = TestPki.CreateRoot("Contoso Root CA");
        _server = TestPki.CreateServer("localhost", _root);
        _identityProvider = TlsTestServer.Start(_server, path => path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal)
            ? $$"""{"issuer":"https://localhost/adfs","authorization_endpoint":"{{RealAuthorizationUrl}}","token_endpoint":"{{RealTokenUrl}}"}"""
            : null);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        await _identityProvider.DisposeAsync();
        _server.Dispose();
        _root.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"jim-openapi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _documentPath = Path.Combine(_directory, "v1.json");
        File.WriteAllText(_documentPath, BuiltDocument);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public async Task ReadAsync_IdentityProviderConfigured_ServesItsRealEndpointsAsync()
    {
        using var backchannel = Backchannel(trusted: true);
        var manager = NewManager(backchannel);
        var document = new StaticOpenApiDocument(_documentPath, ct => IdentityProviderOAuthEndpoints.ReadAsync(manager, ct));

        var served = await document.ReadAsync(CancellationToken.None);

        var flow = AuthorizationCodeFlow(served);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flow.GetProperty("authorizationUrl").GetString(), Is.EqualTo(RealAuthorizationUrl));
            Assert.That(flow.GetProperty("tokenUrl").GetString(), Is.EqualTo(RealTokenUrl));
        }
    }

    [Test]
    public async Task ReadAsync_IdentityProviderNotTrustedYet_ServesTheDocumentAsWrittenAsync()
    {
        using var backchannel = Backchannel(trusted: false);
        var manager = NewManager(backchannel);
        var document = new StaticOpenApiDocument(_documentPath, ct => IdentityProviderOAuthEndpoints.ReadAsync(manager, ct));

        var served = await document.ReadAsync(CancellationToken.None);

        Assert.That(served, Is.EqualTo(BuiltDocument));
    }

    [Test]
    public async Task ReadAsync_RealEndpoints_LeavesTheRestOfTheDocumentAloneAsync()
    {
        var document = new StaticOpenApiDocument(_documentPath, _ => Task.FromResult<(string, string)?>((RealAuthorizationUrl, RealTokenUrl)));

        using var served = JsonDocument.Parse(await document.ReadAsync(CancellationToken.None));

        var root = served.RootElement;
        var flow = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("OAuth2").GetProperty("flows").GetProperty("authorizationCode");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.GetProperty("info").GetProperty("title").GetString(), Is.EqualTo("JIM API"));
            Assert.That(root.GetProperty("info").GetProperty("description").GetString(), Is.EqualTo("Café <docs> & more"));
            Assert.That(root.GetProperty("paths").GetProperty("/api/v1/health").GetProperty("get").GetProperty("summary").GetString(), Is.EqualTo("Health"));
            Assert.That(flow.GetProperty("scopes").GetProperty("api://jim/access").GetString(), Is.EqualTo("Access JIM API"));
            Assert.That(root.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey").GetProperty("scheme").GetString(), Is.EqualTo("bearer"));
        }
    }

    [Test]
    public void Constructor_NeverReadsTheIdentityProvider()
    {
        var reads = 0;

        _ = new StaticOpenApiDocument(_documentPath, _ =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult<(string, string)?>((RealAuthorizationUrl, RealTokenUrl));
        });

        Assert.That(reads, Is.Zero);
    }

    [Test]
    public async Task ReadAsync_IdentityProviderChangesItsEndpoints_ServesTheNewOnesAsync()
    {
        (string, string)? endpoints = (RealAuthorizationUrl, RealTokenUrl);
        var document = new StaticOpenApiDocument(_documentPath, _ => Task.FromResult(endpoints));
        await document.ReadAsync(CancellationToken.None);

        endpoints = ("https://sso.example.com/authorize", "https://sso.example.com/token");
        var served = await document.ReadAsync(CancellationToken.None);

        var flow = AuthorizationCodeFlow(served);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flow.GetProperty("authorizationUrl").GetString(), Is.EqualTo("https://sso.example.com/authorize"));
            Assert.That(flow.GetProperty("tokenUrl").GetString(), Is.EqualTo("https://sso.example.com/token"));
        }
    }

    [Test]
    public async Task ReadAsync_DocumentWithoutAnOAuthScheme_ServesItAsWrittenAsync()
    {
        const string withoutOAuth = """{"openapi":"3.1.1","info":{"title":"JIM API"}}""";
        File.WriteAllText(_documentPath, withoutOAuth);
        var document = new StaticOpenApiDocument(_documentPath, _ => Task.FromResult<(string, string)?>((RealAuthorizationUrl, RealTokenUrl)));

        var served = await document.ReadAsync(CancellationToken.None);

        Assert.That(served, Is.EqualTo(withoutOAuth));
    }

    /// <summary>
    /// The image build writes the API scope it was built with, not the one this deployment's identity provider issues
    /// tokens for (#2594). Try It asks for whatever the document offers, so the scope is replaced too, in the flow and
    /// in the requirement, whether or not the endpoints can be read.
    /// </summary>
    [Test]
    public async Task ReadAsync_TryItApiScope_ReplacesTheBuiltScopeInTheFlowAndTheRequirementAsync()
    {
        const string adfsScope = "api://11111111-1111-1111-1111-111111111111/openid";
        var document = new StaticOpenApiDocument(_documentPath, _ => Task.FromResult<(string, string)?>(null), adfsScope);

        var served = await document.ReadAsync(CancellationToken.None);

        using var json = JsonDocument.Parse(served);
        var requirements = json.RootElement.GetProperty("security").EnumerateArray().ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AuthorizationCodeFlow(served).GetProperty("scopes").EnumerateObject().Select(p => p.Name),
                Is.EqualTo(new[] { "openid", adfsScope }));
            Assert.That(AuthorizationCodeFlow(served).GetProperty("scopes").GetProperty(adfsScope).GetString(), Is.EqualTo("Access JIM API"));
            Assert.That(requirements[0].GetProperty("OAuth2").EnumerateArray().Select(s => s.GetString()), Is.EqualTo(new[] { "openid", adfsScope }));
            Assert.That(requirements[1].GetProperty("ApiKey").GetArrayLength(), Is.Zero, "the API key requirement is left alone");
            Assert.That(AuthorizationCodeFlow(served).GetProperty("authorizationUrl").GetString(), Is.EqualTo(PlaceholderAuthorizationUrl));
        }
    }

    [Test]
    public async Task ReadAsync_TryItApiScopeAsBuilt_ServesTheDocumentAsWrittenAsync()
    {
        var document = new StaticOpenApiDocument(_documentPath, _ => Task.FromResult<(string, string)?>(null), "api://jim/access");

        var served = await document.ReadAsync(CancellationToken.None);

        Assert.That(served, Is.EqualTo(BuiltDocument));
    }

    [Test]
    public async Task ReadAsync_RealEndpointsAndTryItApiScope_ServesBothAsync()
    {
        var document = new StaticOpenApiDocument(_documentPath,
            _ => Task.FromResult<(string, string)?>((RealAuthorizationUrl, RealTokenUrl)), "jim-api");

        var flow = AuthorizationCodeFlow(await document.ReadAsync(CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flow.GetProperty("authorizationUrl").GetString(), Is.EqualTo(RealAuthorizationUrl));
            Assert.That(flow.GetProperty("scopes").EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "openid", "jim-api" }));
        }
    }

    [Test]
    public async Task ReadAsync_NoConfigurationManager_ReadsNoEndpointsAsync()
    {
        var endpoints = await IdentityProviderOAuthEndpoints.ReadAsync(null, CancellationToken.None);

        Assert.That(endpoints, Is.Null);
    }

    private static JsonElement AuthorizationCodeFlow(string served) =>
        JsonDocument.Parse(served).RootElement.GetProperty("components").GetProperty("securitySchemes")
            .GetProperty("OAuth2").GetProperty("flows").GetProperty("authorizationCode");

    private HttpClient Backchannel(bool trusted)
    {
        IReadOnlyCollection<X509Certificate2> certificates = trusted ? [TestPki.PublicOnly(_root)] : [];
        var snapshot = new SignInCertificateSnapshot(_ => Task.FromResult(certificates), TimeProvider.System, freshFor: TimeSpan.Zero);
        return new HttpClient(new IdentityProviderBackchannel(snapshot).CreateHandler());
    }

    private ConfigurationManager<OpenIdConnectConfiguration> NewManager(HttpClient backchannel) =>
        new($"https://localhost:{_identityProvider.Port}/adfs/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(backchannel));

    // The shape the image build writes: the security schemes OpenApiSecurityConfiguration declares, with the
    // placeholder endpoints generation mode uses.
    private static readonly string BuiltDocument = $$"""
        {
          "openapi": "3.1.1",
          "info": {
            "title": "JIM API",
            "description": "Café <docs> & more",
            "version": "v1"
          },
          "paths": {
            "/api/v1/health": {
              "get": {
                "summary": "Health"
              }
            }
          },
          "components": {
            "securitySchemes": {
              "OAuth2": {
                "type": "oauth2",
                "flows": {
                  "authorizationCode": {
                    "authorizationUrl": "{{PlaceholderAuthorizationUrl}}",
                    "tokenUrl": "{{PlaceholderTokenUrl}}",
                    "scopes": {
                      "openid": "OpenID Connect",
                      "api://jim/access": "Access JIM API"
                    }
                  }
                }
              },
              "ApiKey": {
                "type": "http",
                "scheme": "bearer"
              }
            }
          },
          "security": [
            {
              "OAuth2": [
                "openid",
                "api://jim/access"
              ]
            },
            {
              "ApiKey": []
            }
          ]
        }
        """;
}
