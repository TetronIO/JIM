// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using JIM.Connectors.Graph;
using JIM.Connectors.Http;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class GraphHttpClientTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    private ILogger _logger = null!;
    private List<TimeSpan> _delays = null!;
    private FakeAuthentication _authentication = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
        _delays = [];
        _authentication = new FakeAuthentication();
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();
    }

    [Test]
    public async Task SendAsync_RelativePath_IsSentToTheCloudsV1EndpointAsync()
    {
        var handler = Respond(HttpStatusCode.OK);
        using var client = CreateClient(handler, GraphCloud.UsGovernmentL5);

        using var sent = await client.SendAsync(HttpMethod.Get, "users?$select=id", null, null, CancellationToken.None);

        Assert.That(handler.Requests.Single().RequestUri!.AbsoluteUri, Is.EqualTo("https://dod-graph.microsoft.us/v1.0/users?$select=id"));
    }

    [Test]
    public async Task SendAsync_EveryRequest_IsLowPriorityAuthenticatedAndCarriesAClientRequestIdAsync()
    {
        var handler = Respond(HttpStatusCode.OK);
        using var client = CreateClient(handler);

        using var sent = await client.SendAsync(HttpMethod.Get, "users", null, null, CancellationToken.None);

        var headers = handler.Requests.Single().Headers;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(headers.GetValues("x-ms-throttle-priority").Single(), Is.EqualTo("Low"));
            Assert.That(Guid.TryParse(headers.GetValues("client-request-id").Single(), out _), Is.True);
            Assert.That(headers.Authorization?.Parameter, Is.EqualTo("token-1"));
            Assert.That(headers.Contains("ConsistencyLevel"), Is.False);
        }
    }

    [Test]
    public async Task SendAsync_EventualConsistency_SendsTheConsistencyLevelHeaderAsync()
    {
        var handler = Respond(HttpStatusCode.OK);
        using var client = CreateClient(handler);

        using var sent = await client.SendAsync(HttpMethod.Get, "users/$count", null, new GraphRequestOptions { EventualConsistency = true }, CancellationToken.None);

        Assert.That(handler.Requests.Single().Headers.GetValues("ConsistencyLevel").Single(), Is.EqualTo("eventual"));
    }

    [Test]
    public async Task SendAsync_Body_IsSentAsCamelCaseJsonAsync()
    {
        var handler = Respond(HttpStatusCode.Created);
        using var client = CreateClient(handler);

        using var sent = await client.SendAsync(HttpMethod.Post, "users", new { DisplayName = "Ada", AccountEnabled = true }, null, CancellationToken.None);

        Assert.That(handler.Requests.Single().Body, Is.EqualTo("""{"displayName":"Ada","accountEnabled":true}"""));
    }

    [Test]
    public async Task SendAsync_AbsoluteUrlOnTheCloudsGraphHost_IsFollowedAsync()
    {
        // Graph pages with absolute @odata.nextLink and @odata.deltaLink URLs.
        var handler = Respond(HttpStatusCode.OK);
        using var client = CreateClient(handler);

        using var sent = await client.SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/users?$skiptoken=abc", null, null, CancellationToken.None);

        Assert.That(handler.Requests.Single().RequestUri!.AbsoluteUri, Is.EqualTo("https://graph.microsoft.com/v1.0/users?$skiptoken=abc"));
    }

    [TestCase("https://attacker.example.com/v1.0/users")]
    [TestCase("http://graph.microsoft.com/v1.0/users")]
    [TestCase("https://graph.microsoft.us/v1.0/users")]
    public void SendAsync_AbsoluteUrlElsewhere_IsRefusedWithoutSendingTheToken(string url)
    {
        // The access token goes with every request, so a link that points anywhere but this cloud's Graph is never followed.
        var handler = Respond(HttpStatusCode.OK);
        using var client = CreateClient(handler);

        Assert.ThrowsAsync<GraphRequestException>(async () => await client.SendAsync(HttpMethod.Get, url, null, null, CancellationToken.None));
        Assert.That(handler.CallCount, Is.Zero);
    }

    [Test]
    public async Task SendAsync_Throttled_HonoursRetryAfterAndCountsItAsync()
    {
        var handler = new StubHttpMessageHandler((_, call) => Task.FromResult(call == 1
            ? WithRetryAfter(new HttpResponseMessage(HttpStatusCode.TooManyRequests), 7)
            : new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = CreateClient(handler);

        using var response = await client.SendAsync(HttpMethod.Get, "users", null, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(_delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(7) }));
            Assert.That(client.ThrottledResponseCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task SendAsync_Unauthorised_RefreshesTheTokenOnceAndRetriesAsync()
    {
        var handler = new StubHttpMessageHandler((_, call) => Task.FromResult(new HttpResponseMessage(call == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)));
        using var client = CreateClient(handler);

        using var response = await client.SendAsync(HttpMethod.Get, "users", null, null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(_authentication.Invalidations, Is.EqualTo(1));
            Assert.That(handler.Requests[1].Headers.Authorization?.Parameter, Is.EqualTo("token-2"));
        }
    }

    [Test]
    public void SendAsync_PersistentlyUnavailable_ThrowsAfterEveryConfiguredRetry()
    {
        var handler = Respond(HttpStatusCode.ServiceUnavailable);
        using var client = CreateClient(handler);

        var exception = Assert.ThrowsAsync<GraphRequestException>(async () => await client.SendAsync(HttpMethod.Get, "users", null, null, CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(handler.CallCount, Is.EqualTo(4), "the initial attempt plus the three configured retries.");
        }
    }

    [Test]
    public void SendAsync_UnauthorisedTwice_Throws()
    {
        var handler = Respond(HttpStatusCode.Unauthorized);
        using var client = CreateClient(handler);

        var exception = Assert.ThrowsAsync<GraphRequestException>(async () => await client.SendAsync(HttpMethod.Get, "users", null, null, CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(handler.CallCount, Is.EqualTo(2));
        }
    }

    [Test]
    public void SendAsync_Refused_ThrowsWithGraphsErrorCodeMessageAndRequestId()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}""", Encoding.UTF8, "application/json")
            };
            response.Headers.Add("request-id", "4f1e9d2a-0000-0000-0000-000000000001");
            return response;
        });
        using var client = CreateClient(handler);

        var exception = Assert.ThrowsAsync<GraphRequestException>(async () => await client.SendAsync(HttpMethod.Get, "users", null, null, CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(exception.ErrorCode, Is.EqualTo("Authorization_RequestDenied"));
            Assert.That(exception.RequestId, Is.EqualTo("4f1e9d2a-0000-0000-0000-000000000001"));
            Assert.That(exception.Message, Does.Contain("Insufficient privileges"));
            Assert.That(handler.CallCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void SendAsync_RefusedWithANonJsonBody_StillReportsTheStatus()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<html>not found</html>") });
        using var client = CreateClient(handler);

        var exception = Assert.ThrowsAsync<GraphRequestException>(async () => await client.SendAsync(HttpMethod.Get, "users/x", null, null, CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(exception.ErrorCode, Is.Null);
            Assert.That(exception.Message, Does.Contain("404"));
        }
    }

    [Test]
    public async Task SendAsync_Writes_ArePacedButReadsAreNotAsync()
    {
        var handler = Respond(HttpStatusCode.OK);
        using var client = CreateClient(handler, writesPerSecond: 10);

        using (await client.SendAsync(HttpMethod.Get, "users", null, null, CancellationToken.None)) { }
        using (await client.SendAsync(HttpMethod.Get, "users", null, null, CancellationToken.None)) { }
        using (await client.SendAsync(HttpMethod.Patch, "users/1", new { }, null, CancellationToken.None)) { }
        using (await client.SendAsync(HttpMethod.Patch, "users/2", new { }, null, CancellationToken.None)) { }

        Assert.That(_delays, Is.EqualTo(new[] { TimeSpan.FromMilliseconds(100) }));
    }

    [Test]
    public async Task SendAsync_ThrottleLimitPercentageHeader_SlowsLaterWritesAsync()
    {
        var pacer = new GraphRequestPacer(10, () => Start, (_, _) => Task.CompletedTask);
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Add("x-ms-throttle-limit-percentage", "0.9");
            return response;
        });
        using var client = CreateClient(handler, pacer: pacer);

        using var sent = await client.SendAsync(HttpMethod.Get, "users", null, null, CancellationToken.None);

        Assert.That(pacer.SlowdownFactor, Is.EqualTo(2));
    }

    [Test]
    public void Dispose_DisposesTheOwnedCredential()
    {
        var owned = new DisposalProbe();
        var client = CreateClient(Respond(HttpStatusCode.OK), ownedCredential: owned);

        client.Dispose();

        Assert.That(owned.Disposed, Is.True);
    }

    private GraphHttpClient CreateClient(
        StubHttpMessageHandler handler,
        GraphCloud? cloud = null,
        double writesPerSecond = 10,
        GraphRequestPacer? pacer = null,
        IDisposable? ownedCredential = null)
    {
        Task Delay(TimeSpan delay, CancellationToken _)
        {
            _delays.Add(delay);
            return Task.CompletedTask;
        }

        // The pacer's clock stands still, so every write after the first waits a full interval.
        pacer ??= new GraphRequestPacer(writesPerSecond, () => Start, Delay);
        return new GraphHttpClient(
            new HttpClient(handler),
            cloud ?? GraphCloud.Global,
            _authentication,
            new HttpRetryPolicy(3, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), jitterFactor: 0),
            pacer,
            _logger,
            ownedCredential,
            Delay,
            () => Start);
    }

    private static StubHttpMessageHandler Respond(HttpStatusCode status) => new(_ => new HttpResponseMessage(status));

    private static HttpResponseMessage WithRetryAfter(HttpResponseMessage response, int seconds)
    {
        response.Headers.Add("Retry-After", seconds.ToString());
        return response;
    }

    private sealed class FakeAuthentication : IHttpAuthenticationStrategy
    {
        private int _issued;

        public int Invalidations { get; private set; }

        public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $"token-{_issued + 1}");
            return Task.CompletedTask;
        }

        public void InvalidateCachedCredentials()
        {
            Invalidations++;
            _issued++;
        }
    }

    private sealed class DisposalProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
