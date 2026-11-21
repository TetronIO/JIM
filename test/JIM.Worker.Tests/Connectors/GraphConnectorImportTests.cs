// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Web;
using JIM.Connectors.Graph;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class GraphConnectorImportTests
{
    private ILogger _logger = null!;
    private FakeGraphTenant _tenant = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
        _tenant = new FakeGraphTenant();
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();
    }

    [Test]
    public async Task FullImport_Users_AreStagedAsAddedWithTheirSelectedAttributesAsync()
    {
        _tenant.AddUser("u1", "Ada Lovelace", u => { u["jobTitle"] = "Analyst"; u["accountEnabled"] = true; u["department"] = "Unselected"; });

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
        _tenant.AddUser("u1", "Ada");

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
            _tenant.AddUser($"u{i}", $"User {i}");

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
        _tenant.AddUser("u1", "Ada");

        await RunAsync(ConnectedSystem(Users("displayName")), pageSize: 5000);

        Assert.That(Query(UserListRequest(), "$top"), Is.EqualTo("999"));
    }

    [Test]
    public async Task FullImport_Manager_IsExpandedAndStagedAsAReferenceAsync()
    {
        _tenant.AddUser("boss", "Grace");
        _tenant.AddUser("u1", "Ada", u => u["managerId"] = "boss");

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
        _tenant.MembersPageSize = 2;
        _tenant.AddGroup("g1", "Finance");
        _tenant.Members["g1"] = [("user", "u1"), ("user", "u2"), ("group", "g2"), ("device", "d1"), ("servicePrincipal", "sp1"), ("user", "u3")];

        var objects = await ImportAsync(Groups("displayName", "members"));

        var members = objects.Single().Attributes.Single(a => a.Name == "members");
        Assert.That(members.ReferenceValues, Is.EquivalentTo(new[] { "u1", "u2", "g2", "u3" }));
    }

    [Test]
    public async Task FullImport_GroupMembers_AreReadThroughBatchesOfTwentyAsync()
    {
        for (var i = 1; i <= 25; i++)
        {
            _tenant.AddGroup($"g{i}", $"Group {i}");
            _tenant.Members[$"g{i}"] = [("user", $"u{i}")];
        }

        var objects = await ImportAsync(Groups("displayName", "members"));

        var batches = _tenant.GraphRequests.Where(r => r.RequestUri!.AbsolutePath == "/v1.0/$batch").ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objects.Select(o => o.Attributes.Single(a => a.Name == "members").ReferenceValues.Single()),
                Is.EqualTo(Enumerable.Range(1, 25).Select(i => $"u{i}")));
            Assert.That(batches, Has.Count.EqualTo(2));
            Assert.That(_tenant.GraphRequests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/members", StringComparison.Ordinal)), Is.Zero,
                "No group's members fitted in more than one page, so nothing was read outside a batch.");
        }
    }

    [Test]
    public async Task FullImport_GroupWithNoMembers_HasNoMembersAttributeAsync()
    {
        _tenant.AddGroup("g1", "Empty");

        var objects = await ImportAsync(Groups("displayName", "members"));

        Assert.That(objects.Single().Attributes.Select(a => a.Name), Does.Not.Contain("members"));
    }

    [Test]
    public async Task FullImport_MembersCannotBeRead_ReportsTheAttributeSoJimKeepsTheMembershipItHoldsAsync()
    {
        // A group staged with some or none of its members would have JIM remove the rest. Reported as an unreadable
        // value instead, the attribute keeps what JIM already holds, and the group's other attributes still update.
        _tenant.AddGroup("g1", "Readable");
        _tenant.AddGroup("g2", "Unreadable");
        _tenant.Members["g1"] = [("user", "u1")];
        _tenant.MembersFailures["g2"] = HttpStatusCode.Forbidden;

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
    public async Task FullImport_Owners_AreStagedAsReferencesAsync()
    {
        _tenant.AddGroup("g1", "Finance");
        _tenant.Owners["g1"] = ["u1", "u2"];

        var objects = await ImportAsync(Groups("displayName", "owners"));

        Assert.That(objects.Single().Attributes.Single(a => a.Name == "owners").ReferenceValues, Is.EquivalentTo(new[] { "u1", "u2" }));
    }

    [Test]
    public async Task FullImport_SourceOfAuthority_IsDerivedForEveryObjectAsync()
    {
        _tenant.AddUser("cloud", "Cloud");
        _tenant.AddUser("synced", "Synced", u => u["onPremisesSyncEnabled"] = true);
        _tenant.AddUser("converted", "Converted", u => u["onPremisesSyncEnabled"] = true);
        _tenant.AddUser("guest", "Guest", u => u["userType"] = "Guest");
        _tenant.CloudManaged.Add("converted");

        var objects = await ImportAsync(Users("displayName"));

        Assert.That(objects.ToDictionary(o => Values(o, "id").Single(), o => Values(o, "sourceOfAuthority").Single()), Is.EqualTo(new Dictionary<string, string>
        {
            ["cloud"] = "cloud",
            ["synced"] = "onPremisesDirectory",
            ["converted"] = "cloud",
            ["guest"] = "externalEntraDirectory"
        }));
    }

    [Test]
    public async Task FullImport_CloudManagedQuery_UsesEventualConsistencyAndAsksOnlyForIdsAsync()
    {
        _tenant.AddUser("u1", "Ada");

        await ImportAsync(Users("displayName"));

        var request = _tenant.GraphRequests.Single(r => Query(r, "$filter")?.Contains("isCloudManaged", StringComparison.Ordinal) == true);
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
        _tenant.CloudManagedQueryStatus = refusal;
        _tenant.AddUser("synced", "Synced", u => u["onPremisesSyncEnabled"] = true);

        var results = await RunAsync(ConnectedSystem(Users("displayName")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Values(results.SelectMany(r => r.ImportObjects).Single(), "sourceOfAuthority"), Is.EqualTo(new[] { "onPremisesDirectory" }));
            Assert.That(results.First().WarningMessage, Does.Contain("cloud management"));
        }
    }

    [Test]
    public async Task FullImport_TakesADeltaBaselineBeforeReadingEachObjectTypeAsync()
    {
        _tenant.AddUser("u1", "Ada");
        _tenant.AddGroup("g1", "Finance");

        await RunAsync(ConnectedSystem(Users("displayName"), Groups("displayName", "members")));

        var paths = _tenant.GraphRequests.Select(r => r.RequestUri!.AbsolutePath).ToList();
        var userBaseline = _tenant.GraphRequests.Single(r => r.RequestUri!.AbsolutePath == "/v1.0/users/delta");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(paths.IndexOf("/v1.0/users/delta"), Is.LessThan(paths.IndexOf("/v1.0/users")));
            Assert.That(paths.IndexOf("/v1.0/groups/delta"), Is.LessThan(paths.IndexOf("/v1.0/groups")));
            Assert.That(Query(userBaseline, "$deltatoken"), Is.EqualTo("latest"));
            Assert.That(Query(userBaseline, "$select"), Does.Contain("displayName"));
            Assert.That(Query(_tenant.GraphRequests.Single(r => r.RequestUri!.AbsolutePath == "/v1.0/groups/delta"), "$select"), Does.Contain("members"));
        }
    }

    [Test]
    public async Task FullImport_LastPage_PersistsBothDeltaLinksAndEarlierPagesPersistNothingAsync()
    {
        for (var i = 1; i <= 3; i++)
            _tenant.AddUser($"u{i}", $"User {i}");
        _tenant.AddGroup("g1", "Finance");

        var results = await RunAsync(ConnectedSystem(Users("displayName"), Groups("displayName")), pageSize: 2);

        var state = GraphImportState.Read(results.Last().PersistedConnectorData, _logger);
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
        _tenant.AddUser("u1", "Ada");
        _tenant.AddUser("u2", "Grace");

        var results = await RunAsync(ConnectedSystem(Users("displayName")), pageSize: 1);

        Assert.That(results.Select(r => r.CompleteRead), Is.All.True);
    }

    [Test]
    public async Task FullImport_UnselectedObjectType_IsNotReadAsync()
    {
        _tenant.AddUser("u1", "Ada");
        _tenant.AddGroup("g1", "Finance");

        var objects = await ImportAsync(Users("displayName"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(objects.Select(o => o.ObjectType), Is.All.EqualTo("user"));
            Assert.That(_tenant.GraphRequests.Select(r => r.RequestUri!.AbsolutePath), Has.None.StartsWith("/v1.0/groups"));
        }
    }

    [Test]
    public async Task FullImport_DirectoryExtension_IsReadByItsOwnNameAsync()
    {
        _tenant.AddUser("u1", "Ada", u => u["extension_0c1d_employeeNumber"] = "E123");
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

    private async Task<List<ConnectedSystemImportObject>> ImportAsync(params ConnectedSystemObjectType[] objectTypes) =>
        (await RunAsync(ConnectedSystem(objectTypes))).SelectMany(r => r.ImportObjects).ToList();

    private async Task<List<ConnectedSystemImportResult>> RunAsync(ConnectedSystem connectedSystem, int pageSize = 100)
    {
        var connector = new StubbedTransportGraphConnector(_tenant.Handler);
        var runProfile = new ConnectedSystemRunProfile { Name = "Full Import", RunType = ConnectedSystemRunType.FullImport, PageSize = pageSize };
        connector.OpenImportConnection(connectedSystem.SettingValues, persistedConnectorData: null, _logger);
        var results = new List<ConnectedSystemImportResult>();
        var tokens = new List<ConnectedSystemPaginationToken>();
        try
        {
            for (var page = 0; page < 50; page++)
            {
                var result = await connector.ImportAsync(connectedSystem, runProfile, tokens, null, _logger, CancellationToken.None, new RecordingConnectorProgress());
                results.Add(result);
                if (result.PaginationTokens.Count == 0)
                    return results;
                tokens = result.PaginationTokens;
            }
        }
        finally
        {
            connector.CloseImportConnection();
        }

        Assert.Fail("The import did not finish within 50 pages.");
        return results;
    }

    private static ConnectedSystem ConnectedSystem(params ConnectedSystemObjectType[] objectTypes) => new()
    {
        Name = "Entra ID",
        ObjectTypes = objectTypes.ToList(),
        SettingValues =
        [
            new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingTenantId }, StringValue = "7a1f0c2e-3b4d-4e5f-8a9b-0c1d2e3f4a5b" },
            new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingClientId }, StringValue = "1c2d3e4f-5a6b-4c7d-8e9f-a0b1c2d3e4f5" },
            new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingAuthenticationMethod }, StringValue = GraphConnectorConstants.AuthMethodClientSecret },
            new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingClientSecret }, StringEncryptedValue = "client-secret-value" }
        ]
    };

    private static ConnectedSystemObjectType Users(params string[] selected) => ObjectType("user", GraphSchemaCatalogue.UserAttributes, selected);

    private static ConnectedSystemObjectType Groups(params string[] selected) => ObjectType("group", GraphSchemaCatalogue.GroupAttributes, selected);

    /// <summary>An Object Type as schema import leaves it: every catalogue attribute, with id, sourceOfAuthority and those named selected.</summary>
    private static ConnectedSystemObjectType ObjectType(string name, IReadOnlyList<GraphSchemaAttribute> catalogue, string[] selected) => new()
    {
        Name = name,
        Selected = true,
        Attributes = catalogue.Select(a => new ConnectedSystemObjectTypeAttribute
        {
            Name = a.Name,
            Type = a.Type,
            AttributePlurality = a.Plurality,
            Selected = a.Name is "id" or "sourceOfAuthority" || selected.Contains(a.Name),
            IsExternalId = a.Name == "id"
        }).ToList()
    };

    private StubHttpMessageHandler.RecordedRequest UserListRequest() =>
        _tenant.GraphRequests.First(r => r.RequestUri!.AbsolutePath == "/v1.0/users" && Query(r, "$filter") == null);

    private static string? Query(StubHttpMessageHandler.RecordedRequest request, string name) =>
        HttpUtility.ParseQueryString(request.RequestUri!.Query)[name];

    private static List<string> Values(ConnectedSystemImportObject importObject, string name) =>
        importObject.Attributes.SingleOrDefault(a => a.Name == name)?.StringValues ?? [];
}
