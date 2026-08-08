// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using JIM.Connectors.SCIM.Authentication;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers the four authentication methods the SCIM connector offers. Every case runs against a stub
/// message handler, so no test reaches a network or a real authorisation server.
/// </summary>
[TestFixture]
public class ScimAuthenticationTests
{
    private const string TokenEndpoint = "https://provider.example.com/oauth2/token";
    private static readonly DateTimeOffset Start = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    private static HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, "https://provider.example.com/scim/v2/Users");

    private static HttpResponseMessage TokenResponse(string accessToken, int? expiresInSeconds = 3600)
    {
        var expiry = expiresInSeconds.HasValue ? $", \"expires_in\": {expiresInSeconds.Value}" : string.Empty;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"access_token\": \"{accessToken}\", \"token_type\": \"Bearer\"{expiry}}}",
                Encoding.UTF8, "application/json")
        };
    }

    #region static credential strategies

    [Test]
    public async Task BasicAuthentication_ApplyAsync_SetsBasicHeaderWithEncodedCredentialsAsync()
    {
        var strategy = new ScimBasicAuthentication("scim-service", "s3cr3t");
        using var request = CreateRequest();

        await strategy.ApplyAsync(request, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Headers.Authorization?.Scheme, Is.EqualTo("Basic"));
            Assert.That(request.Headers.Authorization?.Parameter,
                Is.EqualTo(Convert.ToBase64String(Encoding.UTF8.GetBytes("scim-service:s3cr3t"))));
        }
    }

    [Test]
    public async Task BearerTokenAuthentication_ApplyAsync_SetsBearerHeaderAsync()
    {
        var strategy = new ScimStaticBearerTokenAuthentication("static-token-value");
        using var request = CreateRequest();

        await strategy.ApplyAsync(request, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Headers.Authorization?.Scheme, Is.EqualTo("Bearer"));
            Assert.That(request.Headers.Authorization?.Parameter, Is.EqualTo("static-token-value"));
        }
    }

    [Test]
    public async Task CustomHeaderAuthentication_ApplyAsync_SetsNamedHeaderAndLeavesAuthorizationAloneAsync()
    {
        var strategy = new ScimCustomHeaderAuthentication("X-Api-Key", "api-key-value");
        using var request = CreateRequest();

        await strategy.ApplyAsync(request, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Headers.GetValues("X-Api-Key").Single(), Is.EqualTo("api-key-value"));
            Assert.That(request.Headers.Authorization, Is.Null);
        }
    }

    [Test]
    public async Task CustomHeaderAuthentication_AppliedToARequestAlreadyCarryingTheHeader_DoesNotDuplicateItAsync()
    {
        var strategy = new ScimCustomHeaderAuthentication("X-Api-Key", "fresh-value");
        using var request = CreateRequest();
        request.Headers.TryAddWithoutValidation("X-Api-Key", "stale-value");

        await strategy.ApplyAsync(request, CancellationToken.None);

        Assert.That(request.Headers.GetValues("X-Api-Key"), Is.EqualTo(new[] { "fresh-value" }));
    }

    #endregion

    #region OAuth 2.0 client credentials

    [Test]
    public async Task OAuth_FirstRequest_AcquiresTokenAndAppliesItAsync()
    {
        var handler = new StubHttpMessageHandler(_ => TokenResponse("issued-token"));
        using var tokenClient = new HttpClient(handler);
        var strategy = CreateOAuthStrategy(tokenClient, scope: "scim:read scim:write");
        using var request = CreateRequest();

        await strategy.ApplyAsync(request, CancellationToken.None);

        var tokenRequest = handler.Requests.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Headers.Authorization?.Parameter, Is.EqualTo("issued-token"));
            Assert.That(tokenRequest.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(tokenRequest.RequestUri?.ToString(), Is.EqualTo(TokenEndpoint));
            Assert.That(tokenRequest.Body, Does.Contain("grant_type=client_credentials"));
            Assert.That(tokenRequest.Body, Does.Contain("client_id=scim-client"));
            Assert.That(tokenRequest.Body, Does.Contain("scope=scim"));
        }
    }

    [Test]
    public async Task OAuth_NoScopeConfigured_OmitsScopeFromTokenRequestAsync()
    {
        var handler = new StubHttpMessageHandler(_ => TokenResponse("issued-token"));
        using var tokenClient = new HttpClient(handler);
        var strategy = CreateOAuthStrategy(tokenClient, scope: null);
        using var request = CreateRequest();

        await strategy.ApplyAsync(request, CancellationToken.None);

        Assert.That(handler.Requests.Single().Body, Does.Not.Contain("scope"));
    }

    [Test]
    public async Task OAuth_SecondRequestWhileTokenIsValid_ReusesCachedTokenAsync()
    {
        var handler = new StubHttpMessageHandler(_ => TokenResponse("issued-token", expiresInSeconds: 3600));
        using var tokenClient = new HttpClient(handler);
        var strategy = CreateOAuthStrategy(tokenClient);

        using var first = CreateRequest();
        using var second = CreateRequest();
        await strategy.ApplyAsync(first, CancellationToken.None);
        await strategy.ApplyAsync(second, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.CallCount, Is.EqualTo(1), "a cached, unexpired token must not be re-acquired.");
            Assert.That(second.Headers.Authorization?.Parameter, Is.EqualTo("issued-token"));
        }
    }

    [Test]
    public async Task OAuth_TokenNearingExpiry_IsRefreshedBeforeItLapsesAsync()
    {
        // A token valid for 60s must not be reused right up to the last moment: an in-flight request
        // would arrive with an expired token. The safety margin forces an early refresh.
        var now = Start;
        var handler = new StubHttpMessageHandler((_, call) => Task.FromResult(TokenResponse($"token-{call}", expiresInSeconds: 60)));
        using var tokenClient = new HttpClient(handler);
        var strategy = CreateOAuthStrategy(tokenClient, utcNow: () => now);

        using var first = CreateRequest();
        await strategy.ApplyAsync(first, CancellationToken.None);

        now = Start.AddSeconds(45);
        using var second = CreateRequest();
        await strategy.ApplyAsync(second, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.CallCount, Is.EqualTo(2));
            Assert.That(second.Headers.Authorization?.Parameter, Is.EqualTo("token-2"));
        }
    }

    [Test]
    public async Task OAuth_ResponseWithoutExpiry_TreatsTokenAsSingleUseAsync()
    {
        // With no expires_in the lifetime is unknown; assuming it is long-lived risks silent 401s.
        var handler = new StubHttpMessageHandler((_, call) => Task.FromResult(TokenResponse($"token-{call}", expiresInSeconds: null)));
        using var tokenClient = new HttpClient(handler);
        var strategy = CreateOAuthStrategy(tokenClient);

        using var first = CreateRequest();
        using var second = CreateRequest();
        await strategy.ApplyAsync(first, CancellationToken.None);
        await strategy.ApplyAsync(second, CancellationToken.None);

        Assert.That(handler.CallCount, Is.EqualTo(2));
    }

    [Test]
    public async Task OAuth_ConcurrentRequests_AcquireOnlyOneTokenAsync()
    {
        // Parallel export is a supported capability, so many callers hit the strategy at once.
        // Without a guard they would stampede the authorisation server with identical token requests.
        var gate = new TaskCompletionSource();
        var handler = new StubHttpMessageHandler(async (_, call) =>
        {
            await gate.Task;
            return TokenResponse($"token-{call}");
        });
        using var tokenClient = new HttpClient(handler);
        var strategy = CreateOAuthStrategy(tokenClient);

        var requests = Enumerable.Range(0, 10).Select(_ => CreateRequest()).ToList();
        var applies = requests.Select(r => strategy.ApplyAsync(r, CancellationToken.None)).ToList();
        gate.SetResult();
        await Task.WhenAll(applies);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.CallCount, Is.EqualTo(1), "concurrent callers must share a single token acquisition.");
            Assert.That(requests.Select(r => r.Headers.Authorization?.Parameter), Is.All.EqualTo("token-1"));
        }
        foreach (var request in requests)
            request.Dispose();
    }

    [Test]
    public async Task OAuth_InvalidateCachedCredentials_ForcesReacquisitionAsync()
    {
        // The client calls this after a 401, so a token revoked early is replaced rather than retried.
        var handler = new StubHttpMessageHandler((_, call) => Task.FromResult(TokenResponse($"token-{call}")));
        using var tokenClient = new HttpClient(handler);
        var strategy = CreateOAuthStrategy(tokenClient);

        using var first = CreateRequest();
        await strategy.ApplyAsync(first, CancellationToken.None);
        strategy.InvalidateCachedCredentials();
        using var second = CreateRequest();
        await strategy.ApplyAsync(second, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.CallCount, Is.EqualTo(2));
            Assert.That(second.Headers.Authorization?.Parameter, Is.EqualTo("token-2"));
        }
    }

    [Test]
    public void OAuth_TokenEndpointRejectsRequest_ThrowsWithoutLeakingTheSecret()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\": \"invalid_client\"}", Encoding.UTF8, "application/json")
        });
        using var tokenClient = new HttpClient(handler);
        var strategy = CreateOAuthStrategy(tokenClient);
        using var request = CreateRequest();

        var exception = Assert.ThrowsAsync<ScimAuthenticationException>(
            async () => await strategy.ApplyAsync(request, CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Does.Contain("401"));
            Assert.That(exception.Message, Does.Not.Contain("top-secret"), "the client secret must never reach an error message.");
            Assert.That(exception.ToString(), Does.Not.Contain("top-secret"));
        }
    }

    [Test]
    public void OAuth_TokenResponseWithoutAccessToken_Throws()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"token_type\": \"Bearer\"}", Encoding.UTF8, "application/json")
        });
        using var tokenClient = new HttpClient(handler);
        var strategy = CreateOAuthStrategy(tokenClient);
        using var request = CreateRequest();

        Assert.ThrowsAsync<ScimAuthenticationException>(async () => await strategy.ApplyAsync(request, CancellationToken.None));
    }

    #endregion

    private static ScimOAuthClientCredentialsAuthentication CreateOAuthStrategy(
        HttpClient tokenClient,
        string? scope = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        return new ScimOAuthClientCredentialsAuthentication(
            tokenClient,
            new Uri(TokenEndpoint),
            clientId: "scim-client",
            clientSecret: "top-secret",
            scope: scope,
            utcNow: utcNow ?? (() => Start));
    }
}
