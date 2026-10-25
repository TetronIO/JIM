// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JIM.TestSupport;
using JIM.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// What JIM's <c>/api</c> bearer scheme does when the identity provider rolls its token-signing key (#2442). Each test
/// runs the real <see cref="JwtBearerHandler"/>, configured by <see cref="JimJwtBearerOptions"/> and
/// <see cref="IdentityProviderSigningKeys"/> exactly as Program.cs configures it, against a fake identity provider over
/// HTTPS behind <see cref="IdentityProviderBackchannel"/>, whose key set the test swaps at runtime.
/// <para>
/// What these pin (IdentityModel 8.19, ASP.NET Core 10):
/// </para>
/// <list type="bullet">
/// <item>A key published before it is used, and fetched by JIM, is accepted on first use.</item>
/// <item>After an unannounced rollover the first request signed by the new key is accepted: JIM fetches the key set
/// and waits for it before refusing a token whose key it has not seen.</item>
/// <item>That fetch happens at most once per <see cref="IdentityProviderSigningKeys.RefreshInterval"/> (30 seconds),
/// however many such tokens arrive, so a rollover within that time of an earlier fetch is refused until it has
/// passed.</item>
/// <item>A token signed by a key the identity provider no longer publishes is refused: IdentityModel's last known
/// good configuration, which would accept it for up to an hour, is turned off.</item>
/// <item>A token issued under the <c>access_token_issuer</c> the discovery document publishes (AD FS's federation
/// service identifier) is accepted with no issuer configured for it, and a token from any other issuer is refused
/// (#2440).</item>
/// </list>
/// </summary>
[TestFixture]
public class SigningKeyRolloverTests
{
    private const string Audience = "jim-api";
    private const string JimIdentityClaim = "jim-test-resolved";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private X509Certificate2 _root = null!;
    private X509Certificate2 _server = null!;
    private TlsTestServer _tlsServer = null!;
    private FakeIdentityProvider _identityProvider = null!;
    private ServiceProvider _services = null!;
    private RecordingFailedAuthenticationRecorder _audit = null!;

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
        _audit = new RecordingFailedAuthenticationRecorder();
    }

    [TearDown]
    public void TearDown()
    {
        _services?.Dispose();
    }

    [Test]
    public void Configure_JimBearerScheme_RefreshesTheKeySetEarlyAtMostEvery30Seconds()
    {
        var options = new JwtBearerOptions();

        JimJwtBearerOptions.Configure(options, "https://idp.example.com/realms/jim", Audience, ["https://idp.example.com/realms/jim"], _ => Task.CompletedTask);

        Assert.Multiple(() =>
        {
            Assert.That(options.RefreshInterval, Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(options.RefreshInterval, Is.EqualTo(IdentityProviderSigningKeys.RefreshInterval));
            Assert.That(options.AutomaticRefreshInterval, Is.EqualTo(TimeSpan.FromHours(12)));
            Assert.That(options.RefreshOnIssuerKeyNotFound, Is.True);
            Assert.That(options.TokenValidationParameters.ValidateIssuerSigningKey, Is.True);
        });
    }

    [Test]
    public void AddIdentityProviderSigningKeyPolicy_JimBearerScheme_TurnsOffTheLastKnownGoodConfiguration()
    {
        BuildBearerScheme();

        var manager = _services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme).ConfigurationManager;

        Assert.That(manager, Is.InstanceOf<BaseConfigurationManager>());
        Assert.That(((BaseConfigurationManager)manager!).UseLastKnownGoodConfiguration, Is.False);
    }

    [Test]
    public async Task AuthenticateAsync_KeyPublishedAheadOfUse_FirstRequestAcceptedAsync()
    {
        using var current = SigningKey.Create("current");
        using var next = SigningKey.Create("next");
        _identityProvider.Publish(current, next);
        BuildBearerScheme();
        Assert.That(await AuthenticateAsync(current), Is.True, "baseline: a token signed by the current key");

        // The provider promotes the key it published earlier (AD FS publishes it 5 days ahead); JIM already holds it.
        var first = await AuthenticateAsync(next);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True, "first request signed by the promoted key");
            Assert.That(_identityProvider.KeySetFetches, Is.EqualTo(1), "no further key set fetch is needed");
            Assert.That(_audit.Reasons, Is.Empty);
        });
    }

    [Test]
    public async Task AuthenticateAsync_UnannouncedRolloverSoonAfterJimFetchedTheKeySet_FirstRequestAcceptedAsync()
    {
        using var old = SigningKey.Create("old");
        using var replacement = SigningKey.Create("replacement");
        _identityProvider.Publish(old);
        BuildBearerScheme();
        Assert.That(await AuthenticateAsync(old), Is.True, "baseline: JIM fetches the key set and accepts the old key");

        // Seconds after that fetch, the provider replaces its key without notice (a Keycloak key change).
        _identityProvider.Publish(replacement);
        var first = await AuthenticateAsync(replacement);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True, "first request signed by the new key");
            Assert.That(_audit.Reasons, Is.Empty, "nothing was refused, so nothing is audited");
        });
    }

    [Test]
    public async Task AuthenticateAsync_UnannouncedRolloverLongAfterAnEarlierEarlyRefresh_FirstRequestAcceptedAsync()
    {
        using var old = SigningKey.Create("old");
        using var stray = SigningKey.Create("stray");
        using var replacement = SigningKey.Create("replacement");
        _identityProvider.Publish(old);

        // A long-running JIM: it fetched the key set, and has since refreshed it early (here forced by a token naming a
        // key the provider never published), longer ago than the refresh interval. The interval is shortened to 1
        // second so the test need not wait JIM's 30.
        var refreshInterval = TimeSpan.FromSeconds(1);
        BuildBearerScheme(refreshInterval);
        Assert.That(await AuthenticateAsync(old), Is.True, "baseline");
        Assert.That(await AuthenticateAsync(stray), Is.False, "a token naming a key the provider never published");
        await Task.Delay(refreshInterval + TimeSpan.FromMilliseconds(500));

        _identityProvider.Publish(replacement);
        var first = await AuthenticateAsync(replacement);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True, "first request signed by the new key");
            Assert.That(_audit.Reasons, Is.EqualTo(new[] { "Access token signing key not found" }), "only the stray token is audited");
        });
    }

    [Test]
    public async Task AuthenticateAsync_UnannouncedRollover_ResolvesTheCallersJimIdentityAsync()
    {
        using var old = SigningKey.Create("old");
        using var replacement = SigningKey.Create("replacement");
        _identityProvider.Publish(old);
        BuildBearerScheme();
        Assert.That(await AuthenticateAsync(old), Is.True, "baseline");

        _identityProvider.Publish(replacement);
        var result = await AuthenticateForResultAsync(replacement);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Principal!.HasClaim(JimIdentityClaim, "true"), Is.True, "OnTokenValidated ran for the accepted token");
    }

    [Test]
    public async Task AuthenticateAsync_TokenSignedByRetiredKeyAfterRollover_RefusedAsync()
    {
        using var old = SigningKey.Create("old");
        using var replacement = SigningKey.Create("replacement");
        _identityProvider.Publish(old);
        BuildBearerScheme();
        Assert.That(await AuthenticateAsync(old), Is.True, "baseline");
        _identityProvider.Publish(replacement);
        Assert.That(await AuthenticateAsync(replacement), Is.True, "JIM now holds the provider's new key set, which no longer carries the old key");
        await WaitUntilJimHoldsAsync(replacement.KeyId);

        // After a rollover made because the old key was compromised (AD FS Update-AdfsCertificate -Urgent), a token
        // forged with it must be refused at once, not accepted from the last known good configuration for an hour.
        var retired = await AuthenticateAsync(old);

        Assert.Multiple(() =>
        {
            Assert.That(retired, Is.False);
            Assert.That(_audit.Reasons, Is.EqualTo(new[] { "Access token signing key not found" }));
        });
    }

    [Test]
    public async Task AuthenticateAsync_ManyTokensNamingUnknownKeys_FetchTheKeySetOnceAsync()
    {
        using var old = SigningKey.Create("old");
        _identityProvider.Publish(old);
        BuildBearerScheme();
        Assert.That(await AuthenticateAsync(old), Is.True, "baseline");

        // Anyone can send a token naming a key JIM has not seen; that must not let them make JIM fetch the key set on
        // every request.
        var strays = Enumerable.Range(0, 10).Select(i => SigningKey.Create($"stray{i}")).ToList();
        try
        {
            var results = await Task.WhenAll(strays.Select(s => AuthenticateAsync(s)));
            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() =>
            {
                Assert.That(results, Is.All.False);
                // The baseline fetch, JIM's own fetch, and the configuration manager's background refresh.
                Assert.That(_identityProvider.KeySetFetches, Is.LessThanOrEqualTo(3));
            });
        }
        finally
        {
            strays.ForEach(s => s.Dispose());
        }
    }

    [Test]
    public async Task AuthenticateAsync_UnannouncedRolloverWithinRefreshIntervalOfEarlierFetch_RefusedUntilIntervalPassesAsync()
    {
        using var old = SigningKey.Create("old");
        using var stray = SigningKey.Create("stray");
        using var replacement = SigningKey.Create("replacement");
        _identityProvider.Publish(old);

        // JIM uses 30 seconds; 10 here keeps the test short while leaving room for the fetches themselves, which can
        // take a few seconds on a Windows machine (localhost is tried over IPv6 first, and the fake identity provider
        // listens on IPv4 only).
        var refreshInterval = TimeSpan.FromSeconds(10);
        BuildBearerScheme(refreshInterval);
        Assert.That(await AuthenticateAsync(old), Is.True, "baseline");
        // JIM opens the window part-way through the stray request, so it opens no earlier than the first clock starts
        // and no later than the second: the first bounds the requests made inside it, the second the wait for its end.
        var windowOpenedNoEarlier = Stopwatch.StartNew();
        Assert.That(await AuthenticateAsync(stray), Is.False, "a token naming an unknown key makes JIM fetch the key set");
        var windowOpenedNoLater = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var fetchesBeforeWindow = _identityProvider.KeySetFetches;

        // The provider rolls its key inside the window that fetch opened.
        _identityProvider.Publish(replacement);
        var refusedInsideWindow = 0;
        while (windowOpenedNoEarlier.Elapsed < refreshInterval - TimeSpan.FromMilliseconds(750))
        {
            Assert.That(await AuthenticateAsync(replacement), Is.False, "inside the window every request is refused");
            refusedInsideWindow++;
            await Task.Delay(200);
        }

        Assert.That(_identityProvider.KeySetFetches, Is.EqualTo(fetchesBeforeWindow), "no key set fetch inside the window");

        var restOfWindow = refreshInterval - windowOpenedNoLater.Elapsed + TimeSpan.FromMilliseconds(250);
        if (restOfWindow > TimeSpan.Zero)
            await Task.Delay(restOfWindow);
        var first = await AuthenticateAsync(replacement);

        Assert.Multiple(() =>
        {
            Assert.That(refusedInsideWindow, Is.GreaterThan(0));
            Assert.That(first, Is.True, "first request after the window fetches the key set and is accepted");
        });
    }

    [Test]
    public async Task AuthenticateAsync_TokenFromPublishedAccessTokenIssuer_AcceptedAsync()
    {
        using var key = SigningKey.Create("current");
        _identityProvider.Publish(key);
        _identityProvider.AccessTokenIssuer = _identityProvider.AdfsAccessTokenIssuer;
        BuildBearerScheme();

        // AD FS signs people in as https://{host}/adfs, but issues access tokens as http://{host}/adfs/services/trust.
        var result = await AuthenticateAsync(key, _identityProvider.AdfsAccessTokenIssuer);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True, "no JIM_SSO_VALID_ISSUERS is needed for the issuer discovery names");
            Assert.That(_audit.Reasons, Is.Empty);
        });
    }

    [Test]
    public async Task AuthenticateAsync_TokenFromAccessTokenIssuerTheProviderDoesNotPublish_RefusedAsync()
    {
        using var key = SigningKey.Create("current");
        _identityProvider.Publish(key);
        BuildBearerScheme();
        Assert.That(await AuthenticateAsync(key), Is.True, "baseline: a token from the authority's issuer");

        var result = await AuthenticateAsync(key, _identityProvider.AdfsAccessTokenIssuer);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.False);
            Assert.That(_audit.Reasons, Is.EqualTo(new[] { "Access token issuer invalid" }));
        });
    }

    [Test]
    public async Task AuthenticateAsync_TokenFromAnyOtherIssuer_RefusedAsync()
    {
        using var key = SigningKey.Create("current");
        _identityProvider.Publish(key);
        _identityProvider.AccessTokenIssuer = _identityProvider.AdfsAccessTokenIssuer;
        BuildBearerScheme();

        var result = await AuthenticateAsync(key, "https://attacker.example.com/adfs/services/trust");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.False);
            Assert.That(_audit.Reasons, Is.EqualTo(new[] { "Access token issuer invalid" }));
        });
    }

    [Test]
    public async Task AuthenticateAsync_UnannouncedRolloverTokenFromPublishedAccessTokenIssuer_AcceptedAsync()
    {
        using var old = SigningKey.Create("old");
        using var replacement = SigningKey.Create("replacement");
        _identityProvider.Publish(old);
        _identityProvider.AccessTokenIssuer = _identityProvider.AdfsAccessTokenIssuer;
        BuildBearerScheme();
        Assert.That(await AuthenticateAsync(old, _identityProvider.AdfsAccessTokenIssuer), Is.True, "baseline");

        // The rollover recovery validates the token itself, so it too must accept the published access token issuer.
        _identityProvider.Publish(replacement);
        var first = await AuthenticateAsync(replacement, _identityProvider.AdfsAccessTokenIssuer);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(_audit.Reasons, Is.Empty);
        });
    }

    [Test]
    public async Task AuthenticateAsync_UnannouncedRolloverTokenFromAnyOtherIssuer_RefusedAsync()
    {
        using var old = SigningKey.Create("old");
        using var replacement = SigningKey.Create("replacement");
        _identityProvider.Publish(old);
        _identityProvider.AccessTokenIssuer = _identityProvider.AdfsAccessTokenIssuer;
        BuildBearerScheme();
        Assert.That(await AuthenticateAsync(old), Is.True, "baseline");

        _identityProvider.Publish(replacement);
        var first = await AuthenticateAsync(replacement, "https://attacker.example.com/adfs/services/trust");

        Assert.That(first, Is.False);
    }

    private void BuildBearerScheme(TimeSpan? refreshInterval = null)
    {
        var snapshot = new SignInCertificateSnapshot(_ => Task.FromResult<IReadOnlyCollection<X509Certificate2>>([TestPki.PublicOnly(_root)]),
            TimeProvider.System, freshFor: TimeSpan.Zero);
        var backchannel = new IdentityProviderBackchannel(snapshot);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFailedAuthenticationRecorder>(_audit);
        services.AddAuthentication().AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            JimJwtBearerOptions.Configure(options, _identityProvider.Issuer, Audience, [_identityProvider.Issuer], ctx =>
            {
                // Stands in for ResolveAndAttachJimIdentityAsync.
                ((ClaimsIdentity)ctx.Principal!.Identity!).AddClaim(new Claim(JimIdentityClaim, "true"));
                return Task.CompletedTask;
            });
            if (refreshInterval is { } interval)
                options.RefreshInterval = interval;
        });

        // As AddIdentityProviderTrust and AddIdentityProviderSigningKeyPolicy do in Program.cs.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure(options => options.BackchannelHttpHandler = backchannel.CreateHandler());
        services.AddIdentityProviderSigningKeyPolicy();
        _services = services.BuildServiceProvider();
    }

    private async Task<bool> AuthenticateAsync(SigningKey key, string? issuer = null) => (await AuthenticateForResultAsync(key, issuer)).Succeeded;

    private async Task<AuthenticateResult> AuthenticateForResultAsync(SigningKey key, string? issuer = null)
    {
        // One scope per request, as ASP.NET Core gives each request: the handler, and the result it caches, are scoped.
        await using var scope = _services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Authorization = $"Bearer {key.IssueToken(issuer ?? _identityProvider.Issuer, Audience)}";
        return await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
    }

    private async Task WaitUntilJimHoldsAsync(string keyId)
    {
        var manager = _services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme).ConfigurationManager!;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < WaitLimit)
        {
            var configuration = await manager.GetConfigurationAsync(CancellationToken.None);
            if (configuration.SigningKeys.Any(k => k.KeyId == keyId))
                return;
            await Task.Delay(20);
        }

        Assert.Fail($"JIM's configuration manager never fetched the key set holding '{keyId}'.");
    }

    /// <summary>
    /// The identity provider's side: discovery, and a key set the test replaces at will.
    /// </summary>
    private sealed class FakeIdentityProvider
    {
        private volatile string _keySet = """{"keys":[]}""";
        private int _keySetFetches;

        public int Port { get; set; }

        public string Issuer => $"https://localhost:{Port}/realms/jim";

        /// <summary>An access token issuer in the form AD FS publishes: its federation service identifier.</summary>
        public string AdfsAccessTokenIssuer => $"http://localhost:{Port}/adfs/services/trust";

        /// <summary>The <c>access_token_issuer</c> discovery publishes, if any.</summary>
        public string? AccessTokenIssuer { get; set; }

        public int KeySetFetches => Volatile.Read(ref _keySetFetches);

        public void Reset()
        {
            _keySet = """{"keys":[]}""";
            AccessTokenIssuer = null;
            Interlocked.Exchange(ref _keySetFetches, 0);
        }

        public void Publish(params SigningKey[] keys) =>
            _keySet = JsonSerializer.Serialize(new { keys = keys.Select(k => k.ToJwk()).ToArray() });

        public string? Respond(string path)
        {
            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                var discovery = new Dictionary<string, string>
                {
                    ["issuer"] = Issuer,
                    ["jwks_uri"] = $"{Issuer}/protocol/openid-connect/certs",
                    ["authorization_endpoint"] = $"{Issuer}/protocol/openid-connect/auth",
                    ["token_endpoint"] = $"{Issuer}/protocol/openid-connect/token"
                };
                if (AccessTokenIssuer is { } accessTokenIssuer)
                    discovery["access_token_issuer"] = accessTokenIssuer;
                return JsonSerializer.Serialize(discovery);
            }

            if (path.EndsWith("/protocol/openid-connect/certs", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _keySetFetches);
                return _keySet;
            }

            return null;
        }
    }

    /// <summary>
    /// An RSA token-signing key with a key id, as an identity provider holds one.
    /// </summary>
    private sealed class SigningKey : IDisposable
    {
        private readonly RSA _rsa;

        private SigningKey(string keyId, RSA rsa)
        {
            KeyId = keyId;
            _rsa = rsa;
        }

        public string KeyId { get; }

        public static SigningKey Create(string name) => new($"{name}-{Guid.NewGuid():N}", RSA.Create(2048));

        public string IssueToken(string issuer, string audience) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", "rollover-test")]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256)
        });

        public object ToJwk()
        {
            var parameters = _rsa.ExportParameters(includePrivateParameters: false);
            return new Dictionary<string, string>
            {
                ["kty"] = "RSA",
                ["use"] = "sig",
                ["alg"] = "RS256",
                ["kid"] = KeyId,
                ["n"] = Base64UrlEncoder.Encode(parameters.Modulus),
                ["e"] = Base64UrlEncoder.Encode(parameters.Exponent)
            };
        }

        public void Dispose() => _rsa.Dispose();
    }

    private sealed class RecordingFailedAuthenticationRecorder : IFailedAuthenticationRecorder
    {
        private readonly List<string> _reasons = [];

        public IReadOnlyList<string> Reasons
        {
            get { lock (_reasons) return _reasons.ToArray(); }
        }

        public Task RecordAsync(string eventName, string reason, string? apiKeyPrefix, string? clientIp)
        {
            lock (_reasons) _reasons.Add(reason);
            return Task.CompletedTask;
        }
    }
}
