// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The simulated tenant's own behaviour as the stand-in Microsoft Graph service serves it to Scenario 040: it records
/// its writes for delta rounds, as Graph does, pages consistently while objects are removed, and answers a tenant of
/// a million objects without scanning it.
/// </summary>
[TestFixture]
public class MockGraphTenantTests
{
    private MockGraphTenant _tenant = null!;

    [SetUp]
    public void SetUp()
    {
        _tenant = new MockGraphTenant { RecordsWrites = true };
    }

    [Test]
    public void Respond_WritesWhenRecordingWrites_DeltaRoundReportsEachWrittenObject()
    {
        var baseline = DeltaLink("users");
        var groupsBaseline = DeltaLink("groups");

        var user = (string)Send(HttpMethod.Post, "users", NewUser("ada"))["id"]!;
        var group = (string)Send(HttpMethod.Post, "groups", new JsonObject { ["displayName"] = "Engineers" })["id"]!;
        Send(HttpMethod.Post, $"groups/{group}/members/$ref", new JsonObject { ["@odata.id"] = $"https://graph.microsoft.com/v1.0/directoryObjects/{user}" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RoundIds(baseline), Is.EqualTo(new[] { user }));
            Assert.That(RoundIds(groupsBaseline), Is.EqualTo(new[] { group, group }), "Created, then a member added.");
        }
    }

    [Test]
    public void Respond_DeleteWhenRecordingWrites_RemovesTheUserFromItsGroupsAndItsReportsManager()
    {
        var manager = (string)Send(HttpMethod.Post, "users", NewUser("grace"))["id"]!;
        var report = (string)Send(HttpMethod.Post, "users", NewUser("ada"))["id"]!;
        var group = (string)Send(HttpMethod.Post, "groups", new JsonObject { ["displayName"] = "Engineers" })["id"]!;
        Send(HttpMethod.Post, $"groups/{group}/members/$ref", new JsonObject { ["@odata.id"] = $"https://graph.microsoft.com/v1.0/directoryObjects/{manager}" });
        Send(HttpMethod.Put, $"users/{report}/manager/$ref", new JsonObject { ["@odata.id"] = $"https://graph.microsoft.com/v1.0/users/{manager}" });
        var usersBaseline = DeltaLink("users");
        var groupsBaseline = DeltaLink("groups");

        Send(HttpMethod.Delete, $"users/{manager}", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_tenant.Members[group], Is.Empty);
            Assert.That(_tenant.User(report)!["managerId"], Is.Null);
            Assert.That(RoundIds(usersBaseline), Is.EquivalentTo(new[] { report, manager }));
            Assert.That(RoundIds(groupsBaseline), Is.EqualTo(new[] { group }));
        }
    }

    [Test]
    public void Respond_PagingWhileObjectsAreRemoved_ReturnsEveryRemainingObjectOnce()
    {
        for (var i = 0; i < 250; i++)
            _tenant.AddUser($"u{i}", $"User {i}");

        var first = Send(HttpMethod.Get, "users?$top=100", null);
        var seen = first["value"]!.AsArray().Select(u => (string)u!["id"]!).ToList();

        // Removed during the read: one already read, and two not yet.
        _tenant.RecordRemoval("users", "u5");
        _tenant.RecordRemoval("users", "u150");
        _tenant.RecordRemoval("users", "u249");

        var next = (string?)first["@odata.nextLink"];
        while (next != null)
        {
            var page = Send(HttpMethod.Get, next["https://graph.microsoft.com/v1.0/".Length..], null);
            seen.AddRange(page["value"]!.AsArray().Select(u => (string)u!["id"]!));
            next = (string?)page["@odata.nextLink"];
        }

        Assert.That(seen, Is.EquivalentTo(Enumerable.Range(0, 249).Where(i => i != 150).Select(i => $"u{i}")));
    }

    [Test]
    public void Respond_CountOfUsersAndOfMembers_AnswersTheNumberHeld()
    {
        _tenant.AddUser("u1", "Ada");
        _tenant.AddUser("u2", "Grace");
        _tenant.AddGroup("g1", "Engineers");
        _tenant.Members["g1"] = [("user", "u1")];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SendForText(HttpMethod.Get, "users/$count"), Is.EqualTo("2"));
            Assert.That(SendForText(HttpMethod.Get, "groups/$count"), Is.EqualTo("1"));
            Assert.That(SendForText(HttpMethod.Get, "groups/g1/members/$count"), Is.EqualTo("1"));
        }
    }

    [Test]
    public void Respond_UserByPrincipalNameAndTheirManager_AnswersAsGraphDoes()
    {
        var manager = (string)Send(HttpMethod.Post, "users", NewUser("grace"))["id"]!;
        var report = (string)Send(HttpMethod.Post, "users", NewUser("ada"))["id"]!;
        Send(HttpMethod.Put, $"users/{report}/manager/$ref", new JsonObject { ["@odata.id"] = $"https://graph.microsoft.com/v1.0/users/{manager}" });

        var byName = Send(HttpMethod.Get, "users/ada@contoso.com?$select=displayName", null);
        var managerRead = Send(HttpMethod.Get, $"users/{report}/manager?$select=displayName", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((string?)byName["id"], Is.EqualTo(report));
            Assert.That((string?)byName["displayName"], Is.EqualTo("ada"));
            Assert.That((string?)managerRead["id"], Is.EqualTo(manager));
            Assert.That((string?)managerRead["displayName"], Is.EqualTo("grace"));
        }
    }

    [Test]
    public void Respond_FilteredAndCountedLists_AnswerOnlyTheMatchingObjects()
    {
        _tenant.AddUser("u1", "run1-Ada", u => u["mailNickname"] = "ada");
        _tenant.AddUser("u2", "run1-Grace");
        _tenant.AddUser("u3", "other-Alan");
        _tenant.AddGroup("g1", "run1-All Staff", g => g["mailNickname"] = "allstaff");
        _tenant.Members["g1"] = [("user", "u1"), ("user", "u2")];

        var prefixed = Send(HttpMethod.Get, "users?$filter=startswith(displayName,'run1-')&$count=true&$top=1&$select=id", null);
        var named = Send(HttpMethod.Get, "groups?$filter=mailNickname eq 'allstaff'&$select=id", null);
        var members = Send(HttpMethod.Get, "groups/g1/members?$count=true&$top=1&$select=id", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)prefixed["@odata.count"], Is.EqualTo(2));
            Assert.That(prefixed["value"]!.AsArray(), Has.Count.EqualTo(1));
            Assert.That(named["value"]!.AsArray().Select(g => (string)g!["id"]!), Is.EqualTo(new[] { "g1" }));
            Assert.That((int?)members["@odata.count"], Is.EqualTo(2));
        }
    }

    [Test]
    public void Respond_DirectoryExtensionDiscovery_AnswersNoExtensions()
    {
        var answer = Send(HttpMethod.Post, "directoryObjects/getAvailableExtensionProperties", []);

        Assert.That(answer["value"]!.AsArray(), Is.Empty);
    }

    [Test]
    public void Respond_OutsideGraphVersionOne_AnswersNotFound()
    {
        using var response = _tenant.Respond(new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/beta/users"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public void Respond_ProvisioningAHundredThousandUsersIntoOneGroup_TakesSecondsNotHours()
    {
        // Every create checks its user principal name is free and every member add that the member is not already
        // held; scanning for either makes provisioning quadratic, which at a million objects never finishes.
        var group = (string)Send(HttpMethod.Post, "groups", new JsonObject { ["displayName"] = "All Staff" })["id"]!;
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 100_000; i++)
        {
            var user = (string)Send(HttpMethod.Post, "users", NewUser($"user{i}"))["id"]!;
            Send(HttpMethod.Post, $"groups/{group}/members/$ref", new JsonObject { ["@odata.id"] = $"https://graph.microsoft.com/v1.0/directoryObjects/{user}" });
        }

        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(60)));
        Assert.That(_tenant.Members[group], Has.Count.EqualTo(100_000));
    }

    private static JsonObject NewUser(string name) => new()
    {
        ["displayName"] = name,
        ["userPrincipalName"] = $"{name}@contoso.com",
        ["passwordProfile"] = new JsonObject { ["password"] = "not-a-real-password" }
    };

    private string DeltaLink(string collection)
    {
        var link = (string)Send(HttpMethod.Get, $"{collection}/delta?$deltatoken=latest", null)["@odata.deltaLink"]!;
        return link["https://graph.microsoft.com/v1.0/".Length..];
    }

    private List<string> RoundIds(string link)
    {
        var ids = new List<string>();
        string? next = link;
        while (next != null)
        {
            var page = Send(HttpMethod.Get, next, null);
            ids.AddRange(page["value"]!.AsArray().Select(e => (string)e!["id"]!));
            next = ((string?)page["@odata.nextLink"])?["https://graph.microsoft.com/v1.0/".Length..];
        }

        return ids;
    }

    private JsonObject Send(HttpMethod method, string path, JsonObject? body)
    {
        var text = SendForText(method, path, body);
        return string.IsNullOrEmpty(text) ? [] : JsonNode.Parse(text)!.AsObject();
    }

    private string SendForText(HttpMethod method, string path, JsonObject? body = null)
    {
        using var request = new HttpRequestMessage(method, $"https://graph.microsoft.com/v1.0/{path}");
        if (body != null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = _tenant.Respond(request);
        Assert.That((int)response.StatusCode, Is.LessThan(400), $"{method} {path}");
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }
}
