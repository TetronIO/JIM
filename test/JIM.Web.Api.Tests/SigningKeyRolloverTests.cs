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
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// What JIM's <c>/api</c> bearer scheme does when the identity provider rolls its token-signing key (#2442). Each test
/// runs the real <see cref="JwtBearerHandler"/>, configured by <see cref="JimJwtBearerOptions"/> exactly as Program.cs
/// configures it, against a fake identity provider over HTTPS behind <see cref="IdentityProviderBackchannel"/>, whose
/// key set the test swaps at runtime.
/// <para>
/// Findings these pin (IdentityModel 8.19, ASP.NET Core 10):
/// </para>
/// <list type="bullet">
/// <item>A key published before it is used, and fetched by JIM, is accepted on first use.</item>
/// <item>After an unannounced rollover the first request signed by the new key is refused (401, audited as
/// "Access token signing key not found"). The refusal starts a key set refresh on a background thread rather than
/// waiting for one, so the request that triggered it never sees the new key; the next request after that one
/// round trip to the identity provider is accepted.</item>
/// <item>The early refresh is rate-limited by <c>RefreshInterval</c> (5 minutes) from the last early refresh, not
/// from the last fetch. A rollover within 5 minutes of an earlier early refresh (an earlier rollover, or any token
/// naming an unknown key) is refused on every request until that window has passed.</item>
/// <item>A token signed by the retired key is still accepted after the refresh, from IdentityModel's last known good
/// configuration, for up to an hour.</item>
/// </list>
/// <para>
/// The fake identity provider holds its key set response until the first request after the switch has been
/// answered. That models the round trip to a real identity provider, which always outlasts the in-process
/// validation that follows the refresh request, and keeps the tests deterministic. A change that made the refresh
/// wait for the fetch would answer the first request only after the hold is released (5 seconds), accepted, and
/// fail the "refused" assertions here: the point at which these tests, and the guidance built on them, change.
/// </para>
/// </summary>
[TestFixture]
public class SigningKeyRolloverTests
{
    private const string Audience = "jim-api";
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(5);

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
        _identityProvider.ReleaseKeySet();
        _services?.Dispose();
    }

    [Test]
    public void Configure_JimBearerScheme_KeepsIdentityModelsRefreshDefaults()
    {
        // The timings below extrapolate from these: an early refresh at most every 5 minutes, a scheduled one every
        // 12 hours, and an early refresh when a token names a key JIM has not seen.
        var options = new JwtBearerOptions();

        JimJwtBearerOptions.Configure(options, "https://idp.example.com/realms/jim", Audience, ["https://idp.example.com/realms/jim"], _ => Task.CompletedTask);

        Assert.Multiple(() =>
        {
            Assert.That(options.RefreshInterval, Is.EqualTo(TimeSpan.FromMinutes(5)));
            Assert.That(options.AutomaticRefreshInterval, Is.EqualTo(TimeSpan.FromHours(12)));
            Assert.That(options.RefreshOnIssuerKeyNotFound, Is.True);
            Assert.That(options.TokenValidationParameters.ValidateIssuerSigningKey, Is.True);
        });
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
    public async Task AuthenticateAsync_UnannouncedRolloverWithinRefreshIntervalOfLastFetch_FirstRefusedNextAcceptedAsync()
    {
        using var old = SigningKey.Create("old");
        using var replacement = SigningKey.Create("replacement");
        _identityProvider.Publish(old);
        BuildBearerScheme();
        Assert.That(await AuthenticateAsync(old), Is.True, "baseline: JIM fetches the key set and accepts the old key");

        // Seconds after that fetch, the provider replaces its key without notice (a Keycloak key change).
        _identityProvider.Publish(replacement);
        var (first, next, recovery) = await RequestAcrossRolloverAsync(replacement);

        Record("unannounced, within the refresh interval of the last fetch (no early refresh before)", first, next, recovery);
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.False, "first request signed by the new key");
            Assert.That(next, Is.True, "next request, once the background refresh has landed");
            Assert.That(_audit.Reasons, Is.EqualTo(new[] { "Access token signing key not found" }));
        });
    }

    [Test]
    public async Task AuthenticateAsync_UnannouncedRolloverLongAfterLastFetch_FirstRefusedNextAcceptedAsync()
    {
        using var old = SigningKey.Create("old");
        using var stray = SigningKey.Create("stray");
        using var replacement = SigningKey.Create("replacement");
        _identityProvider.Publish(old);

        // A long-running JIM: it fetched the key set, and has since made an early refresh (here forced by a token
        // naming a key the provider never published) whose rate-limit window has since passed. The window is
        // shortened to 1 second so the test need not wait JIM's 5 minutes.
        var refreshInterval = TimeSpan.FromSeconds(1);
        BuildBearerScheme(refreshInterval);
        Assert.That(await AuthenticateAsync(old), Is.True, "baseline");
        Assert.That(await AuthenticateAsync(stray), Is.False, "a token naming an unknown key forces an early refresh");
        await WaitForKeySetFetchesAsync(2);
        await Task.Delay(refreshInterval + TimeSpan.FromMilliseconds(500));

        _identityProvider.Publish(replacement);
        var (first, next, recovery) = await RequestAcrossRolloverAsync(replacement);

        Record("unannounced, long after the last fetch and early refresh", first, next, recovery);
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.False, "first request signed by the new key");
            Assert.That(next, Is.True, "next request, once the background refresh has landed");
            Assert.That(_identityProvider.KeySetFetches, Is.EqualTo(3), "one key set fetch, prompted by the first request");
        });
    }

    [Test]
    public async Task AuthenticateAsync_TokenSignedByRetiredKeyAfterRollover_StillAcceptedFromLastKnownGoodConfigurationAsync()
    {
        using var old = SigningKey.Create("old");
        using var replacement = SigningKey.Create("replacement");
        _identityProvider.Publish(old);
        BuildBearerScheme();
        Assert.That(await AuthenticateAsync(old), Is.True, "baseline");
        _identityProvider.Publish(replacement);
        var (_, next, _) = await RequestAcrossRolloverAsync(replacement);
        Assert.That(next, Is.True, "JIM now holds the provider's new key set, which no longer carries the old key");

        // The retired key is gone from the key set JIM holds, yet a token it signs is still accepted: IdentityModel
        // retries a "signing key not found" failure against its last known good configurations, which by default it
        // keeps for an hour (BaseConfigurationManager.LastKnownGoodLifetime). After a rollover made because the old key
        // was compromised (AD FS Update-AdfsCertificate -Urgent), that is an hour in which a token forged with it is
        // still accepted. Recorded on #2442; a change that stops this fails here.
        var retired = await AuthenticateAsync(old);

        Record("token signed by the retired key, after the refresh", retired, retired, TimeSpan.Zero);
        Assert.That(retired, Is.True);
    }

    [Test]
    public async Task AuthenticateAsync_UnannouncedRolloverWithinRefreshIntervalOfEarlyRefresh_RefusedUntilIntervalPassesAsync()
    {
        using var old = SigningKey.Create("old");
        using var stray = SigningKey.Create("stray");
        using var replacement = SigningKey.Create("replacement");
        _identityProvider.Publish(old);

        // JIM uses a 5-minute window; 10 seconds here keeps the test short while leaving room for the early refresh's
        // own fetch, which can take a few seconds on a Windows machine (localhost is tried over IPv6 first, and the
        // fake identity provider listens on IPv4 only). Every interval scales with it.
        var refreshInterval = TimeSpan.FromSeconds(10);
        BuildBearerScheme(refreshInterval);
        Assert.That(await AuthenticateAsync(old), Is.True, "baseline");
        var earlyRefresh = Stopwatch.StartNew();
        Assert.That(await AuthenticateAsync(stray), Is.False, "a token naming an unknown key forces an early refresh");
        await WaitForKeySetFetchesAsync(2);

        // The provider rolls its key inside the window that early refresh opened.
        _identityProvider.Publish(replacement);
        var refusedInsideWindow = 0;
        while (earlyRefresh.Elapsed < refreshInterval - TimeSpan.FromMilliseconds(750))
        {
            Assert.That(await AuthenticateAsync(replacement), Is.False, "inside the window every request is refused");
            refusedInsideWindow++;
            await Task.Delay(200);
        }

        Assert.That(_identityProvider.KeySetFetches, Is.EqualTo(2), "no key set fetch inside the window");

        var restOfWindow = refreshInterval - earlyRefresh.Elapsed + TimeSpan.FromMilliseconds(250);
        if (restOfWindow > TimeSpan.Zero)
            await Task.Delay(restOfWindow);
        var (first, next, recovery) = await RequestAcrossRolloverAsync(replacement);

        Record($"unannounced, within the refresh interval of an early refresh ({refusedInsideWindow} refusals inside the window)",
            first, next, recovery);
        Assert.Multiple(() =>
        {
            Assert.That(refusedInsideWindow, Is.GreaterThan(0));
            Assert.That(first, Is.False, "first request after the window also refuses, and prompts the fetch");
            Assert.That(next, Is.True, "next request, once that fetch has landed");
            Assert.That(_identityProvider.KeySetFetches, Is.EqualTo(3));
        });
    }

    /// <summary>
    /// Sends the first request signed by <paramref name="key"/> while the provider holds its key set response, then
    /// releases it, waits for the refreshed key set to reach JIM, and sends the next.
    /// </summary>
    private async Task<(bool First, bool Next, TimeSpan Recovery)> RequestAcrossRolloverAsync(SigningKey key)
    {
        var clock = Stopwatch.StartNew();
        _identityProvider.HoldKeySet();
        var firstRequest = AuthenticateAsync(key);
        await Task.WhenAny(firstRequest, Task.Delay(HoldLimit));
        _identityProvider.ReleaseKeySet();
        var first = await firstRequest;

        await WaitUntilJimHoldsAsync(key.KeyId);
        var next = await AuthenticateAsync(key);
        return (first, next, clock.Elapsed);
    }

    /// <summary>
    /// Writes the outcome to the test output, the evidence quoted on #2442. The time is from the first request to
    /// the refreshed key set reaching JIM, which is one round trip to the identity provider (here dominated by the
    /// test machine's certificate chain checks, not by anything JIM waits for).
    /// </summary>
    private static void Record(string scenario, bool first, bool next, TimeSpan recovery) =>
        TestContext.Out.WriteLine($"{scenario}: first request {(first ? "accepted" : "refused")}; next request " +
                                  $"{(next ? "accepted" : "refused")}, {recovery.TotalMilliseconds:F0} ms after the first was sent");

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
            JimJwtBearerOptions.Configure(options, _identityProvider.Issuer, Audience, [_identityProvider.Issuer], _ => Task.CompletedTask);
            if (refreshInterval is { } interval)
                options.RefreshInterval = interval;
        });

        // As AddIdentityProviderTrust does in Program.cs.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure(options => options.BackchannelHttpHandler = backchannel.CreateHandler());
        _services = services.BuildServiceProvider();
    }

    private async Task<bool> AuthenticateAsync(SigningKey key)
    {
        // One scope per request, as ASP.NET Core gives each request: the handler, and the result it caches, are scoped.
        await using var scope = _services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Authorization = $"Bearer {key.IssueToken(_identityProvider.Issuer, Audience)}";
        var result = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        return result.Succeeded;
    }

    private async Task WaitUntilJimHoldsAsync(string keyId)
    {
        var manager = _services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme).ConfigurationManager!;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < HoldLimit)
        {
            var configuration = (OpenIdConnectConfiguration)await manager.GetConfigurationAsync(CancellationToken.None);
            if (configuration.SigningKeys.Any(k => k.KeyId == keyId))
                return;
            await Task.Delay(20);
        }

        Assert.Fail($"JIM never fetched the key set holding '{keyId}'.");
    }

    private async Task WaitForKeySetFetchesAsync(int count)
    {
        var deadline = Stopwatch.StartNew();
        while (_identityProvider.KeySetFetches < count && deadline.Elapsed < HoldLimit)
            await Task.Delay(20);
        Assert.That(_identityProvider.KeySetFetches, Is.EqualTo(count), "key set fetches");

        // The fetch has been answered; give the configuration manager a moment to take it in.
        await Task.Delay(200);
    }

    /// <summary>
    /// The identity provider's side: discovery, and a key set the test replaces at will.
    /// </summary>
    private sealed class FakeIdentityProvider
    {
        private volatile string _keySet = """{"keys":[]}""";
        private readonly ManualResetEventSlim _keySetReleased = new(true);
        private int _keySetFetches;

        public int Port { get; set; }

        public string Issuer => $"https://localhost:{Port}/realms/jim";

        public int KeySetFetches => Volatile.Read(ref _keySetFetches);

        public void Reset()
        {
            _keySet = """{"keys":[]}""";
            _keySetReleased.Set();
            Interlocked.Exchange(ref _keySetFetches, 0);
        }

        public void Publish(params SigningKey[] keys) =>
            _keySet = JsonSerializer.Serialize(new { keys = keys.Select(k => k.ToJwk()).ToArray() });

        public void HoldKeySet() => _keySetReleased.Reset();

        public void ReleaseKeySet() => _keySetReleased.Set();

        public string? Respond(string path)
        {
            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
                return JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["issuer"] = Issuer,
                    ["jwks_uri"] = $"{Issuer}/protocol/openid-connect/certs",
                    ["authorization_endpoint"] = $"{Issuer}/protocol/openid-connect/auth",
                    ["token_endpoint"] = $"{Issuer}/protocol/openid-connect/token"
                });

            if (path.EndsWith("/protocol/openid-connect/certs", StringComparison.Ordinal))
            {
                _keySetReleased.Wait(HoldLimit);
                var keySet = _keySet;
                Interlocked.Increment(ref _keySetFetches);
                return keySet;
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
