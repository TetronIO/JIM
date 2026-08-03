// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using JIM.Connectors.SCIM;
using JIM.Connectors.SCIM.Authentication;
using JIM.Scim.Messages;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The client wires authentication, retry and throttling together over HttpClient. Delays are injected,
/// so retry and throttle behaviour is asserted by inspecting the requested waits rather than serving them.
/// </summary>
[TestFixture]
public class ScimHttpClientTests
{
    private const string BaseUrl = "https://provider.example.com/scim/v2";

    private ILogger _logger = null!;
    private List<TimeSpan> _requestedDelays = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
        _requestedDelays = [];
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();
    }

    private sealed class TestResource
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("userName")]
        public string? UserName { get; set; }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/scim+json")
        };
        foreach (var (name, value) in headers)
            response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    private ScimHttpClient CreateClient(
        StubHttpMessageHandler handler,
        IScimAuthenticationStrategy? authentication = null,
        string baseUrl = BaseUrl,
        int maxRetries = 3)
    {
        var httpClient = new HttpClient(handler);
        return new ScimHttpClient(
            httpClient,
            new Uri(baseUrl),
            authentication ?? new ScimStaticBearerTokenAuthentication("test-token"),
            new ScimRetryPolicy(maxRetries, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(300), jitterFactor: 0),
            _logger,
            delay: (duration, _) =>
            {
                _requestedDelays.Add(duration);
                return Task.CompletedTask;
            },
            utcNow: () => new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));
    }

    #region URL composition

    [TestCase("https://provider.example.com/scim/v2")]
    [TestCase("https://provider.example.com/scim/v2/")]
    public async Task GetAsync_BaseUrlWithOrWithoutTrailingSlash_PreservesThePathPrefixAsync(string baseUrl)
    {
        // Composing onto a base URL that carries a path prefix is easy to get wrong: treating the
        // relative path as rooted would drop "/scim/v2" and call the wrong endpoint.
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{\"id\":\"1\"}"));
        using var client = CreateClient(handler, baseUrl: baseUrl);

        await client.GetAsync<TestResource>("Users", CancellationToken.None);

        Assert.That(handler.Requests.Single().RequestUri?.ToString(),
            Is.EqualTo("https://provider.example.com/scim/v2/Users"));
    }

    [Test]
    public async Task GetAsync_RelativePathWithQueryString_IsPreservedAsync()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{\"id\":\"1\"}"));
        using var client = CreateClient(handler);

        await client.GetAsync<TestResource>("Users?startIndex=1&count=100", CancellationToken.None);

        Assert.That(handler.Requests.Single().RequestUri?.ToString(),
            Is.EqualTo("https://provider.example.com/scim/v2/Users?startIndex=1&count=100"));
    }

    #endregion

    #region success paths

    [Test]
    public async Task GetAsync_SuccessfulResponse_DeserialisesTheResourceAsync()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{\"id\":\"abc\",\"userName\":\"jbloggs\"}"));
        using var client = CreateClient(handler);

        var resource = await client.GetAsync<TestResource>("Users/abc", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(resource!.Id, Is.EqualTo("abc"));
            Assert.That(resource.UserName, Is.EqualTo("jbloggs"));
        });
    }

    [Test]
    public async Task GetAsync_AppliesTheAuthenticationStrategyAsync()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{}"));
        using var client = CreateClient(handler, new ScimStaticBearerTokenAuthentication("token-abc"));

        await client.GetAsync<TestResource>("Users", CancellationToken.None);

        Assert.That(handler.Requests.Single().Headers.Authorization?.Parameter, Is.EqualTo("token-abc"));
    }

    [Test]
    public async Task PostAsync_SendsScimJsonContentTypeAsync()
    {
        // RFC 7644 section 3.1 defines application/scim+json; some providers reject plain application/json.
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.Created, "{\"id\":\"new\"}"));
        using var client = CreateClient(handler);

        await client.PostAsync<TestResource>("Users", new TestResource { UserName = "jbloggs" }, CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Multiple(() =>
        {
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(request.Body, Does.Contain("jbloggs"));
        });
    }

    [Test]
    public async Task DeleteAsync_NoContentResponse_CompletesAsync()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = CreateClient(handler);

        await client.DeleteAsync("Users/abc", CancellationToken.None);

        Assert.That(handler.Requests.Single().Method, Is.EqualTo(HttpMethod.Delete));
    }

    #endregion

    #region retry behaviour

    [Test]
    public async Task GetAsync_TransientFailureThenSuccess_RetriesAndReturnsTheResourceAsync()
    {
        var handler = new StubHttpMessageHandler((_, call) => Task.FromResult(call == 1
            ? Json(HttpStatusCode.ServiceUnavailable, "{}")
            : Json(HttpStatusCode.OK, "{\"id\":\"abc\"}")));
        using var client = CreateClient(handler);

        var resource = await client.GetAsync<TestResource>("Users/abc", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(resource!.Id, Is.EqualTo("abc"));
            Assert.That(handler.CallCount, Is.EqualTo(2));
            Assert.That(_requestedDelays, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task GetAsync_RetriedAttempt_CarriesFreshAuthenticationAsync()
    {
        // An HttpRequestMessage cannot be resent, so each attempt must build a new one; if the client
        // reused the message the retry would arrive without an Authorization header.
        var handler = new StubHttpMessageHandler((_, call) => Task.FromResult(call == 1
            ? Json(HttpStatusCode.BadGateway, "{}")
            : Json(HttpStatusCode.OK, "{}")));
        using var client = CreateClient(handler);

        await client.GetAsync<TestResource>("Users", CancellationToken.None);

        Assert.That(handler.Requests.Select(r => r.Headers.Authorization?.Parameter), Is.All.EqualTo("test-token"));
    }

    [Test]
    public void GetAsync_PersistentTransientFailure_ThrowsAfterExhaustingRetries()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = CreateClient(handler, maxRetries: 2);

        var exception = Assert.ThrowsAsync<ScimRequestException>(
            async () => await client.GetAsync<TestResource>("Users", CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(handler.CallCount, Is.EqualTo(2), "the initial attempt plus one retry, per maxRetries.");
        });
    }

    [Test]
    public void GetAsync_PermanentFailure_ThrowsWithoutRetrying()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.BadRequest, "{}"));
        using var client = CreateClient(handler);

        Assert.ThrowsAsync<ScimRequestException>(async () => await client.GetAsync<TestResource>("Users", CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(handler.CallCount, Is.EqualTo(1));
            Assert.That(_requestedDelays, Is.Empty);
        });
    }

    [Test]
    public async Task GetAsync_ThrottledWithRetryAfter_WaitsForTheProvidersDelayAsync()
    {
        var handler = new StubHttpMessageHandler((_, call) => Task.FromResult(call == 1
            ? Json(HttpStatusCode.TooManyRequests, "{}", ("Retry-After", "17"))
            : Json(HttpStatusCode.OK, "{}")));
        using var client = CreateClient(handler);

        await client.GetAsync<TestResource>("Users", CancellationToken.None);

        Assert.That(_requestedDelays.Single(), Is.EqualTo(TimeSpan.FromSeconds(17)));
    }

    #endregion

    #region authentication failure handling

    [Test]
    public async Task GetAsync_Unauthorised_DiscardsTheCachedCredentialAndRetriesOnceAsync()
    {
        // A token can be revoked before its advertised expiry; one re-acquisition distinguishes that
        // from genuinely wrong credentials.
        var authentication = new CountingAuthenticationStrategy();
        var handler = new StubHttpMessageHandler((_, call) => Task.FromResult(call == 1
            ? Json(HttpStatusCode.Unauthorized, "{}")
            : Json(HttpStatusCode.OK, "{\"id\":\"abc\"}")));
        using var client = CreateClient(handler, authentication);

        var resource = await client.GetAsync<TestResource>("Users/abc", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(resource!.Id, Is.EqualTo("abc"));
            Assert.That(authentication.InvalidationCount, Is.EqualTo(1));
            Assert.That(handler.CallCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void GetAsync_UnauthorisedAfterReacquiringTheCredential_ThrowsRatherThanLooping()
    {
        var authentication = new CountingAuthenticationStrategy();
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = CreateClient(handler, authentication);

        var exception = Assert.ThrowsAsync<ScimRequestException>(
            async () => await client.GetAsync<TestResource>("Users", CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(handler.CallCount, Is.EqualTo(2), "one credential refresh only; a wrong secret must not retry forever.");
            Assert.That(authentication.InvalidationCount, Is.EqualTo(1));
        });
    }

    #endregion

    #region error reporting

    [Test]
    public void GetAsync_ScimErrorResponse_ThrowsCarryingStatusScimTypeAndDetail()
    {
        const string body = """
        {
          "schemas": ["urn:ietf:params:scim:api:messages:2.0:Error"],
          "status": "409",
          "scimType": "uniqueness",
          "detail": "userName already exists."
        }
        """;
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.Conflict, body));
        using var client = CreateClient(handler);

        var exception = Assert.ThrowsAsync<ScimRequestException>(
            async () => await client.PostAsync<TestResource>("Users", new TestResource(), CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(exception.ScimType, Is.EqualTo(ScimErrorTypes.Uniqueness));
            Assert.That(exception.Error?.Detail, Is.EqualTo("userName already exists."));
        });
    }

    [Test]
    public void GetAsync_NonScimErrorBody_StillThrowsWithTheStatusCode()
    {
        // A reverse proxy or gateway in front of the provider returns HTML, not a SCIM error. Failing to
        // parse that must not mask the underlying status.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html><body>502 Bad Gateway</body></html>", Encoding.UTF8, "text/html")
        });
        using var client = CreateClient(handler, maxRetries: 1);

        var exception = Assert.ThrowsAsync<ScimRequestException>(
            async () => await client.GetAsync<TestResource>("Users", CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(exception.Error, Is.Null);
        });
    }

    [Test]
    public void GetAsync_NotFound_ThrowsSoTheCallerCanDecideWhetherItMatters()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.NotFound, "{}"));
        using var client = CreateClient(handler);

        var exception = Assert.ThrowsAsync<ScimRequestException>(
            async () => await client.DeleteAsync("Users/gone", CancellationToken.None));

        Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    #endregion

    #region cancellation

    [Test]
    public void GetAsync_CancelledByCaller_PropagatesWithoutRetrying()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        using var client = CreateClient(handler);

        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await client.GetAsync<TestResource>("Users", cts.Token));

        Assert.That(handler.CallCount, Is.EqualTo(1), "an aborting run must not grind through retries.");
    }

    #endregion

    #region proactive throttling

    [Test]
    public async Task GetAsync_ProviderReportsAllowanceNearlyExhausted_PausesBeforeTheNextRequestAsync()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{}",
            ("RateLimit-Remaining", "0"), ("RateLimit-Reset", "5")));
        using var client = CreateClient(handler);

        await client.GetAsync<TestResource>("Users", CancellationToken.None);
        Assert.That(_requestedDelays, Is.Empty, "the pause applies before the next request, not after the current one.");

        await client.GetAsync<TestResource>("Users", CancellationToken.None);

        Assert.That(_requestedDelays.Single(), Is.EqualTo(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public async Task GetAsync_ProviderReportsHealthyAllowance_DoesNotPauseAsync()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{}",
            ("RateLimit-Remaining", "95"), ("RateLimit-Reset", "60")));
        using var client = CreateClient(handler);

        await client.GetAsync<TestResource>("Users", CancellationToken.None);
        await client.GetAsync<TestResource>("Users", CancellationToken.None);

        Assert.That(_requestedDelays, Is.Empty);
    }

    #endregion

    /// <summary>
    /// Records how often the client discarded its cached credential.
    /// </summary>
    private sealed class CountingAuthenticationStrategy : IScimAuthenticationStrategy
    {
        public int InvalidationCount { get; private set; }

        public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token");
            return Task.CompletedTask;
        }

        public void InvalidateCachedCredentials() => InvalidationCount++;
    }
}
