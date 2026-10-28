// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
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
/// What JIM's <c>/api</c> bearer scheme records when it refuses an access token for its audience or issuer (#2443). Each
/// test runs the real <see cref="JwtBearerHandler"/>, configured by <see cref="JimJwtBearerOptions"/> as Program.cs
/// configures it, against a fake identity provider over HTTPS. A token is diagnosed only when the identity provider
/// signed it, and the token is refused either way.
/// </summary>
[TestFixture]
public class AccessTokenSignInProblemTests
{
    private const string Audience = "api://jim";

    private X509Certificate2 _root = null!;
    private X509Certificate2 _server = null!;
    private TlsTestServer _tlsServer = null!;
    private FakeIdentityProvider _identityProvider = null!;
    private ServiceProvider _services = null!;
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
        BuildBearerScheme();
    }

    [TearDown]
    public void TearDown()
    {
        _services?.Dispose();
        _key?.Dispose();
    }

    [Test]
    public async Task AuthenticateAsync_TokenForJim_AcceptedAndNothingRecordedAsync()
    {
        var result = await AuthenticateAsync(_key.IssueToken(_identityProvider.Issuer, Audience));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(_problems.GetRecent(), Is.Empty);
        }
    }

    [Test]
    public async Task AuthenticateAsync_TokenForAnotherAudience_RefusedAndRecordedAsync()
    {
        var result = await AuthenticateAsync(_key.IssueToken(_identityProvider.Issuer, "account"));

        var problem = _problems.GetRecent().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(problem.Source, Is.EqualTo(SignInProblemSource.Api));
            Assert.That(problem.Setting, Is.EqualTo("JIM_SSO_API_SCOPE"));
            Assert.That(problem.Expected, Is.EqualTo(Audience));
            Assert.That(problem.Received, Is.EqualTo("account"));
        }
    }

    [Test]
    public async Task AuthenticateAsync_TokenFromAnotherIssuer_RefusedAndRecordedAsync()
    {
        var result = await AuthenticateAsync(_key.IssueToken(_identityProvider.AdfsAccessTokenIssuer, Audience));

        var problem = _problems.GetRecent().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(problem.Setting, Is.EqualTo("JIM_SSO_VALID_ISSUERS"));
            Assert.That(problem.Expected, Is.EqualTo(_identityProvider.Issuer));
            Assert.That(problem.Received, Is.EqualTo(_identityProvider.AdfsAccessTokenIssuer));
        }
    }

    [Test]
    public async Task AuthenticateAsync_SameProblemTwice_RecordedOnceAndCountedAsync()
    {
        await AuthenticateAsync(_key.IssueToken(_identityProvider.Issuer, "account"));
        await AuthenticateAsync(_key.IssueToken(_identityProvider.Issuer, "account"));

        Assert.That(_problems.GetRecent().Single().Count, Is.EqualTo(2));
    }

    [Test]
    public async Task AuthenticateAsync_ForgedTokenForAnotherAudience_RefusedAndNotRecordedAsync()
    {
        // Signed by a key the identity provider never published: anyone can make one, naming any audience.
        using var forger = SigningKey.Create("forger");

        var result = await AuthenticateAsync(forger.IssueToken(_identityProvider.Issuer, "made-up-audience"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(_problems.GetRecent(), Is.Empty);
        }
    }

    [Test]
    public async Task AuthenticateAsync_ExpiredTokenForAnotherAudience_NotRecordedAsync()
    {
        var result = await AuthenticateAsync(_key.IssueToken(_identityProvider.Issuer, "account", expires: DateTime.UtcNow.AddHours(-1)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(_problems.GetRecent(), Is.Empty, "an expired token is refused for its age, which no setting explains");
        }
    }

    [Test]
    public async Task AuthenticateAsync_NotAToken_NotRecordedAsync()
    {
        var result = await AuthenticateAsync("not-a-token");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(_problems.GetRecent(), Is.Empty);
        }
    }

    private void BuildBearerScheme()
    {
        var snapshot = new SignInCertificateSnapshot(_ => Task.FromResult<System.Collections.Generic.IReadOnlyCollection<X509Certificate2>>([TestPki.PublicOnly(_root)]),
            TimeProvider.System, freshFor: TimeSpan.Zero);
        var backchannel = new IdentityProviderBackchannel(snapshot);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFailedAuthenticationRecorder>(new RecordingFailedAuthenticationRecorder());
        services.AddSingleton(_problems);
        services.AddAuthentication().AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            JimJwtBearerOptions.Configure(options, _identityProvider.Issuer, Audience, [_identityProvider.Issuer], _ => Task.CompletedTask));
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure(options => options.BackchannelHttpHandler = backchannel.CreateHandler());
        services.AddIdentityProviderSigningKeyPolicy();
        _services = services.BuildServiceProvider();
    }

    private async Task<AuthenticateResult> AuthenticateAsync(string token)
    {
        await using var scope = _services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Authorization = $"Bearer {token}";
        return await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
    }
}
