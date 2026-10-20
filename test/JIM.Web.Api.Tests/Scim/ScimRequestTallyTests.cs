// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Models.Security;
using JIM.Web.Middleware.Api;
using JIM.Web.Scim;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NUnit.Framework;

namespace JIM.Web.Api.Tests.Scim;

/// <summary>
/// How SCIM requests are counted for the summary Activity (#124, D17): which outcomes count as a create, an update, a
/// delete or a rejection; how the buffer gathers them per Connected System, key and 15-minute window until a flush
/// drains it; and the result filter that feeds it from every SCIM response.
/// </summary>
[TestFixture]
public class ScimRequestTallyTests
{
    private static readonly DateTimeOffset NineSixteen = new(2026, 10, 20, 9, 16, 0, TimeSpan.Zero);
    private static readonly Guid KeyId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [TestCase("POST", 201, ScimRequestOutcome.Created)]
    [TestCase("PUT", 200, ScimRequestOutcome.Updated)]
    [TestCase("PATCH", 200, ScimRequestOutcome.Updated)]
    [TestCase("PATCH", 204, ScimRequestOutcome.Updated)]
    [TestCase("DELETE", 204, ScimRequestOutcome.Deleted)]
    [TestCase("GET", 200, ScimRequestOutcome.NotCounted)]
    [TestCase("POST", 200, ScimRequestOutcome.NotCounted)] // a search
    [TestCase("POST", 400, ScimRequestOutcome.Rejected)]
    [TestCase("GET", 404, ScimRequestOutcome.Rejected)]
    [TestCase("POST", 409, ScimRequestOutcome.Rejected)]
    [TestCase("PATCH", 412, ScimRequestOutcome.Rejected)]
    [TestCase("GET", 401, ScimRequestOutcome.NotCounted)] // the security audit's
    [TestCase("GET", 403, ScimRequestOutcome.NotCounted)]
    [TestCase("POST", 500, ScimRequestOutcome.NotCounted)]
    public void Classify_StatusAndMethod_Outcome(string method, int status, ScimRequestOutcome expected)
    {
        Assert.That(ScimRequestOutcomes.Classify(method, status), Is.EqualTo(expected));
    }

    [Test]
    public void Drain_CountsGatheredPerConnectedSystemKeyAndWindow()
    {
        var clock = new ManualTimeProvider(NineSixteen);
        var buffer = new ScimRequestTallyBuffer(clock);

        buffer.Record(7, KeyId, "Entra key", ScimRequestOutcome.Created, null);
        clock.Advance(TimeSpan.FromMinutes(1));
        buffer.Record(7, KeyId, "Entra key", ScimRequestOutcome.Created, null);
        buffer.Record(7, KeyId, "Entra key", ScimRequestOutcome.Updated, null);
        buffer.Record(7, KeyId, "Entra key", ScimRequestOutcome.Rejected, "409 uniqueness on POST /Users");
        buffer.Record(8, KeyId, "Entra key", ScimRequestOutcome.Deleted, null);
        clock.Advance(TimeSpan.FromMinutes(14)); // 9:31, the next window
        buffer.Record(7, KeyId, "Entra key", ScimRequestOutcome.Deleted, null);

        var tallies = buffer.Drain().OrderBy(t => t.WindowStart).ThenBy(t => t.ConnectedSystemId).ToList();

        Assert.That(tallies, Has.Count.EqualTo(3));
        var first = tallies[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That((first.ConnectedSystemId, first.ApiKeyId, first.ApiKeyName), Is.EqualTo((7, KeyId, "Entra key")));
            Assert.That(first.WindowStart, Is.EqualTo(new DateTime(2026, 10, 20, 9, 15, 0, DateTimeKind.Utc)));
            Assert.That((first.Created, first.Updated, first.Deleted, first.Rejected), Is.EqualTo((2, 1, 0, 1)));
            Assert.That(first.FirstSeen, Is.EqualTo(NineSixteen.UtcDateTime));
            Assert.That(first.LastSeen, Is.EqualTo(NineSixteen.UtcDateTime.AddMinutes(1)));
            Assert.That(first.LatestRejection, Is.EqualTo("409 uniqueness on POST /Users"));
            Assert.That(tallies[1].ConnectedSystemId, Is.EqualTo(8));
            Assert.That(tallies[2].WindowStart, Is.EqualTo(new DateTime(2026, 10, 20, 9, 30, 0, DateTimeKind.Utc)));
        }
    }

    [Test]
    public void Drain_EmptiesTheBufferSoNothingIsFlushedTwice()
    {
        var buffer = new ScimRequestTallyBuffer(new ManualTimeProvider(NineSixteen));
        buffer.Record(7, KeyId, "Entra key", ScimRequestOutcome.Created, null);

        var first = buffer.Drain();
        var second = buffer.Drain();

        Assert.That((first.Count, second.Count), Is.EqualTo((1, 0)));
    }

    [Test]
    public void Record_NotCounted_RecordsNothing()
    {
        var buffer = new ScimRequestTallyBuffer(new ManualTimeProvider(NineSixteen));

        buffer.Record(7, KeyId, "Entra key", ScimRequestOutcome.NotCounted, null);

        Assert.That(buffer.Drain(), Is.Empty);
    }

    [Test]
    public void Record_ConcurrentRequestsAndDrains_EveryRequestCountedOnce()
    {
        const int requests = 20_000;
        var buffer = new ScimRequestTallyBuffer(new ManualTimeProvider(NineSixteen));
        var drained = new List<JIM.Models.Scim.ScimRequestTally>();

        var drainer = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
                lock (drained)
                    drained.AddRange(buffer.Drain());
        });
        Parallel.For(0, requests, _ => buffer.Record(7, KeyId, "Entra key", ScimRequestOutcome.Created, null));
        drainer.Wait();
        drained.AddRange(buffer.Drain());

        Assert.That(drained.Sum(t => t.Created), Is.EqualTo(requests));
    }

    [Test]
    public async Task Filter_RejectedWrite_RecordedWithItsKeyStatusTypeAndEndpointAsync()
    {
        var buffer = new ScimRequestTallyBuffer(new ManualTimeProvider(NineSixteen));
        var context = Context("POST", StatusCodes.Status409Conflict, endpoint: "Users", scimType: "uniqueness");

        await new ScimRequestTallyFilter(buffer).OnResultExecutionAsync(context, () => Executed(context));

        var tally = buffer.Drain().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((tally.ConnectedSystemId, tally.ApiKeyId, tally.ApiKeyName), Is.EqualTo((7, KeyId, "Entra key")));
            Assert.That(tally.Rejected, Is.EqualTo(1));
            Assert.That(tally.LatestRejection, Is.EqualTo("409 uniqueness on POST /Users"));
        }
    }

    [Test]
    public async Task Filter_AcceptedCreate_CountedAsync()
    {
        var buffer = new ScimRequestTallyBuffer(new ManualTimeProvider(NineSixteen));
        var context = Context("POST", StatusCodes.Status201Created, endpoint: "Users");

        await new ScimRequestTallyFilter(buffer).OnResultExecutionAsync(context, () => Executed(context));

        Assert.That(buffer.Drain().Single().Created, Is.EqualTo(1));
    }

    [Test]
    public async Task Filter_NoScopedKey_RecordsNothingAsync()
    {
        var buffer = new ScimRequestTallyBuffer(new ManualTimeProvider(NineSixteen));
        var context = Context("POST", StatusCodes.Status201Created, endpoint: "Users", authenticated: false);

        await new ScimRequestTallyFilter(buffer).OnResultExecutionAsync(context, () => Executed(context));

        Assert.That(buffer.Drain(), Is.Empty);
    }

    [Test]
    public async Task Filter_UnscopedKey_RecordsNothingAsync()
    {
        var buffer = new ScimRequestTallyBuffer(new ManualTimeProvider(NineSixteen));
        var context = Context("POST", StatusCodes.Status201Created, endpoint: "Users", scoped: false);

        await new ScimRequestTallyFilter(buffer).OnResultExecutionAsync(context, () => Executed(context));

        Assert.That(buffer.Drain(), Is.Empty);
    }

    private static ResultExecutingContext Context(string method, int status, string endpoint, string? scimType = null, bool authenticated = true,
        bool scoped = true)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Response.StatusCode = status;
        if (scimType != null)
            http.Items[ScimRequestOutcomes.ErrorTypeItem] = scimType;
        if (authenticated)
        {
            var key = new ApiKey
            {
                Id = KeyId, Name = "Entra key", KeyPrefix = "jim_ak_test",
                Scope = scoped ? ApiKeyScope.ScimServiceProvider : ApiKeyScope.None, ScopeConnectedSystemId = scoped ? 7 : null
            };
            http.User = new ClaimsPrincipal(new ClaimsIdentity(ApiKeyAuthenticationHandler.BuildApiKeyClaims(key), ApiKeyAuthenticationHandler.SchemeName));
        }

        var routeData = new RouteData();
        routeData.Values[ScimRoutes.ConnectedSystemIdRouteValue] = "7";
        routeData.Values["endpoint"] = endpoint;
        http.Request.RouteValues = routeData.Values;
        var actionContext = new ActionContext(http, routeData, new ActionDescriptor());
        return new ResultExecutingContext(actionContext, new List<IFilterMetadata>(), new StatusCodeResult(status), controller: new object());
    }

    private static Task<ResultExecutedContext> Executed(ResultExecutingContext context) =>
        Task.FromResult(new ResultExecutedContext(context, context.Filters, context.Result, context.Controller));

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
