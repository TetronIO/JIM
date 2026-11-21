// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using JIM.Connectors.Graph;
using JIM.Connectors.Http;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// <c>$batch</c> carries up to 20 requests in one call. Graph answers each separately and in any order, so answers are
/// matched by id, a throttled request is retried on its own, and a request with no answer is a failure.
/// </summary>
[TestFixture]
public class GraphBatchTests
{
    private ILogger _logger = null!;
    private List<TimeSpan> _delays = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
        _delays = [];
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();
    }

    [Test]
    public async Task SendBatchAsync_SendsEveryRequestWithAnIdToTheBatchEndpointAsync()
    {
        var handler = BatchHandler(_ => Answer(new R("1", 200, """{"value":[]}"""), new R("2", 200, """{"value":[]}""")));
        using var client = CreateClient(handler);

        await client.SendBatchAsync([new(HttpMethod.Get, "/groups/a/members?$select=id"), new(HttpMethod.Get, "/groups/b/members?$select=id")], CancellationToken.None);

        var sent = handler.Requests.Single();
        using var body = JsonDocument.Parse(sent.Body!);
        var requests = body.RootElement.GetProperty("requests").EnumerateArray().ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(sent.RequestUri!.AbsoluteUri, Is.EqualTo("https://graph.microsoft.com/v1.0/$batch"));
            Assert.That(requests.Select(r => r.GetProperty("id").GetString()), Is.EqualTo(new[] { "1", "2" }));
            Assert.That(requests.Select(r => r.GetProperty("method").GetString()), Is.EqualTo(new[] { "GET", "GET" }));
            Assert.That(requests[0].GetProperty("url").GetString(), Is.EqualTo("/groups/a/members?$select=id"));
        }
    }

    [Test]
    public async Task SendBatchAsync_AnswersOutOfOrder_AreMatchedToTheirRequestsAsync()
    {
        var handler = BatchHandler(_ => Answer(new R("2", 404, """{"error":{"code":"Request_ResourceNotFound"}}"""), new R("1", 200, """{"value":["first"]}""")));
        using var client = CreateClient(handler);

        var responses = await client.SendBatchAsync([new(HttpMethod.Get, "/a"), new(HttpMethod.Get, "/b")], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(responses[0].Status, Is.EqualTo(200));
            Assert.That(responses[0].Body!.Value.GetProperty("value")[0].GetString(), Is.EqualTo("first"));
            Assert.That(responses[1].Status, Is.EqualTo(404));
        }
    }

    [Test]
    public async Task SendBatchAsync_ThrottledRequest_IsRetriedAloneAfterItsRetryAfterAsync()
    {
        var handler = BatchHandler(call => call == 1
            ? Answer(new R("1", 200, """{"value":[]}"""), new R("2", 429, """{"error":{"code":"TooManyRequests"}}""", 9))
            : Answer(new R("2", 200, """{"value":["retried"]}""")));
        using var client = CreateClient(handler);

        var responses = await client.SendBatchAsync([new(HttpMethod.Get, "/a"), new(HttpMethod.Get, "/b")], CancellationToken.None);

        using var retry = JsonDocument.Parse(handler.Requests[1].Body!);
        var retried = retry.RootElement.GetProperty("requests").EnumerateArray().ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(retried.Select(r => r.GetProperty("url").GetString()), Is.EqualTo(new[] { "/b" }));
            Assert.That(_delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(9) }));
            Assert.That(responses[1].Status, Is.EqualTo(200));
            Assert.That(responses[1].Body!.Value.GetProperty("value")[0].GetString(), Is.EqualTo("retried"));
            Assert.That(client.ThrottledResponseCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task SendBatchAsync_ThrottledUntilRetriesRunOut_ReturnsTheFailureAsync()
    {
        var handler = BatchHandler(_ => Answer(new R("1", 429, """{"error":{"code":"TooManyRequests"}}""", 1)));
        using var client = CreateClient(handler, maxRetries: 2);

        var responses = await client.SendBatchAsync([new(HttpMethod.Get, "/a")], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(responses.Single().Status, Is.EqualTo(429));
            // Maximum Retries counts the initial attempt today, so two means two calls in all (#3023).
            Assert.That(handler.CallCount, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task SendBatchAsync_RequestWithNoAnswer_IsAFailureNotASuccessAsync()
    {
        var handler = BatchHandler(_ => Answer(new R("1", 204, null)));
        using var client = CreateClient(handler);

        var responses = await client.SendBatchAsync([new(HttpMethod.Delete, "/a"), new(HttpMethod.Delete, "/b")], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(responses[0].IsSuccess, Is.True);
            Assert.That(responses[1].IsSuccess, Is.False);
            Assert.That(responses[1].Status, Is.Zero);
            Assert.That(responses[1].Error, Is.Not.Empty);
        }
    }

    [Test]
    public async Task SendBatchAsync_Writes_ArePacedOnePerRequestAsync()
    {
        var handler = BatchHandler(_ => Answer(new R("1", 204, null), new R("2", 204, null), new R("3", 204, null)));
        using var client = CreateClient(handler);

        await client.SendBatchAsync([new(HttpMethod.Patch, "/a", new { }), new(HttpMethod.Get, "/b"), new(HttpMethod.Delete, "/c")], CancellationToken.None);
        await client.SendBatchAsync([new(HttpMethod.Patch, "/d", new { })], CancellationToken.None);

        // Two writes at ten a second: the next write waits for both slots, while the read in the first batch takes none.
        Assert.That(_delays, Is.EqualTo(new[] { TimeSpan.FromMilliseconds(200) }));
    }

    [Test]
    public void SendBatchAsync_MoreThanTwentyRequests_IsRefused()
    {
        using var client = CreateClient(BatchHandler(_ => Answer()));
        var requests = Enumerable.Range(0, 21).Select(i => new GraphBatchRequest(HttpMethod.Get, $"/x/{i}")).ToList();

        Assert.ThrowsAsync<ArgumentException>(async () => await client.SendBatchAsync(requests, CancellationToken.None));
    }

    private GraphHttpClient CreateClient(StubHttpMessageHandler handler, int maxRetries = 3)
    {
        Task Delay(TimeSpan delay, CancellationToken _)
        {
            _delays.Add(delay);
            return Task.CompletedTask;
        }

        var start = new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);
        return new GraphHttpClient(
            new HttpClient(handler),
            GraphCloud.Global,
            new StaticToken(),
            new HttpRetryPolicy(maxRetries, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), jitterFactor: 0),
            new GraphRequestPacer(10, () => start, Delay),
            _logger,
            delay: Delay,
            utcNow: () => start);
    }

    private static StubHttpMessageHandler BatchHandler(Func<int, string> answer) =>
        new((_, call) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(call), Encoding.UTF8, "application/json") }));

    private sealed record R(string Id, int Status, string? Body, int? RetryAfter = null);

    private static string Answer(params R[] responses) =>
        JsonSerializer.Serialize(new
        {
            responses = responses.Select(r => new Dictionary<string, object?>
            {
                ["id"] = r.Id,
                ["status"] = r.Status,
                ["headers"] = r.RetryAfter is { } seconds ? new Dictionary<string, string> { ["Retry-After"] = seconds.ToString() } : new Dictionary<string, string>(),
                ["body"] = r.Body == null ? null : JsonDocument.Parse(r.Body).RootElement
            })
        });

    private sealed class StaticToken : IHttpAuthenticationStrategy
    {
        public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token");
            return Task.CompletedTask;
        }

        public void InvalidateCachedCredentials()
        {
        }
    }
}
