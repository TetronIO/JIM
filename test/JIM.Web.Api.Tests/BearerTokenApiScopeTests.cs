// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using JIM.TestSupport;
using JIM.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Which tokens JIM's <c>/api</c> bearer scheme accepts when the audience it requires is the portal's own client ID, as
/// it is for Keycloak (<c>JIM_SSO_API_SCOPE=jim-api</c>). The portal's ID token carries that same audience, so the
/// audience alone cannot tell it from an access token: an access token must also grant JIM's API scope, which an ID
/// token never does. Each test runs the real <see cref="JwtBearerHandler"/>, configured by
/// <see cref="JimJwtBearerOptions"/> as Program.cs configures it, against a fake identity provider over HTTPS.
/// </summary>
[TestFixture]
public class BearerTokenApiScopeTests
{
    private const string ClientId = "jim-web";
    private const string KeycloakApiScope = "jim-api";
    private X509Certificate2 _root = null!;
    private X509Certificate2 _server = null!;
    private TlsTestServer _tlsServer = null!;
    private FakeIdentityProvider _identityProvider = null!;
    private ServiceProvider? _services;
    private SignInProblemLog _problems = null!;
    private SigningKey _key = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _root = TestPki.CreateRoot("Contoso Root CA");
        _server = TestPki.CreateServer("localhost", _root);
        _identityProvider = new FakeIdentityProvider();
        _tlsServer = TlsTestServer.Start(_server, _identityProvider.Respond);
        _identityProvider.Port = _tlsServer.Port;
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        await _tlsServer.DisposeAsync();
        _server.Dispose();
        _root.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        _identityProvider.Reset();
        _key = SigningKey.Create("current");
        _identityProvider.Publish(_key);
        _problems = new SignInProblemLog(TimeProvider.System);
    }

    [TearDown]
    public void TearDown()
    {
        _services?.Dispose();
        _key?.Dispose();
    }

    [Test]
    public async Task AuthenticateAsync_PortalIdTokenWhenAudienceIsClientId_RefusedAndRecordedAsync()
    {
        BuildBearerScheme(KeycloakApiScope);

        // What Keycloak issues the portal as its ID token: the client ID as the audience, and no scope.
        var result = await AuthenticateAsync(_key.IssueToken(_identityProvider.Issuer, ClientId, claims: new Dictionary<string, object> { ["typ"] = "ID" }));

        var problem = _problems.GetRecent().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.False, "an ID token is not an access token, whatever its audience");
            Assert.That(problem.Source, Is.EqualTo(SignInProblemSource.Api));
            Assert.That(problem.Setting, Is.EqualTo("JIM_SSO_API_SCOPE"));
            Assert.That(problem.Expected, Is.EqualTo(KeycloakApiScope));
            Assert.That(problem.Received, Is.EqualTo("no scope"));
        }
    }

    [Test]
    public async Task AuthenticateAsync_AccessTokenGrantingApiScope_AcceptedAsync()
    {
        BuildBearerScheme(KeycloakApiScope);

        var result = await AuthenticateAsync(_key.IssueToken(_identityProvider.Issuer, ClientId,
            claims: new Dictionary<string, object> { ["scope"] = "openid jim-api profile" }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(_problems.GetRecent(), Is.Empty);
        }
    }

    [Test]
    public async Task AuthenticateAsync_ScpArrayGrantingApiScope_AcceptedAsync()
    {
        BuildBearerScheme(KeycloakApiScope);

        var result = await AuthenticateAsync(_key.IssueToken(_identityProvider.Issuer, ClientId,
            claims: new Dictionary<string, object> { ["scp"] = new[] { "openid", "jim-api" } }));

        Assert.That(result.Succeeded, Is.True);
    }

    [Test]
    public async Task AuthenticateAsync_ScopeOnlyResemblingApiScope_RefusedAsync()
    {
        BuildBearerScheme(KeycloakApiScope);

        var result = await AuthenticateAsync(_key.IssueToken(_identityProvider.Issuer, ClientId,
            claims: new Dictionary<string, object> { ["scope"] = "openid jim-api-old JIM-API" }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.False, "scopes match exactly");
            Assert.That(_problems.GetRecent().Single().Received, Is.EqualTo("openid jim-api-old JIM-API"));
        }
    }

    [Test]
    public async Task AuthenticateAsync_NoApiScopeConfiguredAndAudienceIsClientId_RefusedAndRecordedAsync()
    {
        // With no API scope, JIM requires the client ID as the audience and has nothing to tell an access token from
        // the portal's ID token by, so it accepts neither.
        BuildBearerScheme(apiScope: null);

        var result = await AuthenticateAsync(_key.IssueToken(_identityProvider.Issuer, ClientId,
            claims: new Dictionary<string, object> { ["scope"] = "openid profile" }));

        var problem = _problems.GetRecent().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(problem.Setting, Is.EqualTo("JIM_SSO_API_SCOPE"));
            Assert.That(problem.Expected, Is.EqualTo("an API scope in JIM_SSO_API_SCOPE"));
        }
    }

    [Test]
    public async Task AuthenticateAsync_AudienceIsNotClientId_NoScopeRequiredAsync()
    {
        // AD FS: the audience is the API's own identifier, which no ID token carries, so the audience is enough.
        BuildBearerScheme("api://jim");

        var result = await AuthenticateAsync(_key.IssueToken(_identityProvider.Issuer, "api://jim"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(_problems.GetRecent(), Is.Empty);
        }
    }

    [Test]
    public void For_AudienceIsNotClientId_RequiresNothing()
    {
        var requirement = BearerScopeRequirement.For("api://jim", ClientId, "api://jim");

        Assert.That(requirement.IsRequired, Is.False);
    }

    [Test]
    public void For_AudienceIsClientIdWithPlainScope_RequiresTheScope()
    {
        var requirement = BearerScopeRequirement.For(KeycloakApiScope, ClientId, ClientId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requirement.IsRequired, Is.True);
            Assert.That(requirement.Scope, Is.EqualTo(KeycloakApiScope));
        }
    }

    [Test]
    public void For_AudienceIsClientIdWithApiUriScope_RequiresThePermission()
    {
        // Entra ID with version 2 access tokens: the audience is the client ID, and scp holds the permission alone.
        var requirement = BearerScopeRequirement.For("api://11111111-2222-3333-4444-555555555555/access_as_user",
            "11111111-2222-3333-4444-555555555555", "11111111-2222-3333-4444-555555555555");

        Assert.That(requirement.Scope, Is.EqualTo("access_as_user"));
    }

    [Test]
    public void For_AudienceIsClientIdWithNoScope_RequiredButNoScopeCanMeetIt()
    {
        var requirement = BearerScopeRequirement.For(null, ClientId, ClientId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requirement.IsRequired, Is.True);
            Assert.That(requirement.Scope, Is.Null);
        }
    }

    private void BuildBearerScheme(string? apiScope)
    {
        var snapshot = new SignInCertificateSnapshot(_ => Task.FromResult<IReadOnlyCollection<X509Certificate2>>([TestPki.PublicOnly(_root)]),
            TimeProvider.System, freshFor: TimeSpan.Zero);
        var backchannel = new IdentityProviderBackchannel(snapshot);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFailedAuthenticationRecorder>(new RecordingFailedAuthenticationRecorder());
        services.AddSingleton(_problems);
        var audience = ApiAudienceResolver.Resolve(_identityProvider.Issuer, apiScope, ClientId);
        var requirement = BearerScopeRequirement.For(apiScope, ClientId, audience);
        services.AddAuthentication().AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            JimJwtBearerOptions.Configure(options, _identityProvider.Issuer, audience, [_identityProvider.Issuer], requirement, _ => Task.CompletedTask));
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure(options => options.BackchannelHttpHandler = backchannel.CreateHandler());
        services.AddIdentityProviderSigningKeyPolicy();
        _services = services.BuildServiceProvider();
    }

    private async Task<AuthenticateResult> AuthenticateAsync(string token)
    {
        await using var scope = _services!.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Authorization = $"Bearer {token}";
        return await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
    }
}
