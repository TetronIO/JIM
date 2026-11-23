// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using JIM.Connectors.Graph;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class GraphConnectorImportTests : GraphImportTestBase
{
    [Test]
    public async Task FullImport_Users_AreStagedAsAddedWithTheirSelectedAttributesAsync()
    {
        Tenant.AddUser("u1", "Ada Lovelace", u => { u["jobTitle"] = "Analyst"; u["accountEnabled"] = true; u["department"] = "Unselected"; });

        var objects = await ImportAsync(Users("displayName", "jobTitle", "accountEnabled"));

        var ada = objects.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ada.ObjectType, Is.EqualTo("user"));
            Assert.That(ada.ChangeType, Is.EqualTo(ObjectChangeType.Added));
            Assert.That(Values(ada, "id"), Is.EqualTo(new[] { "u1" }));
            Assert.That(Values(ada, "displayName"), Is.EqualTo(new[] { "Ada Lovelace" }));
            Assert.That(Values(ada, "jobTitle"), Is.EqualTo(new[] { "Analyst" }));
            Assert.That(ada.Attributes.Single(a => a.Name == "accountEnabled").BoolValues, Is.EqualTo(new[] { true }));
            Assert.That(ada.Attributes.Select(a => a.Name), Does.Not.Contain("department"));
        }
    }

    [Test]
    public async Task FullImport_SelectsOnlyWhatItNeedsAsync()
    {
        Tenant.AddUser("u1", "Ada");

        await ImportAsync(Users("displayName", "extensionAttribute3"));

        var select = Query(UserListRequest(), "$select")!.Split(',');
        using (Assert.EnterMultipleScope())
        {
            Assert.That(select, Is.SupersetOf(new[] { "id", "displayName", "onPremisesExtensionAttributes", "onPremisesSyncEnabled", "userType" }));
            Assert.That(select, Does.Not.Contain("jobTitle"));
            Assert.That(select, Does.Not.Contain("extensionAttribute3"));
            Assert.That(select, Does.Not.Contain("sourceOfAuthority"));
            Assert.That(Query(UserListRequest(), "$expand"), Is.Null, "The manager is only expanded when selected.");
        }
    }

    [Test]
    public async Task FullImport_PagesThroughEveryUserAtTheRunProfilesPageSizeAsync()
    {
        for (var i = 1; i <= 5; i++)
            Tenant.AddUser($"u{i}", $"User {i}");

        var results = await RunAsync(ConnectedSystem(Users("displayName")), pageSize: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.SelectMany(r => r.ImportObjects).Select(o => Values(o, "id").Single()), Is.EqualTo(new[] { "u1", "u2", "u3", "u4", "u5" }));
            Assert.That(results, Has.Count.EqualTo(3));
            Assert.That(Query(UserListRequest(), "$top"), Is.EqualTo("2"));
        }
    }

    [Test]
    public async Task FullImport_PageSizeAboveGraphsLimit_IsCappedAt999Async()
    {
        Tenant.AddUser("u1", "Ada");

        await RunAsync(ConnectedSystem(Users("displayName")), pageSize: 5000);

        Assert.That(Query(UserListRequest(), "$top"), Is.EqualTo("999"));
    }

    [Test]
    public async Task FullImport_Manager_IsExpandedAndStagedAsAReferenceAsync()
    {
        Tenant.AddUser("boss", "Grace");
        Tenant.AddUser("u1", "Ada", u => u["managerId"] = "boss");

        var objects = await ImportAsync(Users("displayName", "manager"));

        var manager = objects.Single(o => Values(o, "id").Single() == "u1").Attributes.Single(a => a.Name == "manager");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.ReferenceValues, Is.EqualTo(new[] { "boss" }));
            Assert.That(Query(UserListRequest(), "$expand"), Is.EqualTo("manager($select=id)"));
            Assert.That(objects.Single(o => Values(o, "id").Single() == "boss").Attributes.Select(a => a.Name), Does.Not.Contain("manager"));
        }
    }

    [Test]
    public async Task FullImport_GroupMembers_AreReadInFullAsReferencesToUsersAndGroupsOnlyAsync()
    {
        Tenant.MembersPageSize = 2;
        Tenant.AddGroup("g1", "Finance");
        Tenant.Members["g1"] = [("user", "u1"), ("user", "u2"), ("group", "g2"), ("device", "d1"), ("servicePrincipal", "sp1"), ("user", "u3")];

        var objects = await ImportAsync(Groups("displayName", "members"));

        var members = objects.Single().Attributes.Single(a => a.Name == "members");
        Assert.That(members.ReferenceValues, Is.EquivalentTo(new[] { "u1", "u2", "g2", "u3" }));
    }

    [Test]
    public async Task FullImport_GroupMembers_AreReadThroughBatchesOfTwentyAsync()
    {
        for (var i = 1; i <= 25; i++)
        {
            Tenant.AddGroup($"g{i}", $"Group {i}");
            Tenant.Members[$"g{i}"] = [("user", $"u{i}")];
        }

        var objects = await ImportAsync(Groups("displayName", "members"));

        var batches = Tenant.GraphRequests.Where(r => r.RequestUri!.AbsolutePath == "/v1.0/$batch").ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objects.Select(o => o.Attributes.Single(a => a.Name == "members").ReferenceValues.Single()),
                Is.EqualTo(Enumerable.Range(1, 25).Select(i => $"u{i}")));
            Assert.That(batches, Has.Count.EqualTo(2));
            Assert.That(Tenant.GraphRequests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/members", StringComparison.Ordinal)), Is.Zero,
                "No group's members fitted in more than one page, so nothing was read outside a batch.");
        }
    }

    [Test]
    public async Task FullImport_GroupWithNoMembers_HasNoMembersAttributeAsync()
    {
        Tenant.AddGroup("g1", "Empty");

        var objects = await ImportAsync(Groups("displayName", "members"));

        Assert.That(objects.Single().Attributes.Select(a => a.Name), Does.Not.Contain("members"));
    }

    [Test]
    public async Task FullImport_MembersCannotBeRead_ReportsTheAttributeSoJimKeepsTheMembershipItHoldsAsync()
    {
        // A group staged with some or none of its members would have JIM remove the rest. Reported as an unreadable
        // value instead, the attribute keeps what JIM already holds, and the group's other attributes still update.
        Tenant.AddGroup("g1", "Readable");
        Tenant.AddGroup("g2", "Unreadable");
        Tenant.Members["g1"] = [("user", "u1")];
        Tenant.MembersFailures["g2"] = HttpStatusCode.Forbidden;

        var objects = await ImportAsync(Groups("displayName", "members"));

        var unreadable = objects.Single(o => Values(o, "id").Single() == "g2");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objects.Single(o => Values(o, "id").Single() == "g1").ErrorType, Is.Null);
            Assert.That(unreadable.ErrorType, Is.EqualTo(ConnectedSystemImportObjectError.AttributeValueError));
            Assert.That(unreadable.AttributeValueErrors!.Single().AttributeName, Is.EqualTo("members"));
            Assert.That(unreadable.AttributeValueErrors!.Single().Reason, Does.Contain("403"));
            Assert.That(unreadable.Attributes.Select(a => a.Name), Does.Not.Contain("members"));
            Assert.That(Values(unreadable, "displayName"), Is.EqualTo(new[] { "Unreadable" }));
            Assert.That(unreadable.ChangeType, Is.EqualTo(ObjectChangeType.Added));
        }
    }

    [Test]
    public async Task FullImport_HiddenMembershipWithoutPermission_ReportsMembersUnreadableWithoutAskingGraphAsync()
    {
        // Microsoft's documentation does not say what Graph answers for a hidden membership without Member.Read.Hidden. An
        // empty list would read as a group with no members, and JIM would remove every member it holds, so the Connector
        // does not ask.
        Tenant.Roles = ["User.Read.All", "Group.Read.All"];
        Tenant.AddGroup("g1", "Board", g => g["visibility"] = "HiddenMembership");
        Tenant.AddGroup("g2", "Finance", g => g["visibility"] = "Public");
        Tenant.Members["g1"] = [("user", "u1")];
        Tenant.Members["g2"] = [("user", "u1")];

        var objects = await ImportAsync(Groups("displayName", "members"));

        var board = objects.Single(o => Values(o, "id").Single() == "g1");
        var finance = objects.Single(o => Values(o, "id").Single() == "g2");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Tenant.MembersRequested, Is.EqualTo(new[] { "g2" }));
            Assert.That(board.Attributes.Select(a => a.Name), Does.Not.Contain("members"));
            Assert.That(board.AttributeValueErrors!.Single().AttributeName, Is.EqualTo("members"));
            Assert.That(board.AttributeValueErrors!.Single().Reason, Does.Contain("Member.Read.Hidden"));
            Assert.That(Values(board, "displayName"), Is.EqualTo(new[] { "Board" }));
            Assert.That(finance.Attributes.Single(a => a.Name == "members").ReferenceValues, Is.EqualTo(new[] { "u1" }));
        }
    }

    [Test]
    public async Task FullImport_HiddenMembershipWithPermission_ReadsTheMembersAsync()
    {
        Tenant.AddGroup("g1", "Board", g => g["visibility"] = "HiddenMembership");
        Tenant.Members["g1"] = [("user", "u1")];

        var objects = await ImportAsync(Groups("displayName", "members"));

        Assert.That(objects.Single().Attributes.Single(a => a.Name == "members").ReferenceValues, Is.EqualTo(new[] { "u1" }));
    }

    [Test]
    public async Task FullImport_ReadingMembers_SelectsVisibilityWithoutStagingItAsync()
    {
        Tenant.AddGroup("g1", "Finance", g => g["visibility"] = "Public");

        var objects = await ImportAsync(Groups("displayName", "members"));

        var groupList = Tenant.GraphRequests.First(r => r.RequestUri!.AbsolutePath == "/v1.0/groups" && Query(r, "$filter") == null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Query(groupList, "$select")!.Split(','), Does.Contain("visibility"));
            Assert.That(objects.Single().Attributes.Select(a => a.Name), Does.Not.Contain("visibility"));
        }
    }

    [Test]
    public async Task FullImport_Owners_AreStagedAsReferencesAsync()
    {
        Tenant.AddGroup("g1", "Finance");
        Tenant.Owners["g1"] = ["u1", "u2"];

        var objects = await ImportAsync(Groups("displayName", "owners"));

        Assert.That(objects.Single().Attributes.Single(a => a.Name == "owners").ReferenceValues, Is.EquivalentTo(new[] { "u1", "u2" }));
    }

    [Test]
    public async Task FullImport_SourceOfAuthority_IsDerivedForEveryObjectAsync()
    {
        Tenant.AddUser("cloud", "Cloud");
        Tenant.AddUser("synced", "Synced", u => u["onPremisesSyncEnabled"] = true);
        Tenant.AddUser("converted", "Converted", u => u["onPremisesSyncEnabled"] = true);
        Tenant.AddUser("guest", "Guest", u => u["userType"] = "Guest");
        Tenant.CloudManaged.Add("converted");

        var objects = (await RunAsync(Tracked(ConnectedSystem(Users("displayName"))))).SelectMany(r => r.ImportObjects).ToList();

        Assert.That(objects.ToDictionary(o => Values(o, "id").Single(), o => Values(o, "sourceOfAuthority").Single()), Is.EqualTo(new Dictionary<string, string>
        {
            ["cloud"] = "cloud",
            ["synced"] = "onPremisesDirectory",
            ["converted"] = "cloud",
            ["guest"] = "externalEntraDirectory"
        }));
    }

    [Test]
    public async Task FullImport_SourceOfAuthorityNotTracked_DerivesFromSynchronisationWithoutAskingOrWarningAsync()
    {
        // Tracking is off by default, since its permission also allows changing Source of Authority. A synchronised
        // object switched to cloud management then still reads as onPremisesDirectory, which the docs say plainly.
        Tenant.AddUser("cloud", "Cloud");
        Tenant.AddUser("converted", "Converted", u => u["onPremisesSyncEnabled"] = true);
        Tenant.CloudManaged.Add("converted");

        var results = await RunAsync(ConnectedSystem(Users("displayName")));

        var objects = results.SelectMany(r => r.ImportObjects).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objects.ToDictionary(o => Values(o, "id").Single(), o => Values(o, "sourceOfAuthority").Single()), Is.EqualTo(new Dictionary<string, string>
            {
                ["cloud"] = "cloud",
                ["converted"] = "onPremisesDirectory"
            }));
            Assert.That(Tenant.GraphRequests.Where(r => Query(r, "$filter")?.Contains("isCloudManaged", StringComparison.Ordinal) == true), Is.Empty);
            Assert.That(results.Select(r => r.WarningMessage), Is.All.Null);
        }
    }

    [Test]
    public async Task FullImport_CloudManagedQuery_UsesEventualConsistencyAndAsksOnlyForIdsAsync()
    {
        Tenant.AddUser("u1", "Ada");

        await RunAsync(Tracked(ConnectedSystem(Users("displayName"))));

        var request = Tenant.GraphRequests.Single(r => Query(r, "$filter")?.Contains("isCloudManaged", StringComparison.Ordinal) == true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Headers.GetValues("ConsistencyLevel").Single(), Is.EqualTo("eventual"));
            Assert.That(Query(request, "$select"), Is.EqualTo("id"));
            Assert.That(Query(request, "$count"), Is.EqualTo("true"));
        }
    }

    [TestCase(HttpStatusCode.BadRequest)]
    [TestCase(HttpStatusCode.Forbidden)]
    public async Task FullImport_CloudManagedQueryRefused_WarnsAndDerivesFromSynchronisationAloneAsync(HttpStatusCode refusal)
    {
        Tenant.CloudManagedQueryStatus = refusal;
        Tenant.AddUser("synced", "Synced", u => u["onPremisesSyncEnabled"] = true);

        var results = await RunAsync(Tracked(ConnectedSystem(Users("displayName"))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Values(results.SelectMany(r => r.ImportObjects).Single(), "sourceOfAuthority"), Is.EqualTo(new[] { "onPremisesDirectory" }));
            Assert.That(results.First().WarningMessage, Does.Contain("Source of Authority"));
            Assert.That(results.First().WarningMessage, Does.Contain("User-OnPremisesSyncBehavior.ReadWrite.All"));
        }
    }

    [Test]
    public async Task FullImport_TakesADeltaBaselineBeforeReadingEachObjectTypeAsync()
    {
        Tenant.AddUser("u1", "Ada");
        Tenant.AddGroup("g1", "Finance");

        await RunAsync(ConnectedSystem(Users("displayName"), Groups("displayName", "members")));

        // The cloud-managed query shares the list's path, so it is told apart by its filter.
        var paths = Tenant.GraphRequests.Where(r => Query(r, "$filter") == null).Select(r => r.RequestUri!.AbsolutePath).ToList();
        var userBaseline = Tenant.GraphRequests.Single(r => r.RequestUri!.AbsolutePath == "/v1.0/users/delta");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(paths.IndexOf("/v1.0/users/delta"), Is.LessThan(paths.IndexOf("/v1.0/users")));
            Assert.That(paths.IndexOf("/v1.0/groups/delta"), Is.LessThan(paths.IndexOf("/v1.0/groups")));
            Assert.That(Query(userBaseline, "$deltatoken"), Is.EqualTo("latest"));
            Assert.That(Query(userBaseline, "$select"), Does.Contain("displayName"));
            Assert.That(Query(Tenant.GraphRequests.Single(r => r.RequestUri!.AbsolutePath == "/v1.0/groups/delta"), "$select"), Does.Contain("members"));
        }
    }

    [Test]
    public async Task FullImport_LastPage_PersistsBothDeltaLinksAndEarlierPagesPersistNothingAsync()
    {
        for (var i = 1; i <= 3; i++)
            Tenant.AddUser($"u{i}", $"User {i}");
        Tenant.AddGroup("g1", "Finance");

        var results = await RunAsync(ConnectedSystem(Users("displayName"), Groups("displayName")), pageSize: 2);

        var state = GraphImportState.Read(results.Last().PersistedConnectorData, Logger);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Take(results.Count - 1).Select(r => r.PersistedConnectorData), Is.All.Null);
            Assert.That(state, Is.Not.Null);
            Assert.That(state!.DeltaLinks["user"], Does.Contain("baseline-users"));
            Assert.That(state.DeltaLinks["group"], Does.Contain("baseline-groups"));
        }
    }

    [Test]
    public async Task FullImport_EveryPage_ReadsEveryObjectAsync()
    {
        Tenant.AddUser("u1", "Ada");
        Tenant.AddUser("u2", "Grace");

        var results = await RunAsync(ConnectedSystem(Users("displayName")), pageSize: 1);

        Assert.That(results.Select(r => r.CompleteRead), Is.All.True);
    }

    [Test]
    public async Task FullImport_UnselectedObjectType_IsNotReadAsync()
    {
        Tenant.AddUser("u1", "Ada");
        Tenant.AddGroup("g1", "Finance");

        var objects = await ImportAsync(Users("displayName"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(objects.Select(o => o.ObjectType), Is.All.EqualTo("user"));
            Assert.That(Tenant.GraphRequests.Select(r => r.RequestUri!.AbsolutePath), Has.None.StartsWith("/v1.0/groups"));
        }
    }

    [Test]
    public async Task FullImport_DirectoryExtension_IsReadByItsOwnNameAsync()
    {
        Tenant.AddUser("u1", "Ada", u => u["extension_0c1d_employeeNumber"] = "E123");
        var users = Users("displayName");
        users.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Name = "extension_0c1d_employeeNumber", Type = AttributeDataType.Text, Selected = true });

        var objects = await ImportAsync(users);

        Assert.That(Values(objects.Single(), "extension_0c1d_employeeNumber"), Is.EqualTo(new[] { "E123" }));
    }

    [Test]
    public void GetPhases_FullImport_DeclaresPreparingAndFetching()
    {
        var phases = new GraphConnector().GetPhases(ConnectedSystem(Users("displayName")), new ConnectedSystemRunProfile { Name = "Full Import", RunType = ConnectedSystemRunType.FullImport });

        Assert.That(phases.Select(p => p.Key), Is.EqualTo(new[] { GraphConnectorPhases.Prepare, GraphConnectorPhases.Fetch }));
    }
}
