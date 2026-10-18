// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Scim;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.Scim;
using JIM.Scim.Messages;
using JIM.Scim.Resources;
using JIM.Scim.Service;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The SCIM server (#124, Phase 2b): every write an identity provider can make, end to end through
/// <see cref="JimApplication"/> into the real store, with the statuses and bodies the HTTP surface (Phase 4) will return.
/// Intake and PATCH are the Phase 1 primitives; what is proved here is how the server puts them together: uniqueness as
/// <c>409</c>, <c>If-Match</c> as <c>412</c>, strict references (D9), versions, and a journal entry for every accepted
/// write (D17).
/// </summary>
/// <remarks>
/// Real PostgreSQL and a no-tracking context, as JIM.Web runs; opt-in via the <c>JIM_TEST_RESET_*</c> environment
/// variables like the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ScimServerDatabaseTests
{
    private const string BaseUrl = "https://jim.example/scim/v2/7";
    private const string Users = "/Users";
    private const string Groups = "/Groups";
    private const string ClientAddress = "203.0.113.9";

    private string _connectionString = null!;
    private int _systemId;
    private ApiKey _key = null!;

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL SCIM server tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);

        await using var ctx = NewContext();
        var definition = new ConnectorDefinition { Name = "scim-server-tests" };
        ctx.ConnectorDefinitions.Add(definition);
        await ctx.SaveChangesAsync();
        var system = new ConnectedSystem { Name = "SCIM source", ConnectorDefinitionId = definition.Id };
        ctx.ConnectedSystems.Add(system);
        _key = new ApiKey { Id = Guid.NewGuid(), Name = "Entra provisioning", KeyHash = "hash", KeyPrefix = "jim_ak_test" };
        ctx.ApiKeys.Add(_key);
        await ctx.SaveChangesAsync();
        _systemId = system.Id;

        await WithServerAsync(server => server.EnsureCoreDefinitionsAsync(_systemId));
    }

    // ─── Definitions ───

    [Test]
    public async Task EnsureCoreDefinitionsAsync_SeedsOnceAsync()
    {
        var seededAgain = await WithServerAsync(server => server.EnsureCoreDefinitionsAsync(_systemId));

        await using var ctx = NewContext();
        var store = new PostgresDataRepository(ctx).ScimStore;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(seededAgain, Is.False, "already seeded by SetUp");
            Assert.That((await store.GetResourceTypesAsync(_systemId)).Select(t => t.Name), Is.EquivalentTo(new[] { "User", "Group" }));
            Assert.That((await store.GetSchemasAsync(_systemId)).Select(s => s.Urn),
                Is.EquivalentTo(new[] { ScimUrns.User, ScimUrns.Group, ScimUrns.EnterpriseUser }));
        }
    }

    // ─── Create ───

    [Test]
    public async Task CreateAsync_User_Returns201WithIdMetaAndVersionOneAsync()
    {
        var result = await CreateAsync(Users, UserBody("Ada.Lovelace", externalId: "E-1"));

        var id = result.Resource!["id"]!.GetValue<string>();
        var meta = result.Resource["meta"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(201));
            Assert.That(Guid.TryParse(id, out _), Is.True);
            Assert.That(result.Version, Is.EqualTo(1));
            Assert.That(result.ETag, Is.EqualTo("W/\"1\""));
            Assert.That(result.Location, Is.EqualTo($"{BaseUrl}/Users/{id}"));
            Assert.That(meta["resourceType"]!.GetValue<string>(), Is.EqualTo("User"));
            Assert.That(meta["location"]!.GetValue<string>(), Is.EqualTo(result.Location));
            Assert.That(meta["version"]!.GetValue<string>(), Is.EqualTo("W/\"1\""));
            Assert.That(meta["created"]!.GetValue<string>(), Is.EqualTo(meta["lastModified"]!.GetValue<string>()));
            Assert.That(result.Resource["userName"]!.GetValue<string>(), Is.EqualTo("Ada.Lovelace"));
        }
    }

    [Test]
    public async Task CreateAsync_UnknownEndpoint_Returns404Async()
    {
        var result = await CreateAsync("/Devices", new JsonObject { ["name"] = "x" });

        Assert.That(result.Error?.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task CreateAsync_UnknownConnectedSystem_Returns404Async()
    {
        var result = await WithServerAsync(server => server.CreateAsync(Context(_systemId + 1000), Users, UserBody("ada")));

        Assert.That(result.Error?.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task CreateAsync_WithPassword_IsRefusedAndNothingIsStoredAsync()
    {
        var body = UserBody("ada");
        body["password"] = "S3cret-Value!";

        var result = await CreateAsync(Users, body);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error?.StatusCode, Is.EqualTo(400));
            Assert.That(result.Error?.Detail, Does.Not.Contain("S3cret"));
            Assert.That(await CountAsync("ScimStoreResources"), Is.Zero);
            Assert.That(await CountAsync("ScimStoreJournalEntries"), Is.Zero);
        }
    }

    [Test]
    public async Task CreateAsync_MissingRequiredAttribute_Returns400Async()
    {
        var result = await CreateAsync(Users, new JsonObject { ["displayName"] = "Ada" });

        Assert.That(result.Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidValue));
    }

    [Test]
    public async Task CreateAsync_UserNameHeldInAnotherCase_Returns409NamingTheAttributeAsync()
    {
        await CreateAsync(Users, UserBody("Ada.Lovelace"));

        var result = await CreateAsync(Users, UserBody("ada.lovelace"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error?.StatusCode, Is.EqualTo(409));
            Assert.That(result.Error?.ScimType, Is.EqualTo(ScimErrorTypes.Uniqueness));
            Assert.That(result.Error?.Detail, Is.EqualTo("The attribute 'userName' must be unique."));
            Assert.That(await CountAsync("ScimStoreResources"), Is.EqualTo(1));
            Assert.That(await CountAsync("ScimStoreJournalEntries"), Is.EqualTo(1), "a refused write is not journalled");
        }
    }

    [Test]
    public async Task CreateAsync_ExternalIdHeld_Returns409Async()
    {
        await CreateAsync(Users, UserBody("ada", externalId: "E-1"));

        var result = await CreateAsync(Users, UserBody("grace", externalId: "E-1"));

        Assert.That(result.Error?.Detail, Is.EqualTo("The attribute 'externalId' must be unique."));
    }

    [Test]
    public async Task CreateAsync_GroupWithMembers_HoldsThemAsReferencesAndReturnsThemAsync()
    {
        var ada = await CreateUserAsync("ada");
        var staff = await CreateGroupAsync("Staff");

        var result = await CreateAsync(Groups, GroupBody("Admins", ada, staff));

        var members = result.Resource!["members"]!.AsArray().Select(m => m!.AsObject()).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(201));
            Assert.That(members.Select(m => (m["value"]!.GetValue<string>(), m["type"]!.GetValue<string>(), m["$ref"]!.GetValue<string>())),
                Is.EquivalentTo(new[]
                {
                    (ada.ToString(), "User", $"{BaseUrl}/Users/{ada}"),
                    (staff.ToString(), "Group", $"{BaseUrl}/Groups/{staff}")
                }));
            Assert.That(await StoredPayloadAsync(Id(result)), Does.Not.Contain("members"), "members are rows, not payload");
        }
    }

    [Test]
    public async Task CreateAsync_MemberThatIsNotALiveResource_Returns400AndStoresNothingAsync()
    {
        var result = await CreateAsync(Groups, GroupBody("Admins", Guid.NewGuid()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error?.StatusCode, Is.EqualTo(400));
            Assert.That(result.Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidValue));
            Assert.That(await CountAsync("ScimStoreResources"), Is.Zero);
        }
    }

    [Test]
    public async Task CreateAsync_ManagerOfTheWrongType_Returns400Async()
    {
        var staff = await CreateGroupAsync("Staff");
        var body = UserBody("ada");
        body[ScimUrns.EnterpriseUser] = new JsonObject { ["manager"] = new JsonObject { ["value"] = staff.ToString() } };

        var result = await CreateAsync(Users, body);

        Assert.That(result.Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidValue), "a manager must be a User (D9)");
    }

    [Test]
    public async Task CreateAsync_UserWithManager_HoldsTheManagerAsAReferenceAsync()
    {
        var grace = await CreateUserAsync("grace");
        var body = UserBody("ada");
        body[ScimUrns.EnterpriseUser] = new JsonObject { ["department"] = "R&D", ["manager"] = new JsonObject { ["value"] = grace.ToString() } };

        var result = await CreateAsync(Users, body);

        var extension = result.Resource![ScimUrns.EnterpriseUser]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(extension["department"]!.GetValue<string>(), Is.EqualTo("R&D"));
            Assert.That(extension["manager"]!["value"]!.GetValue<string>(), Is.EqualTo(grace.ToString()));
            Assert.That(await ReferenceCountAsync(Id(result)), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task CreateAsync_IsJournalledWithTheActorAndTheAttributesSetAsync()
    {
        var result = await CreateAsync(Users, UserBody("ada", externalId: "E-1"));

        var entry = (await JournalAsync()).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.ResourceId, Is.EqualTo(Id(result)));
            Assert.That(entry.ResourceTypeName, Is.EqualTo("User"));
            Assert.That(entry.Operation, Is.EqualTo(ScimStoreOperation.Created));
            Assert.That(entry.ChangedAttributes, Is.EquivalentTo(new[] { "userName", "externalId", "displayName" }));
            Assert.That(entry.ApiKeyId, Is.EqualTo(_key.Id));
            Assert.That(entry.ApiKeyName, Is.EqualTo(_key.Name));
            Assert.That(entry.ClientAddress, Is.EqualTo(ClientAddress));
        }
    }

    // ─── Get ───

    [Test]
    public async Task GetAsync_ReturnsTheResourceWithItsVersionAsync()
    {
        var ada = await CreateUserAsync("ada");
        var admins = await CreateGroupAsync("Admins", ada);

        var result = await WithServerAsync(server => server.GetAsync(Context(), Groups, admins.ToString()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(200));
            Assert.That(result.Version, Is.EqualTo(1));
            Assert.That(result.Resource!["displayName"]!.GetValue<string>(), Is.EqualTo("Admins"));
            Assert.That(result.Resource["members"]!.AsArray().Single()!["value"]!.GetValue<string>(), Is.EqualTo(ada.ToString()));
        }
    }

    [Test]
    public async Task GetAsync_ExcludedMembers_LeavesThemOutAsync()
    {
        var ada = await CreateUserAsync("ada");
        var admins = await CreateGroupAsync("Admins", ada);

        var result = await WithServerAsync(server => server.GetAsync(Context(), Groups, admins.ToString(),
            new ScimAttributeSelection(ExcludedAttributes: ["members"])));

        Assert.That(result.Resource!.ContainsKey("members"), Is.False);
    }

    [TestCase("not-a-guid")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    public async Task GetAsync_NoSuchResource_Returns404Async(string id)
    {
        var result = await WithServerAsync(server => server.GetAsync(Context(), Users, id));

        Assert.That(result.Error?.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task GetAsync_ThroughAnotherTypesEndpoint_Returns404Async()
    {
        var ada = await CreateUserAsync("ada");

        var result = await WithServerAsync(server => server.GetAsync(Context(), Groups, ada.ToString()));

        Assert.That(result.Error?.StatusCode, Is.EqualTo(404));
    }

    // ─── Replace ───

    [Test]
    public async Task ReplaceAsync_RaisesTheVersionAndJournalsOnlyWhatChangedAsync()
    {
        var created = await CreateAsync(Users, UserBody("ada", externalId: "E-1"));
        var id = Id(created);
        var body = UserBody("ada", externalId: "E-1");
        body["displayName"] = "Ada, Countess of Lovelace";

        var result = await WithServerAsync(server => server.ReplaceAsync(Context(), Users, id.ToString(), body, ifMatch: null));

        var entry = (await JournalAsync()).Last();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(200));
            Assert.That(result.Version, Is.EqualTo(2));
            Assert.That(result.Resource!["meta"]!["created"]!.GetValue<string>(),
                Is.EqualTo(created.Resource!["meta"]!["created"]!.GetValue<string>()), "creation time is kept");
            Assert.That(entry.Operation, Is.EqualTo(ScimStoreOperation.Replaced));
            Assert.That(entry.ChangedAttributes, Is.EqualTo(new[] { "displayName" }));
        }
    }

    [Test]
    public async Task ReplaceAsync_NothingChanged_WritesNothingAsync()
    {
        var id = Id(await CreateAsync(Users, UserBody("ada")));

        var result = await WithServerAsync(server => server.ReplaceAsync(Context(), Users, id.ToString(), UserBody("ada"), ifMatch: null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(200));
            Assert.That(result.Version, Is.EqualTo(1), "an unchanged resource keeps its version, so imports see no change");
            Assert.That(await CountAsync("ScimStoreJournalEntries"), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ReplaceAsync_SameMembersInAnotherOrder_WritesNothingAsync()
    {
        var ada = await CreateUserAsync("ada");
        var grace = await CreateUserAsync("grace");
        var admins = await CreateGroupAsync("Admins", ada, grace);

        // Both orders, so one of them differs from whatever order the store reads members back in.
        var oneOrder = await WithServerAsync(server => server.ReplaceAsync(Context(), Groups, admins.ToString(), GroupBody("Admins", grace, ada), ifMatch: null));
        var otherOrder = await WithServerAsync(server => server.ReplaceAsync(Context(), Groups, admins.ToString(), GroupBody("Admins", ada, grace), ifMatch: null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(new[] { oneOrder.Version, otherOrder.Version }, Is.All.EqualTo(1), "membership is a set: order is not a change");
            Assert.That(await CountAsync("ScimStoreJournalEntries"), Is.EqualTo(3), "the three creates only");
        }
    }

    [TestCase("W/\"1\"", 200)]
    [TestCase("*", 200)]
    [TestCase("W/\"7\"", 412)]
    [TestCase("garbage", 412)]
    public async Task ReplaceAsync_IfMatch_IsHonouredAsync(string ifMatch, int expectedStatus)
    {
        var id = Id(await CreateAsync(Users, UserBody("ada")));
        var body = UserBody("ada");
        body["title"] = "Analyst";

        var result = await WithServerAsync(server => server.ReplaceAsync(Context(), Users, id.ToString(), body, ifMatch));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error?.StatusCode ?? result.StatusCode, Is.EqualTo(expectedStatus));
            Assert.That(await CountAsync("ScimStoreJournalEntries"), Is.EqualTo(expectedStatus == 200 ? 2 : 1));
        }
    }

    [Test]
    public async Task ReplaceAsync_TakingAHeldUserName_Returns409AndChangesNothingAsync()
    {
        await CreateUserAsync("grace");
        var id = Id(await CreateAsync(Users, UserBody("ada")));

        var result = await WithServerAsync(server => server.ReplaceAsync(Context(), Users, id.ToString(), UserBody("GRACE"), ifMatch: null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error?.StatusCode, Is.EqualTo(409));
            Assert.That(await StoredPayloadAsync(id), Does.Contain("ada"));
        }
    }

    [Test]
    public async Task ReplaceAsync_NoSuchResource_Returns404Async()
    {
        var result = await WithServerAsync(server => server.ReplaceAsync(Context(), Users, Guid.NewGuid().ToString(), UserBody("ada"), ifMatch: null));

        Assert.That(result.Error?.StatusCode, Is.EqualTo(404));
    }

    // ─── Patch ───

    [Test]
    public async Task PatchAsync_ReplaceAnAttribute_RaisesTheVersionAsync()
    {
        var id = Id(await CreateAsync(Users, UserBody("ada")));

        var result = await PatchAsync(Users, id, """[{"op":"replace","path":"title","value":"Analyst"}]""");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(200));
            Assert.That(result.Version, Is.EqualTo(2));
            Assert.That(result.Resource!["title"]!.GetValue<string>(), Is.EqualTo("Analyst"));
            Assert.That((await JournalAsync()).Last().ChangedAttributes, Is.EqualTo(new[] { "title" }));
        }
    }

    [Test]
    public async Task PatchAsync_AddAndRemoveMembers_ChangesOnlyThoseReferencesAsync()
    {
        var ada = await CreateUserAsync("ada");
        var grace = await CreateUserAsync("grace");
        var alan = await CreateUserAsync("alan");
        var admins = await CreateGroupAsync("Admins", ada, grace);

        var result = await PatchAsync(Groups, admins, $$$"""
            [{"op":"add","path":"members","value":[{"value":"{{{alan}}}"}]},
             {"op":"remove","path":"members[value eq \"{{{grace}}}\"]"}]
            """);

        var entry = (await JournalAsync()).Last();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(200));
            Assert.That(result.Version, Is.EqualTo(2));
            Assert.That(await TargetsAsync(admins), Is.EquivalentTo(new[] { ada, alan }));
            Assert.That(result.Resource!["members"]!.AsArray().Select(m => Guid.Parse(m!["value"]!.GetValue<string>())),
                Is.EquivalentTo(new[] { ada, alan }));
            Assert.That(entry.Operation, Is.EqualTo(ScimStoreOperation.Patched));
            Assert.That(entry.ChangedAttributes, Is.EqualTo(new[] { "members" }));
        }
    }

    [Test]
    public async Task PatchAsync_AddingAMemberThatIsNotLive_Returns400AndChangesNothingAsync()
    {
        var ada = await CreateUserAsync("ada");
        var admins = await CreateGroupAsync("Admins", ada);

        var result = await PatchAsync(Groups, admins, $$$"""[{"op":"add","path":"members","value":[{"value":"{{{Guid.NewGuid()}}}"}]}]""");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidValue));
            Assert.That(await TargetsAsync(admins), Is.EqualTo(new[] { ada }));
            Assert.That(await VersionAsync(admins), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task PatchAsync_ChangingNothing_KeepsTheVersionAsync()
    {
        var ada = await CreateUserAsync("ada");
        var admins = await CreateGroupAsync("Admins", ada);

        var result = await PatchAsync(Groups, admins, $$$"""[{"op":"add","path":"members","value":[{"value":"{{{ada}}}"}]}]""");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(200));
            Assert.That(result.Version, Is.EqualTo(1));
            Assert.That(await CountAsync("ScimStoreJournalEntries"), Is.EqualTo(2), "the two creates only");
        }
    }

    [Test]
    public async Task PatchAsync_OneOperationFails_AppliesNoneAsync()
    {
        var id = Id(await CreateAsync(Users, UserBody("ada")));

        var result = await PatchAsync(Users, id, """
            [{"op":"replace","path":"title","value":"Analyst"},{"op":"replace","path":"active","value":"not-a-boolean"}]
            """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error?.StatusCode, Is.EqualTo(400));
            Assert.That(await StoredPayloadAsync(id), Does.Not.Contain("Analyst"));
        }
    }

    [Test]
    public async Task PatchAsync_RemovingARequiredAttribute_Returns400Async()
    {
        var id = Id(await CreateAsync(Users, UserBody("ada")));

        var result = await PatchAsync(Users, id, """[{"op":"remove","path":"userName"}]""");

        Assert.That(result.Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidValue));
    }

    [Test]
    public async Task PatchAsync_SetManager_HoldsItAsAReferenceAsync()
    {
        var grace = await CreateUserAsync("grace");
        var ada = await CreateUserAsync("ada");

        var result = await PatchAsync(Users, ada, $$$"""
            [{"op":"replace","path":"{{{ScimUrns.EnterpriseUser}}}:manager","value":{"value":"{{{grace}}}"}}]
            """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(200));
            Assert.That(await TargetsAsync(ada), Is.EqualTo(new[] { grace }));
            Assert.That((await JournalAsync()).Last().ChangedAttributes, Is.EqualTo(new[] { $"{ScimUrns.EnterpriseUser}:manager" }));
        }
    }

    [Test]
    public async Task PatchAsync_ExcludingMembers_LeavesThemOutOfTheResponseAsync()
    {
        var ada = await CreateUserAsync("ada");
        var grace = await CreateUserAsync("grace");
        var admins = await CreateGroupAsync("Admins", ada);

        var result = await WithServerAsync(server => server.PatchAsync(Context(), Groups, admins.ToString(),
            Operations($$$"""[{"op":"add","path":"members","value":[{"value":"{{{grace}}}"}]}]"""), ifMatch: null,
            new ScimAttributeSelection(ExcludedAttributes: ["members"])));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Resource!.ContainsKey("members"), Is.False);
            Assert.That(result.Resource["displayName"]!.GetValue<string>(), Is.EqualTo("Admins"));
        }
    }

    [Test]
    public async Task PatchAsync_IfMatchStale_Returns412Async()
    {
        var id = Id(await CreateAsync(Users, UserBody("ada")));

        var result = await WithServerAsync(server => server.PatchAsync(Context(), Users, id.ToString(),
            Operations("""[{"op":"replace","path":"title","value":"Analyst"}]"""), ifMatch: "W/\"2\""));

        Assert.That(result.Error?.StatusCode, Is.EqualTo(412));
    }

    // ─── Delete ───

    [Test]
    public async Task DeleteAsync_User_TombstonesItAndJournalsEveryResourceThatLostAReferenceAsync()
    {
        var ada = await CreateUserAsync("ada");
        var admins = await CreateGroupAsync("Admins", ada);
        var reportBody = UserBody("grace");
        reportBody[ScimUrns.EnterpriseUser] = new JsonObject { ["manager"] = new JsonObject { ["value"] = ada.ToString() } };
        var grace = Id(await CreateAsync(Users, reportBody));

        var result = await WithServerAsync(server => server.DeleteAsync(Context(), Users, ada.ToString(), ifMatch: null));

        var journal = await JournalAsync();
        var last = journal.Where(e => e.ChangeSequence == journal.Max(j => j.ChangeSequence)).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(204));
            Assert.That((await WithServerAsync(server => server.GetAsync(Context(), Users, ada.ToString()))).Error?.StatusCode, Is.EqualTo(404));
            Assert.That(await TargetsAsync(admins), Is.Empty);
            Assert.That(await TargetsAsync(grace), Is.Empty, "a deleted manager is cleared, not left dangling");
            Assert.That(await VersionAsync(admins), Is.EqualTo(2));
            Assert.That(last.Select(e => (e.ResourceId, e.ResourceTypeName, e.Operation, string.Join(",", e.ChangedAttributes))),
                Is.EquivalentTo(new[]
                {
                    (ada, "User", ScimStoreOperation.Deleted, ""),
                    (admins, "Group", ScimStoreOperation.ReferenceRemovedByDeletion, "members"),
                    (grace, "User", ScimStoreOperation.ReferenceRemovedByDeletion, $"{ScimUrns.EnterpriseUser}:manager")
                }));
        }

        Assert.That((await CreateAsync(Users, UserBody("ada"))).StatusCode, Is.EqualTo(201), "its userName is free again");
    }

    [Test]
    public async Task DeleteAsync_IfMatchStale_Returns412AndKeepsTheResourceAsync()
    {
        var ada = await CreateUserAsync("ada");

        var result = await WithServerAsync(server => server.DeleteAsync(Context(), Users, ada.ToString(), ifMatch: "W/\"9\""));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error?.StatusCode, Is.EqualTo(412));
            Assert.That(await VersionAsync(ada), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task DeleteAsync_NoSuchResource_Returns404Async()
    {
        var result = await WithServerAsync(server => server.DeleteAsync(Context(), Users, Guid.NewGuid().ToString(), ifMatch: null));

        Assert.That(result.Error?.StatusCode, Is.EqualTo(404));
    }

    // ─── Helpers ───

    private ScimRequestContext Context(int? systemId = null) =>
        new(systemId ?? _systemId, BaseUrl, _key.Id, _key.Name, ClientAddress);

    private async Task<T> WithServerAsync<T>(Func<ScimServer, Task<T>> call)
    {
        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        return await call(jim.Scim);
    }

    private Task<ScimServiceResult> CreateAsync(string endpoint, JsonObject body) =>
        WithServerAsync(server => server.CreateAsync(Context(), endpoint, body));

    private Task<ScimServiceResult> PatchAsync(string endpoint, Guid id, string operations) =>
        WithServerAsync(server => server.PatchAsync(Context(), endpoint, id.ToString(), Operations(operations), ifMatch: null));

    private async Task<Guid> CreateUserAsync(string userName)
    {
        var result = await CreateAsync(Users, UserBody(userName));
        Assert.That(result.StatusCode, Is.EqualTo(201), result.Error?.Detail);
        return Id(result);
    }

    private async Task<Guid> CreateGroupAsync(string displayName, params Guid[] members)
    {
        var result = await CreateAsync(Groups, GroupBody(displayName, members));
        Assert.That(result.StatusCode, Is.EqualTo(201), result.Error?.Detail);
        return Id(result);
    }

    private static Guid Id(ScimServiceResult result) => Guid.Parse(result.Resource!["id"]!.GetValue<string>());

    private static JsonObject UserBody(string userName, string? externalId = null)
    {
        var body = new JsonObject { ["schemas"] = new JsonArray(ScimUrns.User), ["userName"] = userName, ["displayName"] = userName };
        if (externalId != null)
            body["externalId"] = externalId;
        return body;
    }

    private static JsonObject GroupBody(string displayName, params Guid[] members) => new()
    {
        ["schemas"] = new JsonArray(ScimUrns.Group),
        ["displayName"] = displayName,
        ["members"] = new JsonArray(members.Select(m => (JsonNode)new JsonObject { ["value"] = m.ToString() }).ToArray())
    };

    private static List<ScimPatchOperation> Operations(string json) =>
        JsonNode.Parse(json)!.AsArray().Select(node => new ScimPatchOperation
        {
            Op = node!["op"]!.GetValue<string>(),
            Path = node["path"]?.GetValue<string>(),
            Value = node["value"]?.DeepClone()
        }).ToList();

    private async Task<List<ScimStoreJournalEntry>> JournalAsync()
    {
        await using var ctx = NewContext();
        return await ctx.ScimStoreJournalEntries.OrderBy(e => e.Id).ToListAsync();
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""SELECT count(*) FROM "{table}" """, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> ReferenceCountAsync(Guid source) => (await TargetsAsync(source)).Count;

    private async Task<List<Guid>> TargetsAsync(Guid source)
    {
        await using var ctx = NewContext();
        return (await new PostgresDataRepository(ctx).ScimStore.GetReferencesAsync(source)).Select(r => r.TargetId).ToList();
    }

    private async Task<string> StoredPayloadAsync(Guid id)
    {
        await using var ctx = NewContext();
        return (await ctx.ScimStoreResources.SingleAsync(r => r.Id == id)).Payload!;
    }

    private async Task<long> VersionAsync(Guid id)
    {
        await using var ctx = NewContext();
        return (await ctx.ScimStoreResources.SingleAsync(r => r.Id == id)).Version;
    }
}
