// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using JIM.Connectors.Graph;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The Microsoft Graph Connector's Delta Import: Graph's delta rounds say which objects changed, and each is read again
/// for its current state, since JIM stages a changed object whole.
/// </summary>
[TestFixture]
public class GraphConnectorDeltaImportTests : GraphImportTestBase
{
    [Test]
    public async Task DeltaImport_ChangedUser_IsReadAgainAndStagedAsUpdatedWithItsCurrentStateAsync()
    {
        Tenant.AddUser("u1", "Ada", u => u["jobTitle"] = "Analyst");
        Tenant.AddUser("u2", "Grace");
        var system = ConnectedSystem(Users("displayName", "jobTitle"));
        var state = await FullImportStateAsync(system);

        Tenant.Users.Single(u => (string)u["id"]! == "u1")["jobTitle"] = "Engineer";
        Tenant.RecordChange("users", "u1");
        var results = await DeltaAsync(system, state);

        var ada = results.SelectMany(r => r.ImportObjects).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Values(ada, "id"), Is.EqualTo(new[] { "u1" }));
            Assert.That(ada.ChangeType, Is.EqualTo(ObjectChangeType.Updated));
            Assert.That(Values(ada, "jobTitle"), Is.EqualTo(new[] { "Engineer" }));
            Assert.That(Values(ada, "displayName"), Is.EqualTo(new[] { "Ada" }));
            Assert.That(Values(ada, "sourceOfAuthority"), Is.EqualTo(new[] { "cloud" }));
            Assert.That(results.Select(r => r.CompleteRead), Is.All.False);
            Assert.That(results.Select(r => r.WarningMessage), Is.All.Null);
            Assert.That(DeltaRunListRequests(), Is.Empty, "A Delta Import must not read the whole collection.");
        }
    }

    [Test]
    public async Task DeltaImport_ChangedUser_IsReadWithItsSelectionAndManagerAsync()
    {
        Tenant.AddUser("boss", "Grace");
        Tenant.AddUser("u1", "Ada");
        var system = ConnectedSystem(Users("displayName", "manager"));
        var state = await FullImportStateAsync(system);

        Tenant.Users.Single(u => (string)u["id"]! == "u1")["managerId"] = "boss";
        Tenant.RecordChange("users", "u1");
        var results = await DeltaAsync(system, state);

        var read = DeltaRunGraphRequests().Single(r => r.RequestUri!.AbsolutePath == "/v1.0/$batch");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.SelectMany(r => r.ImportObjects).Single().Attributes.Single(a => a.Name == "manager").ReferenceValues, Is.EqualTo(new[] { "boss" }));
            Assert.That(read.Body, Does.Contain("/users/u1?$select="));
            Assert.That(read.Body, Does.Contain("$expand=manager($select=id)"));
        }
    }

    [Test]
    public async Task DeltaImport_ChangedGroup_IsReadAgainWithItsWholeMembershipAsync()
    {
        // Graph's members@delta carries only the members added and removed, and JIM stages a changed object whole, so a
        // changed group's membership is read in full, exactly as a Full Import reads it.
        Tenant.MembersPageSize = 2;
        Tenant.AddGroup("g1", "Finance");
        Tenant.Members["g1"] = [("user", "u1"), ("user", "u2")];
        var system = ConnectedSystem(Groups("displayName", "members"));
        var state = await FullImportStateAsync(system);

        Tenant.Members["g1"].Add(("user", "u3"));
        Tenant.RecordChange("groups", "g1");
        var objects = (await DeltaAsync(system, state)).SelectMany(r => r.ImportObjects).ToList();

        Assert.That(objects.Single().Attributes.Single(a => a.Name == "members").ReferenceValues, Is.EqualTo(new[] { "u1", "u2", "u3" }));
    }

    [Test]
    public async Task DeltaImport_RemovedObjects_AreStagedAsDeletedWithoutReadingThemAsync([Values("deleted", "changed")] string reason)
    {
        // "changed" is Graph's word for an object in the recycle bin, which is gone from the directory all the same.
        Tenant.AddUser("u1", "Ada");
        Tenant.AddUser("u2", "Grace");
        var system = ConnectedSystem(Users("displayName"));
        var state = await FullImportStateAsync(system);

        Tenant.RecordRemoval("users", "u2", reason);
        var removed = (await DeltaAsync(system, state)).SelectMany(r => r.ImportObjects).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed.ObjectType, Is.EqualTo("user"));
            Assert.That(removed.ChangeType, Is.EqualTo(ObjectChangeType.Deleted));
            Assert.That(Values(removed, "id"), Is.EqualTo(new[] { "u2" }));
            Assert.That(DeltaRunGraphRequests().Select(r => r.RequestUri!.AbsolutePath), Does.Not.Contain("/v1.0/$batch"));
        }
    }

    [Test]
    public async Task DeltaImport_ObjectRemovedAndRestoredInOneRound_IsStagedAsItNowStandsAsync()
    {
        Tenant.AddUser("u1", "Ada");
        var system = ConnectedSystem(Users("displayName"));
        var state = await FullImportStateAsync(system);

        Tenant.RecordRemoval("users", "u1", "changed");
        Tenant.AddUser("u1", "Ada Restored");
        Tenant.RecordChange("users", "u1");
        var objects = (await DeltaAsync(system, state)).SelectMany(r => r.ImportObjects).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(objects, Has.Count.EqualTo(1));
            Assert.That(objects.Single().ChangeType, Is.EqualTo(ObjectChangeType.Updated));
            Assert.That(Values(objects.Single(), "displayName"), Is.EqualTo(new[] { "Ada Restored" }));
        }
    }

    [Test]
    public async Task DeltaImport_ObjectChangedThenRemovedInOneRound_IsStagedAsDeletedAsync()
    {
        Tenant.AddUser("u1", "Ada");
        var system = ConnectedSystem(Users("displayName"));
        var state = await FullImportStateAsync(system);

        Tenant.RecordChange("users", "u1");
        Tenant.RecordRemoval("users", "u1");
        var objects = (await DeltaAsync(system, state)).SelectMany(r => r.ImportObjects).ToList();

        Assert.That(objects.Single().ChangeType, Is.EqualTo(ObjectChangeType.Deleted));
    }

    [Test]
    public async Task DeltaImport_GroupReportedOnSeveralPages_IsReadOnceAsync()
    {
        // A large group's member changes are spread over several delta pages, each carrying the group again.
        Tenant.DeltaPageSize = 1;
        Tenant.AddGroup("g1", "Finance");
        Tenant.Members["g1"] = [("user", "u1")];
        var system = ConnectedSystem(Groups("displayName", "members"));
        var state = await FullImportStateAsync(system);

        Tenant.RecordChange("groups", "g1");
        Tenant.RecordChange("groups", "g1");
        Tenant.RecordChange("groups", "g1");
        Tenant.MembersRequested.Clear();
        var objects = (await DeltaAsync(system, state)).SelectMany(r => r.ImportObjects).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(objects, Has.Count.EqualTo(1));
            Assert.That(Tenant.MembersRequested, Is.EqualTo(new[] { "g1" }));
        }
    }

    [Test]
    public async Task DeltaImport_ManyChanges_ArePagedAtTheRunProfilesPageSizeAsync()
    {
        for (var i = 1; i <= 5; i++)
            Tenant.AddUser($"u{i}", $"User {i}");
        var system = ConnectedSystem(Users("displayName"));
        var state = await FullImportStateAsync(system);

        for (var i = 1; i <= 5; i++)
            Tenant.RecordChange("users", $"u{i}");
        var results = await DeltaAsync(system, state, pageSize: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.ImportObjects.Count), Is.EqualTo(new[] { 2, 2, 1 }));
            Assert.That(results.SelectMany(r => r.ImportObjects).Select(o => Values(o, "id").Single()), Is.EquivalentTo(new[] { "u1", "u2", "u3", "u4", "u5" }));
        }
    }

    [Test]
    public async Task DeltaImport_UsersAndGroups_BothRoundsAreReadAndBothLinksAdvancedOnTheLastPageOnlyAsync()
    {
        Tenant.AddUser("u1", "Ada");
        Tenant.AddGroup("g1", "Finance");
        var system = ConnectedSystem(Users("displayName"), Groups("displayName"));
        var state = await FullImportStateAsync(system);

        Tenant.RecordChange("users", "u1");
        Tenant.RecordChange("groups", "g1");
        var results = await DeltaAsync(system, state);

        var next = GraphImportState.Read(results.Last().PersistedConnectorData, Logger);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.SelectMany(r => r.ImportObjects).Select(o => o.ObjectType), Is.EqualTo(new[] { "user", "group" }));
            Assert.That(results.Take(results.Count - 1).Select(r => r.PersistedConnectorData), Is.All.Null);
            Assert.That(next!.DeltaLinks["user"], Does.Not.Contain(FakeGraphTenant.BaselineToken("users")));
            Assert.That(next.DeltaLinks["group"], Does.Not.Contain(FakeGraphTenant.BaselineToken("groups")));
        }
    }

    [Test]
    public async Task DeltaImport_NoChanges_StagesNothingAndStillAdvancesTheLinksAsync()
    {
        Tenant.AddUser("u1", "Ada");
        var system = ConnectedSystem(Users("displayName"));
        var state = await FullImportStateAsync(system);

        var results = await DeltaAsync(system, state);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.SelectMany(r => r.ImportObjects), Is.Empty);
            Assert.That(results.Last().PersistedConnectorData, Is.Not.Null);
            Assert.That(results.Single().CompleteRead, Is.False);
        }
    }

    [Test]
    public async Task DeltaImport_NoPersistedState_ReadsEveryObjectAndSaysSoAsync()
    {
        Tenant.AddUser("u1", "Ada");
        Tenant.AddUser("u2", "Grace");
        var system = ConnectedSystem(Users("displayName"));

        var results = await DeltaAsync(system, persistedConnectorData: null, pageSize: 1);

        AssertReadEveryObject(results, "u1", "u2");
        Assert.That(results.First().WarningMessage, Does.Contain("no delta link"));
    }

    [TestCase(HttpStatusCode.Gone, "resyncRequired")]
    [TestCase(HttpStatusCode.BadRequest, "syncStateNotFound")]
    public async Task DeltaImport_DeltaLinkNoLongerHonoured_ReadsEveryObjectAndSaysSoAsync(HttpStatusCode status, string code)
    {
        Tenant.AddUser("u1", "Ada");
        Tenant.AddUser("u2", "Grace");
        var system = ConnectedSystem(Users("displayName"));
        var state = await FullImportStateAsync(system);

        Tenant.ExpiredDeltaTokens[FakeGraphTenant.BaselineToken("users")] = (status, code);
        var results = await DeltaAsync(system, state, pageSize: 1);

        AssertReadEveryObject(results, "u1", "u2");
        Assert.That(results.First().WarningMessage, Does.Contain("seven days"));
    }

    [Test]
    public async Task DeltaImport_SecondTypesLinkNoLongerHonoured_ReadsEveryObjectOfBothTypesAsync()
    {
        // JIM holds a run to the answer its first page gives, so whether a run reads everything is settled before any
        // object is returned, for every Object Type.
        Tenant.AddUser("u1", "Ada");
        Tenant.AddGroup("g1", "Finance");
        var system = ConnectedSystem(Users("displayName"), Groups("displayName"));
        var state = await FullImportStateAsync(system);

        Tenant.ExpiredDeltaTokens[FakeGraphTenant.BaselineToken("groups")] = (HttpStatusCode.Gone, "resyncRequired");
        var results = await DeltaAsync(system, state);

        AssertReadEveryObject(results, "u1", "g1");
    }

    [Test]
    public async Task DeltaImport_AttributeSelectionChangedSinceTheBaseline_ReadsEveryObjectAndSaysSoAsync()
    {
        // A delta round tracks only what its first request selected, so a newly selected attribute's changes would never
        // be reported.
        Tenant.AddUser("u1", "Ada");
        var state = await FullImportStateAsync(ConnectedSystem(Users("displayName")));

        var results = await DeltaAsync(ConnectedSystem(Users("displayName", "jobTitle")), state);

        AssertReadEveryObject(results, "u1");
        Assert.That(results.First().WarningMessage, Does.Contain("selected attributes"));
    }

    [Test]
    public async Task DeltaImport_ObjectTypeNewlySelected_ReadsEveryObjectAsync()
    {
        Tenant.AddUser("u1", "Ada");
        Tenant.AddGroup("g1", "Finance");
        var state = await FullImportStateAsync(ConnectedSystem(Users("displayName")));

        var results = await DeltaAsync(ConnectedSystem(Users("displayName"), Groups("displayName")), state);

        AssertReadEveryObject(results, "u1", "g1");
    }

    [Test]
    public async Task DeltaImport_ObjectMovedToCloudManagement_ReadsEveryObjectSoItsSourceOfAuthorityChangesAsync()
    {
        // Moving an object to cloud management changes nothing a delta round reports, so the change is seen in the set of
        // cloud-managed objects instead.
        Tenant.AddUser("u1", "Ada", u => u["onPremisesSyncEnabled"] = true);
        var system = ConnectedSystem(Users("displayName"));
        var state = await FullImportStateAsync(system);

        Tenant.CloudManaged.Add("u1");
        var results = await DeltaAsync(system, state);

        AssertReadEveryObject(results, "u1");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Values(results.SelectMany(r => r.ImportObjects).Single(), "sourceOfAuthority"), Is.EqualTo(new[] { "cloud" }));
            Assert.That(results.First().WarningMessage, Does.Contain("cloud management"));
        }
    }

    [Test]
    public async Task DeltaImport_CloudManagedSetUnchanged_ReadsOnlyTheChangesAsync()
    {
        Tenant.AddUser("u1", "Ada", u => u["onPremisesSyncEnabled"] = true);
        Tenant.AddUser("u2", "Grace");
        Tenant.CloudManaged.Add("u1");
        var system = ConnectedSystem(Users("displayName"));
        var state = await FullImportStateAsync(system);

        Tenant.RecordChange("users", "u2");
        var results = await DeltaAsync(system, state);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.CompleteRead), Is.All.False);
            Assert.That(results.SelectMany(r => r.ImportObjects).Select(o => Values(o, "id").Single()), Is.EqualTo(new[] { "u2" }));
        }
    }

    [Test]
    public async Task DeltaImport_MemberReadHiddenGrantedSinceTheBaseline_ReadsEveryObjectAsync()
    {
        // Hidden memberships the Connector could not read before were never baselined, so no delta round would report them.
        Tenant.Roles = ["User.Read.All", "Group.Read.All"];
        Tenant.AddGroup("g1", "Board", g => g["visibility"] = "HiddenMembership");
        var system = ConnectedSystem(Groups("displayName", "members"));
        var state = await FullImportStateAsync(system);

        Tenant.Roles = GraphTestTokens.AllPermissions;
        var results = await DeltaAsync(system, state);

        AssertReadEveryObject(results, "g1");
    }

    [Test]
    public async Task DeltaImport_ChangedObjectNotYetReadable_IsReadAgainUntilGraphFindsItAsync()
    {
        // Replication delay can leave an object Graph has just reported as changed unreadable for a few seconds.
        var system = ConnectedSystem(Users("displayName"));
        var state = await FullImportStateAsync(system);

        Tenant.AddUser("u1", "Ada");
        Tenant.RecordChange("users", "u1");
        Tenant.NotYetReadable["u1"] = 2;
        var objects = (await DeltaAsync(system, state)).SelectMany(r => r.ImportObjects).ToList();

        Assert.That(Values(objects.Single(), "displayName"), Is.EqualTo(new[] { "Ada" }));
    }

    [Test]
    public void DeltaImport_ChangedObjectNeverFound_FailsTheRunSoTheChangeIsReadAgainNextTimeAsync()
    {
        // Staging it as deleted could delete an object that exists; skipping it would advance the link past its change.
        // Failing keeps the old link, and the next run either reads it or is told it was removed.
        Tenant.AddUser("u1", "Ada");
        var system = ConnectedSystem(Users("displayName"));
        var state = FullImportStateAsync(system).GetAwaiter().GetResult();

        Tenant.RecordChange("users", "u1");
        Tenant.NotYetReadable["u1"] = 100;

        var ex = Assert.ThrowsAsync<GraphRequestException>(async () => await DeltaAsync(system, state));
        Assert.That(ex!.Message, Does.Contain("u1"));
    }

    [Test]
    public void GetPhases_DeltaImport_DeclaresPreparingAndFetching()
    {
        var phases = new GraphConnector().GetPhases(ConnectedSystem(Users("displayName")), new ConnectedSystemRunProfile { Name = "Delta Import", RunType = ConnectedSystemRunType.DeltaImport });

        Assert.That(phases.Select(p => p.Key), Is.EqualTo(new[] { GraphConnectorPhases.Prepare, GraphConnectorPhases.Fetch }));
    }

    [Test]
    public async Task DescribeImportWatermark_FullImportState_DescribesEachObjectTypeByWhenItsLinkWasTakenAsync()
    {
        Tenant.AddUser("u1", "Ada");
        Tenant.AddGroup("g1", "Finance");
        var before = DateTime.UtcNow;
        var state = await FullImportStateAsync(ConnectedSystem(Users("displayName"), Groups("displayName")));

        var description = new GraphConnector().DescribeImportWatermark(state, Logger);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(description!.Kind, Is.EqualTo("delta link"));
            Assert.That(description.Points.Select(p => p.Scope), Is.EquivalentTo(new[] { "user", "group" }));
            Assert.That(description.Points.Select(p => p.Time), Is.All.GreaterThanOrEqualTo(before.AddSeconds(-1)));
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not json")]
    public void DescribeImportWatermark_NoReadableState_IsNull(string? persisted)
    {
        Assert.That(new GraphConnector().DescribeImportWatermark(persisted, Logger), Is.Null);
    }

    [Test]
    public void RemoveImportWatermark_LeavesNothing()
    {
        Assert.That(new GraphConnector().RemoveImportWatermark("""{"deltaLinks":{"user":"x"}}""", Logger), Is.Null);
    }

    private async Task<string> FullImportStateAsync(ConnectedSystem connectedSystem)
    {
        var results = await RunAsync(connectedSystem);
        return results.Last().PersistedConnectorData ?? throw new AssertionException("The Full Import persisted no state.");
    }

    private async Task<List<ConnectedSystemImportResult>> DeltaAsync(ConnectedSystem connectedSystem, string? persistedConnectorData, int pageSize = 100)
    {
        _deltaRunStartsAt = Tenant.Handler.Requests.Count;
        return await RunAsync(connectedSystem, ConnectedSystemRunType.DeltaImport, persistedConnectorData, pageSize);
    }

    private int _deltaRunStartsAt;

    private List<StubHttpMessageHandler.RecordedRequest> DeltaRunGraphRequests() =>
        Tenant.Handler.Requests.Skip(_deltaRunStartsAt).Where(r => r.RequestUri!.Host == "graph.microsoft.com").ToList();

    /// <summary>Requests the Delta Import made for a whole collection rather than for its changes.</summary>
    private List<string> DeltaRunListRequests() => DeltaRunGraphRequests()
        .Where(r => r.RequestUri!.AbsolutePath is "/v1.0/users" or "/v1.0/groups" && Query(r, "$filter") == null)
        .Select(r => r.RequestUri!.ToString())
        .ToList();

    private static void AssertReadEveryObject(List<ConnectedSystemImportResult> results, params string[] ids)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.CompleteRead), Is.All.True, "A run that cannot read the changes reads every object, on every page.");
            Assert.That(results.SelectMany(r => r.ImportObjects).Select(o => Values(o, "id").Single()), Is.EquivalentTo(ids));
            Assert.That(results.First().WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(results.Last().PersistedConnectorData, Is.Not.Null, "A full read takes new links for the next Delta Import.");
        }
    }
}
