// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.Scim;
using JIM.Scim.Messages;
using JIM.TestSupport;
using JIM.Web.Middleware.Api;
using JIM.Web.Scim;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Web.Api.Tests.Scim;

/// <summary>
/// The SCIM resource endpoints (RFC 7644 section 3; #124 Phase 4b) as a SCIM client meets them, through the controller
/// and the real store: status codes, <c>Location</c> and <c>ETag</c>, <c>If-Match</c>, filtering and search, attribute
/// selection, SCIM errors for bodies that are not JSON objects, and the journal attributing each write to the key and
/// address that made it (D17).
/// </summary>
/// <remarks>
/// Real PostgreSQL and a no-tracking context, as JIM.Web runs; opt-in via the <c>JIM_TEST_RESET_*</c> environment
/// variables like the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ScimResourcesControllerDatabaseTests
{
    private const string ClientAddress = "203.0.113.9";

    private string _connectionString = null!;
    private int _systemId;
    private ApiKey _key = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment(databaseSuffix: "_web_api");
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL SCIM endpoint tests.");
        _connectionString = connectionString!;
        PostgresTestDatabase.CreateDatabaseIfMissing(_connectionString);

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using (var ctx = NewContext())
        {
            var definition = new ConnectorDefinition { Name = $"scim-endpoint-def-{suffix}" };
            ctx.ConnectorDefinitions.Add(definition);
            await ctx.SaveChangesAsync();
            var system = new ConnectedSystem { Name = $"scim-endpoint-system-{suffix}", ConnectorDefinitionId = definition.Id };
            ctx.ConnectedSystems.Add(system);
            await ctx.SaveChangesAsync();
            _systemId = system.Id;
            _key = new ApiKey
            {
                Id = Guid.NewGuid(), Name = $"Entra provisioning {suffix}", KeyHash = $"hash-{suffix}", KeyPrefix = "jim_ak_test",
                Scope = ApiKeyScope.ScimServiceProvider, ScopeConnectedSystemId = system.Id, ExpiresAt = DateTime.UtcNow.AddDays(30)
            };
            ctx.ApiKeys.Add(_key);
            await ctx.SaveChangesAsync();
        }

        await WithJimAsync(jim => jim.Scim.EnsureCoreDefinitionsAsync(_systemId));
    }

    [Test]
    public async Task Create_User_201WithLocationETagAndTheJournalNamingTheKeyAsync()
    {
        var response = await SendAsync("POST", "Users", body: User("alice"));

        var id = response.Body!["id"]!.GetValue<string>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Status, Is.EqualTo(StatusCodes.Status201Created));
            Assert.That(response.Location, Is.EqualTo($"https://jim.example.com/scim/v2/{_systemId}/Users/{id}"));
            Assert.That(response.ETag, Is.EqualTo("W/\"1\""));
            Assert.That(response.Body["meta"]?["location"]?.GetValue<string>(), Is.EqualTo(response.Location));
        }

        await using var ctx = NewContext();
        var entry = await ctx.ScimStoreJournalEntries.SingleAsync(e => e.ConnectedSystemId == _systemId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.ApiKeyId, Is.EqualTo(_key.Id));
            Assert.That(entry.ApiKeyName, Is.EqualTo(_key.Name));
            Assert.That(entry.ClientAddress, Is.EqualTo(ClientAddress));
        }
    }

    [Test]
    public async Task Get_ExistingResource_200WithItsETagAsync()
    {
        var id = await CreateUserAsync("alice");

        var response = await SendAsync("GET", $"Users/{id}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(response.ETag, Is.EqualTo("W/\"1\""));
            Assert.That(response.Body!["userName"]?.GetValue<string>(), Is.EqualTo("alice"));
        }
    }

    [TestCase("00000000-0000-0000-0000-000000000001")]
    [TestCase("not-a-uuid")]
    public async Task Get_UnknownOrMalformedId_Scim404Async(string id)
    {
        var response = await SendAsync("GET", $"Users/{id}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Status, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(response.Body!["schemas"]?[0]?.GetValue<string>(), Is.EqualTo(ScimUrns.Error));
        }
    }

    [Test]
    public async Task Get_WithAttributes_ReturnsOnlyThoseAndTheAlwaysReturnedOnesAsync()
    {
        var id = await CreateUserAsync("alice");

        var response = await SendAsync("GET", $"Users/{id}", query: "?attributes=userName");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Body!["userName"], Is.Not.Null);
            Assert.That(response.Body["id"], Is.Not.Null);
            Assert.That(response.Body["displayName"], Is.Null);
        }
    }

    [Test]
    public async Task Replace_WithAStaleIfMatch_412AndNothingChangedAsync()
    {
        var id = await CreateUserAsync("alice");

        var response = await SendAsync("PUT", $"Users/{id}", body: User("alice", displayName: "Alice Changed"), ifMatch: "W/\"7\"");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Status, Is.EqualTo(StatusCodes.Status412PreconditionFailed));
            Assert.That((await SendAsync("GET", $"Users/{id}")).Body!["displayName"]?.GetValue<string>(), Is.EqualTo("alice"));
        }
    }

    [Test]
    public async Task Replace_WithTheCurrentIfMatch_200AndTheNextVersionAsync()
    {
        var id = await CreateUserAsync("alice");

        var response = await SendAsync("PUT", $"Users/{id}", body: User("alice", displayName: "Alice Changed"), ifMatch: "W/\"1\"");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(response.ETag, Is.EqualTo("W/\"2\""));
            Assert.That(response.Body!["displayName"]?.GetValue<string>(), Is.EqualTo("Alice Changed"));
        }
    }

    [Test]
    public async Task Patch_WithACapitalisedOp_AppliedAndReturnedAsync()
    {
        // Some identity providers capitalise the op; tolerated (see "Interoperability leniency").
        var id = await CreateUserAsync("alice");
        var patch = new JsonObject
        {
            ["schemas"] = new JsonArray(ScimUrns.PatchOp),
            ["Operations"] = new JsonArray(new JsonObject { ["op"] = "Replace", ["path"] = "displayName", ["value"] = "Alice Patched" })
        };

        var response = await SendAsync("PATCH", $"Users/{id}", body: patch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(response.Body!["displayName"]?.GetValue<string>(), Is.EqualTo("Alice Patched"));
            Assert.That(response.ETag, Is.EqualTo("W/\"2\""));
        }
    }

    [Test]
    public async Task Delete_204ThenGone404Async()
    {
        var id = await CreateUserAsync("alice");

        var deleted = await SendAsync("DELETE", $"Users/{id}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted.Status, Is.EqualTo(StatusCodes.Status204NoContent));
            Assert.That(deleted.Body, Is.Null);
            Assert.That((await SendAsync("GET", $"Users/{id}")).Status, Is.EqualTo(StatusCodes.Status404NotFound));
        }
    }

    [Test]
    public async Task List_FilteredByUserName_OnlyThatUserWithTheTotalAsync()
    {
        await CreateUserAsync("alice");
        await CreateUserAsync("bob");

        var response = await SendAsync("GET", "Users", query: "?filter=" + WebUtility.UrlEncode("userName eq \"bob\""));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(response.Body!["totalResults"]?.GetValue<int>(), Is.EqualTo(1));
            Assert.That(response.Body["Resources"]!.AsArray().Single()!["userName"]?.GetValue<string>(), Is.EqualTo("bob"));
        }
    }

    [Test]
    public async Task List_WithCountAndStartIndex_PagesAndOffersACursorAsync()
    {
        await CreateUserAsync("alice");
        await CreateUserAsync("bob");
        await CreateUserAsync("carol");

        var response = await SendAsync("GET", "Users", query: "?startIndex=1&count=2");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Body!["totalResults"]?.GetValue<int>(), Is.EqualTo(3));
            Assert.That(response.Body["Resources"]!.AsArray(), Has.Count.EqualTo(2));
            Assert.That(response.Body["nextCursor"], Is.Not.Null);
        }
    }

    [Test]
    public async Task List_ByCursor_WalksEveryResourceOnceAsync()
    {
        // RFC 9865: an empty cursor asks for the first page with cursor paging; each nextCursor fetches the next.
        await CreateUserAsync("alice");
        await CreateUserAsync("bob");
        await CreateUserAsync("carol");

        var first = await SendAsync("GET", "Users", query: "?cursor=&count=2");
        var next = first.Body!["nextCursor"]!.GetValue<string>();
        var second = await SendAsync("GET", "Users", query: "?count=2&cursor=" + WebUtility.UrlEncode(next));

        var userNames = first.Body["Resources"]!.AsArray().Concat(second.Body!["Resources"]!.AsArray())
            .Select(r => r!["userName"]!.GetValue<string>());
        Assert.That(userNames, Is.EquivalentTo(new[] { "alice", "bob", "carol" }));
    }

    [Test]
    public async Task Get_AcceptingOnlyApplicationJson_AnsweredAsApplicationJsonAsync()
    {
        var id = await CreateUserAsync("alice");

        var response = await SendAsync("GET", $"Users/{id}", accept: "application/json");

        Assert.That(response.ContentType, Does.StartWith("application/json"));
    }

    [Test]
    public async Task Search_PostedQuery_AnsweredAsTheSameListAsync()
    {
        await CreateUserAsync("alice");
        await CreateUserAsync("bob");
        var search = new JsonObject
        {
            ["schemas"] = new JsonArray(ScimUrns.SearchRequest),
            ["filter"] = "userName eq \"alice\""
        };

        var response = await SendAsync("POST", "Users/.search", body: search);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(response.Body!["totalResults"]?.GetValue<int>(), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Search_MemberOfTheWrongType_400InvalidValueAsync()
    {
        var search = new JsonObject { ["schemas"] = new JsonArray(ScimUrns.SearchRequest), ["count"] = "lots" };

        var response = await SendAsync("POST", "Users/.search", body: search);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
            Assert.That(response.Body!["scimType"]?.GetValue<string>(), Is.EqualTo(ScimErrorTypes.InvalidValue));
        }
    }

    [Test]
    public async Task Create_DuplicateUserName_409UniquenessAsync()
    {
        await CreateUserAsync("alice");

        var response = await SendAsync("POST", "Users", body: User("alice"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Status, Is.EqualTo(StatusCodes.Status409Conflict));
            Assert.That(response.Body!["scimType"]?.GetValue<string>(), Is.EqualTo(ScimErrorTypes.Uniqueness));
        }
    }

    [TestCase("{ not json")]
    [TestCase("[1, 2]")]
    [TestCase("")]
    public async Task Create_BodyNotAJsonObject_400InvalidSyntaxAsync(string rawBody)
    {
        var response = await SendAsync("POST", "Users", rawBody: rawBody);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Status, Is.EqualTo(StatusCodes.Status400BadRequest));
            Assert.That(response.Body!["scimType"]?.GetValue<string>(), Is.EqualTo(ScimErrorTypes.InvalidSyntax));
        }
    }

    [Test]
    public async Task Create_UnknownEndpoint_Scim404Async()
    {
        var response = await SendAsync("POST", "Devices", body: new JsonObject { ["schemas"] = new JsonArray("urn:example:Device") });

        Assert.That(response.Status, Is.EqualTo(StatusCodes.Status404NotFound));
    }

    #region helpers

    private sealed record Response(int Status, JsonNode? Body, string? ETag, string? Location, string? ContentType);

    private async Task<string> CreateUserAsync(string userName)
    {
        var response = await SendAsync("POST", "Users", body: User(userName));
        Assert.That(response.Status, Is.EqualTo(StatusCodes.Status201Created), response.Body?.ToJsonString());
        return response.Body!["id"]!.GetValue<string>();
    }

    private static JsonObject User(string userName, string? displayName = null) => new()
    {
        ["schemas"] = new JsonArray(ScimUrns.User),
        ["userName"] = userName,
        ["displayName"] = displayName ?? userName
    };

    /// <summary>
    /// Sends one request through the controller, as the router would: the path after the base URL selects the action
    /// and its route values.
    /// </summary>
    private async Task<Response> SendAsync(string method, string path, JsonNode? body = null, string? rawBody = null, string query = "",
        string? ifMatch = null, string? accept = null)
    {
        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx));

        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("jim.example.com");
        http.Request.Path = $"/scim/v2/{_systemId}/{path}";
        http.Request.QueryString = new QueryString(query.Length == 0 ? null : query);
        http.Request.RouteValues[ScimRoutes.ConnectedSystemIdRouteValue] = _systemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Connection.RemoteIpAddress = IPAddress.Parse(ClientAddress);
        http.User = new ClaimsPrincipal(new ClaimsIdentity(ApiKeyAuthenticationHandler.BuildApiKeyClaims(_key), ApiKeyAuthenticationHandler.SchemeName));
        if (ifMatch != null)
            http.Request.Headers.IfMatch = ifMatch;
        if (accept != null)
            http.Request.Headers.Accept = accept;
        var text = rawBody ?? body?.ToJsonString();
        if (text != null)
        {
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(text));
            http.Request.ContentType = ScimResponses.ContentType;
        }

        var controller = new ScimResourcesController(jim) { ControllerContext = new ControllerContext { HttpContext = http } };
        var segments = path.Split('/');
        var endpoint = segments[0];
        var id = segments.Length > 1 ? segments[1] : null;
        IActionResult result = (method, id) switch
        {
            ("POST", ".search") => await controller.SearchAsync(endpoint),
            ("POST", null) => await controller.CreateAsync(endpoint),
            ("GET", null) => await controller.ListAsync(endpoint),
            ("GET", { } resourceId) => await controller.GetAsync(endpoint, resourceId),
            ("PUT", { } resourceId) => await controller.ReplaceAsync(endpoint, resourceId),
            ("PATCH", { } resourceId) => await controller.PatchAsync(endpoint, resourceId),
            ("DELETE", { } resourceId) => await controller.DeleteAsync(endpoint, resourceId),
            _ => throw new ArgumentException($"No action for {method} {path}.")
        };

        var content = result as ContentResult;
        var status = content?.StatusCode ?? (result as IStatusCodeActionResult)?.StatusCode ?? 0;
        return new Response(status, string.IsNullOrEmpty(content?.Content) ? null : JsonNode.Parse(content.Content),
            http.Response.Headers.ETag.ToString() is { Length: > 0 } etag ? etag : null,
            http.Response.Headers.Location.ToString() is { Length: > 0 } location ? location : null,
            content?.ContentType);
    }

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    private async Task WithJimAsync<T>(Func<JimApplication, Task<T>> call)
    {
        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        await call(jim);
    }

    #endregion
}
