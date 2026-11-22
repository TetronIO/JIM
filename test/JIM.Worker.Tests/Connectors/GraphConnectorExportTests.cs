// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text.Json.Nodes;
using JIM.Connectors.Graph;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The Microsoft Graph Connector's export: Pending Exports written through <c>$batch</c>, every step safe to send again,
/// since JIM sends a failed export again whole.
/// </summary>
[TestFixture]
public class GraphConnectorExportTests
{
    private FakeGraphTenant _tenant = null!;
    private static readonly ConnectedSystemObjectType UserType = new() { Id = 1, Name = "user", Selected = true };
    private static readonly ConnectedSystemObjectType GroupType = new() { Id = 2, Name = "group", Selected = true };

    [SetUp]
    public void SetUp()
    {
        _tenant = new FakeGraphTenant();
    }

    #region Create

    [Test]
    public async Task Create_User_PostsItsAttributesWithAGeneratedPasswordAndReturnsItsIdAsync()
    {
        _tenant.NextIds.Enqueue("new-user");

        var result = (await ExportAsync(NewAda())).Single();

        var created = _tenant.User("new-user")!;
        var request = WriteRequests().Single(r => r["method"]!.GetValue<string>() == "POST");
        var password = request["body"]!["passwordProfile"]!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.ExternalId, Is.EqualTo("new-user"));
            Assert.That(result.SetsAccountEnabledState, Is.True);
            Assert.That((string?)created["displayName"], Is.EqualTo("Ada Lovelace"));
            Assert.That((string?)created["userPrincipalName"], Is.EqualTo("ada@contoso.com"));
            Assert.That(created["accountEnabled"]!.GetValue<bool>(), Is.True);
            Assert.That(password["password"]!.GetValue<string>(), Has.Length.GreaterThanOrEqualTo(16));
            Assert.That(password["forceChangePasswordNextSignIn"]!.GetValue<bool>(), Is.True);
        }
    }

    [Test]
    public async Task Create_TwoUsers_GetDifferentPasswordsAsync()
    {
        await ExportAsync(NewAda(), Create(UserType, Set("userPrincipalName", "grace@contoso.com"), Set("displayName", "Grace"), Set("mailNickname", "grace"), Set("accountEnabled", true)));

        var passwords = WriteRequests().Select(r => r["body"]!["passwordProfile"]!["password"]!.GetValue<string>()).ToList();
        Assert.That(passwords.Distinct().Count(), Is.EqualTo(2));
    }

    [Test]
    public void GeneratedPassword_MeetsEntraIdComplexity()
    {
        for (var i = 0; i < 200; i++)
        {
            var password = GraphPasswordGenerator.Generate();
            var categories = new[] { password.Any(char.IsUpper), password.Any(char.IsLower), password.Any(char.IsDigit), password.Any(c => !char.IsLetterOrDigit(c)) };
            Assert.That(categories.Count(c => c), Is.EqualTo(4), "Every category is used, so Entra ID's three-of-four rule always holds.");
        }
    }

    [Test]
    public async Task Create_UserWithExtensionAttribute_NestsItUnderOnPremisesExtensionAttributesAsync()
    {
        _tenant.NextIds.Enqueue("new-user");

        await ExportAsync(NewAda(Set("extensionAttribute3", "E123")));

        Assert.That((string?)_tenant.User("new-user")!["onPremisesExtensionAttributes"]!["extensionAttribute3"], Is.EqualTo("E123"));
    }

    [Test]
    public async Task Create_UserWithManager_SetsTheManagerOnceTheUserExistsAsync()
    {
        _tenant.AddUser("boss", "Grace");
        _tenant.NextIds.Enqueue("new-user");

        var result = (await ExportAsync(NewAda(Set("manager", "boss")))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That((string?)_tenant.User("new-user")!["managerId"], Is.EqualTo("boss"));
            Assert.That(WriteRequests().Select(r => r["method"]!.GetValue<string>()), Is.EqualTo(new[] { "POST", "PUT" }));
        }
    }

    [Test]
    public async Task Create_UserPrincipalNameTaken_FailsAsAValueAlreadyInUseNamingTheAttributeAsync()
    {
        _tenant.AddUser("u1", "Ada", u => u["userPrincipalName"] = "ada@contoso.com");

        var result = (await ExportAsync(NewAda())).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.UniqueValueAlreadyInUse));
            Assert.That(result.RejectedAttributeName, Is.EqualTo("userPrincipalName"));
        }
    }

    [Test]
    public async Task Create_GroupWithMoreThanTwentyMembers_BindsTwentyAndAddsTheRestAfterwardsAsync()
    {
        for (var i = 1; i <= 25; i++)
            _tenant.AddUser($"u{i}", $"User {i}");
        _tenant.NextIds.Enqueue("new-group");

        var result = (await ExportAsync(Create(GroupType, [.. NewGroupAttributes(), .. Enumerable.Range(1, 25).Select(i => Add("members", $"u{i}"))]))).Single();

        var create = WriteRequests().Single(r => r["method"]!.GetValue<string>() == "POST");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.ExternalId, Is.EqualTo("new-group"));
            Assert.That(create["body"]!["members@odata.bind"]!.AsArray(), Has.Count.EqualTo(20));
            Assert.That(create["body"]!["members@odata.bind"]![0]!.GetValue<string>(), Is.EqualTo("https://graph.microsoft.com/v1.0/directoryObjects/u1"));
            Assert.That(_tenant.Members["new-group"].Select(m => m.Id), Is.EquivalentTo(Enumerable.Range(1, 25).Select(i => $"u{i}")));
        }
    }

    [Test]
    public async Task Create_FollowUpFails_StillReturnsTheNewIdSoTheCreateIsNeverSentAgainAsync()
    {
        // Failing the export would have JIM send the create again, making a second object. The member left unwritten is
        // found by the confirming import, which stages it again.
        for (var i = 1; i <= 21; i++)
            _tenant.AddUser($"u{i}", $"User {i}");
        _tenant.NextIds.Enqueue("new-group");
        _tenant.WriteInterceptor = (method, path, _) => method == HttpMethod.Patch && path == "groups/new-group"
            ? (HttpStatusCode.Forbidden, "Authorization_RequestDenied", "Insufficient privileges to complete the operation.")
            : null;

        var result = (await ExportAsync(Create(GroupType, [.. NewGroupAttributes(), .. Enumerable.Range(1, 21).Select(i => Add("members", $"u{i}"))]))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.ExternalId, Is.EqualTo("new-group"));
        }
    }

    [Test]
    public async Task Create_GroupBindingAMemberNotYetReplicated_TriesAgainAndCreatesItWithTheMemberAsync()
    {
        // Found against a real tenant: a group whose members are groups the same export created moments earlier was
        // refused with 404, since Graph had not yet replicated them.
        _tenant.AddGroup("engineering", "Engineering");
        _tenant.NotYetReferenceable["engineering"] = 2;
        _tenant.NextIds.Enqueue("all-departments");

        var result = (await ExportAsync(Create(GroupType, [.. NewGroupAttributes(), Add("members", "engineering")]))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.ExternalId, Is.EqualTo("all-departments"));
            Assert.That(_tenant.Members["all-departments"].Select(m => m.Id), Is.EqualTo(new[] { "engineering" }));
            Assert.That(_tenant.Groups, Has.Count.EqualTo(2), "One group created, however many times the create was sent.");
        }
    }

    [Test]
    public async Task Create_GroupBindingAMemberGraphNeverFinds_FailsAsAMissingDependencyForJimToSendAgainAsync()
    {
        var result = (await ExportAsync(Create(GroupType, [.. NewGroupAttributes(), Add("members", "never-created")]))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.MissingDependency));
            Assert.That(_tenant.Groups, Is.Empty);
        }
    }

    [Test]
    public async Task Create_UserWhoseManagerIsNotYetReplicated_SetsTheManagerOnceGraphFindsThemAsync()
    {
        _tenant.AddUser("boss", "Grace");
        _tenant.NotYetReferenceable["boss"] = 2;
        _tenant.NextIds.Enqueue("new-user");

        var result = (await ExportAsync(NewAda(Set("manager", "boss")))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That((string?)_tenant.User("new-user")!["managerId"], Is.EqualTo("boss"));
        }
    }

    [Test]
    public async Task Update_MemberNotYetReplicated_TriesAgainAndAddsItAsync()
    {
        _tenant.AddUser("u1", "u1");
        _tenant.NotYetReferenceable["u1"] = 2;
        _tenant.AddGroup("g1", "Finance");

        var result = (await ExportAsync(Against("g1", GroupType, PendingExportChangeType.Update, Add("members", "u1")))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(_tenant.Members["g1"].Select(m => m.Id), Is.EqualTo(new[] { "u1" }));
        }
    }

    [Test]
    public async Task Create_TwentyFiveUsers_TravelInTwoBatchesAsync()
    {
        var exports = Enumerable.Range(1, 25).Select(i => Create(UserType,
            Set("userPrincipalName", $"user{i}@contoso.com"), Set("displayName", $"User {i}"), Set("mailNickname", $"user{i}"), Set("accountEnabled", true))).ToArray();

        var results = await ExportAsync(exports);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.Success), Is.All.True);
            Assert.That(_tenant.Handler.Requests.Count(r => r.RequestUri!.AbsolutePath == "/v1.0/$batch"), Is.EqualTo(2));
            Assert.That(_tenant.Users, Has.Count.EqualTo(25));
        }
    }

    #endregion

    #region Update

    [Test]
    public async Task Update_PatchesOnlyTheChangedAttributesAndClearsANullOneAsync()
    {
        _tenant.AddUser("u1", "Ada", u => { u["jobTitle"] = "Analyst"; u["department"] = "Maths"; u["officeLocation"] = "B1"; });

        var result = (await ExportAsync(Against("u1", UserType, PendingExportChangeType.Update, Set("jobTitle", "Engineer"), Set("department", null)))).Single();

        var patch = WriteRequests().Single()["body"]!.AsObject();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(patch.Select(p => p.Key), Is.EquivalentTo(new[] { "jobTitle", "department" }));
            Assert.That((string?)_tenant.User("u1")!["jobTitle"], Is.EqualTo("Engineer"));
            Assert.That(_tenant.User("u1")!["department"], Is.Null);
            Assert.That((string?)_tenant.User("u1")!["officeLocation"], Is.EqualTo("B1"));
        }
    }

    [Test]
    public async Task Update_MultiValuedAttribute_SendsTheWholeSetSinceGraphReplacesItAsync()
    {
        _tenant.AddUser("u1", "Ada", u => u["otherMails"] = new JsonArray("a@x.com", "b@x.com"));
        var export = Against("u1", UserType, PendingExportChangeType.Update, Add("otherMails", "c@x.com"), Remove("otherMails", "a@x.com"));
        Hold(export, "otherMails", "a@x.com", "b@x.com");

        await ExportAsync(export);

        Assert.That(_tenant.User("u1")!["otherMails"]!.AsArray().Select(v => v!.GetValue<string>()), Is.EquivalentTo(new[] { "b@x.com", "c@x.com" }));
    }

    [Test]
    public async Task Update_Membership_AddsAndRemovesMembersAsync()
    {
        foreach (var id in new[] { "u1", "u2", "u3", "u4" })
            _tenant.AddUser(id, id);
        _tenant.AddGroup("g1", "Finance");
        _tenant.Members["g1"] = [("user", "u1"), ("user", "u2")];

        var result = (await ExportAsync(Against("g1", GroupType, PendingExportChangeType.Update, Add("members", "u3"), Add("members", "u4"), Remove("members", "u1")))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(_tenant.Members["g1"].Select(m => m.Id), Is.EquivalentTo(new[] { "u2", "u3", "u4" }));
        }
    }

    [Test]
    public async Task Update_MemberAlreadyPresent_CountsAsAddedAsync()
    {
        // A failed export is sent again whole, so a member it added the first time is already there.
        foreach (var id in new[] { "u1", "u2" })
            _tenant.AddUser(id, id);
        _tenant.AddGroup("g1", "Finance");
        _tenant.Members["g1"] = [("user", "u1")];

        var result = (await ExportAsync(Against("g1", GroupType, PendingExportChangeType.Update, Add("members", "u1"), Add("members", "u2")))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(_tenant.Members["g1"].Select(m => m.Id), Is.EquivalentTo(new[] { "u1", "u2" }));
        }
    }

    [Test]
    public async Task Update_MemberThatDoesNotExistYet_FailsAsAMissingDependencyAsync()
    {
        _tenant.AddUser("u1", "u1");
        _tenant.AddGroup("g1", "Finance");

        var result = (await ExportAsync(Against("g1", GroupType, PendingExportChangeType.Update, Add("members", "u1"), Add("members", "not-replicated-yet")))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.MissingDependency));
            Assert.That(_tenant.Members["g1"].Select(m => m.Id), Is.EqualTo(new[] { "u1" }), "The members that exist are still added.");
        }
    }

    [Test]
    public async Task Update_RemovingAMemberAlreadyGone_CountsAsRemovedAsync()
    {
        _tenant.AddGroup("g1", "Finance");

        var result = (await ExportAsync(Against("g1", GroupType, PendingExportChangeType.Update, Remove("members", "u1")))).Single();

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public async Task Update_RemoveAllMembers_RemovesEveryMemberGraphHoldsAsync()
    {
        foreach (var id in new[] { "u1", "u2" })
            _tenant.AddUser(id, id);
        _tenant.AddGroup("g1", "Finance");
        _tenant.Members["g1"] = [("user", "u1"), ("user", "u2")];

        var result = (await ExportAsync(Against("g1", GroupType, PendingExportChangeType.Update,
            new PendingExportAttributeValueChange { Attribute = Attribute("members", GroupType), ChangeType = PendingExportAttributeChangeType.RemoveAll }))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(_tenant.Members["g1"], Is.Empty);
        }
    }

    [Test]
    public async Task Update_ManagerChangedAndRemoved_SetsAndRemovesTheReferenceAsync()
    {
        _tenant.AddUser("boss", "Grace");
        _tenant.AddUser("u1", "Ada");
        _tenant.AddUser("u2", "Alan", u => u["managerId"] = "boss");

        var results = await ExportAsync(
            Against("u1", UserType, PendingExportChangeType.Update, Set("manager", "boss")),
            Against("u2", UserType, PendingExportChangeType.Update, Set("manager", null)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.Success), Is.All.True);
            Assert.That((string?)_tenant.User("u1")!["managerId"], Is.EqualTo("boss"));
            Assert.That(_tenant.User("u2")!["managerId"], Is.Null);
        }
    }

    [Test]
    public async Task Update_ManagerRemovedWhenThereIsNone_CountsAsRemovedAsync()
    {
        _tenant.AddUser("u1", "Ada");

        var result = (await ExportAsync(Against("u1", UserType, PendingExportChangeType.Update, Set("manager", null)))).Single();

        Assert.That(result.Success, Is.True, result.ErrorMessage);
    }

    [Test]
    public async Task Update_SynchronisedObject_IsRejectedNamingSourceOfAuthorityAsync()
    {
        _tenant.AddUser("u1", "Ada", u => u["onPremisesSyncEnabled"] = true);

        var result = (await ExportAsync(Against("u1", UserType, PendingExportChangeType.Update, Set("jobTitle", "Engineer")))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.RejectedByConnectedSystem));
            Assert.That(result.ErrorMessage, Does.Contain("sourceOfAuthority"));
        }
    }

    [Test]
    public async Task Update_Refused_IsAccessDeniedAsync()
    {
        _tenant.AddUser("u1", "Ada");
        _tenant.WriteInterceptor = (_, _, _) => (HttpStatusCode.Forbidden, "Authorization_RequestDenied", "Insufficient privileges to complete the operation.");

        var result = (await ExportAsync(Against("u1", UserType, PendingExportChangeType.Update, Set("jobTitle", "Engineer")))).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.AccessDenied));
            Assert.That(result.ErrorMessage, Does.Contain("Insufficient privileges"));
        }
    }

    [Test]
    public async Task Update_ObjectWithoutAnExternalId_FailsWithoutSendingAnythingAsync()
    {
        var export = new PendingExport { ChangeType = PendingExportChangeType.Update, AttributeValueChanges = new([Set("jobTitle", "Engineer")]) };

        var result = (await ExportAsync(export)).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(_tenant.Handler.Requests.Count(r => r.RequestUri!.AbsolutePath == "/v1.0/$batch"), Is.Zero);
        }
    }

    #endregion

    #region Delete

    [Test]
    public async Task Delete_RemovesTheObjectAndOneAlreadyGoneCountsAsDeletedAsync()
    {
        _tenant.AddUser("u1", "Ada");

        var results = await ExportAsync(
            Against("u1", UserType, PendingExportChangeType.Delete),
            Against("gone", UserType, PendingExportChangeType.Delete));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.Success), Is.All.True);
            Assert.That(_tenant.User("u1"), Is.Null);
        }
    }

    [Test]
    public async Task Delete_WithDisableBehaviour_DisablesAUserAndDeletesAGroupAsync()
    {
        _tenant.AddUser("u1", "Ada", u => u["accountEnabled"] = true);
        _tenant.AddGroup("g1", "Finance");

        var results = await ExportAsync(GraphConnectorConstants.DeleteBehaviourDisable,
            Against("u1", UserType, PendingExportChangeType.Delete),
            Against("g1", GroupType, PendingExportChangeType.Delete));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.Success), Is.All.True);
            Assert.That(_tenant.User("u1")!["accountEnabled"]!.GetValue<bool>(), Is.False);
            Assert.That(results[0].SetsAccountEnabledState, Is.True);
            Assert.That(_tenant.Group("g1"), Is.Null);
        }
    }

    [Test]
    public void Settings_DeleteBehaviour_DefaultsToDeleteInTheExportCategory()
    {
        var setting = new GraphConnector().GetSettings().Single(s => s.Name == GraphConnectorConstants.SettingDeleteBehaviour);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setting.DropDownValues, Is.EqualTo(new[] { GraphConnectorConstants.DeleteBehaviourDelete, GraphConnectorConstants.DeleteBehaviourDisable }));
            Assert.That(setting.DefaultStringValue, Is.EqualTo(GraphConnectorConstants.DeleteBehaviourDelete));
            Assert.That(setting.Category, Is.EqualTo(ConnectedSystemSettingCategory.Export));
        }
    }

    #endregion

    [Test]
    public void ExportAsync_BeforeOpeningTheConnection_Throws()
    {
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new GraphConnector().ExportAsync([NewAda()], CancellationToken.None, new RecordingConnectorProgress()));
    }

    [Test]
    public void ExportAsync_CancelledBeforeAnythingIsSent_ThrowsAndSendsNothing()
    {
        var connector = new StubbedTransportGraphConnector(_tenant.Handler);
        connector.OpenExportConnection(Settings(null), persistedConnectorData: null);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () => await connector.ExportAsync([NewAda()], cancelled.Token, new RecordingConnectorProgress()));
        Assert.That(_tenant.Handler.Requests.Count(r => r.RequestUri!.AbsolutePath == "/v1.0/$batch"), Is.Zero);
        connector.CloseExportConnection();
    }

    private Task<List<ConnectedSystemExportResult>> ExportAsync(params PendingExport[] exports) => ExportAsync(null, exports);

    private async Task<List<ConnectedSystemExportResult>> ExportAsync(string? deleteBehaviour, params PendingExport[] exports)
    {
        var connector = new StubbedTransportGraphConnector(_tenant.Handler);
        connector.OpenExportConnection(Settings(deleteBehaviour), persistedConnectorData: null);
        try
        {
            var results = await connector.ExportAsync(exports, CancellationToken.None, new RecordingConnectorProgress());
            Assert.That(results, Has.Count.EqualTo(exports.Length), "One result per Pending Export.");
            return results;
        }
        finally
        {
            connector.CloseExportConnection();
        }
    }

    /// <summary>Every write sent inside a <c>$batch</c>, in the order sent.</summary>
    private List<JsonNode> WriteRequests() => _tenant.Handler.Requests
        .Where(r => r.RequestUri!.AbsolutePath == "/v1.0/$batch")
        .SelectMany(r => JsonNode.Parse(r.Body!)!["requests"]!.AsArray())
        .Where(r => r!["method"]!.GetValue<string>() != "GET")
        .Select(r => r!)
        .ToList();

    private static List<ConnectedSystemSettingValue> Settings(string? deleteBehaviour)
    {
        var settings = new List<ConnectedSystemSettingValue>
        {
            new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingTenantId }, StringValue = "7a1f0c2e-3b4d-4e5f-8a9b-0c1d2e3f4a5b" },
            new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingClientId }, StringValue = "1c2d3e4f-5a6b-4c7d-8e9f-a0b1c2d3e4f5" },
            new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingAuthenticationMethod }, StringValue = GraphConnectorConstants.AuthMethodClientSecret },
            new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingClientSecret }, StringEncryptedValue = "client-secret-value" }
        };
        if (deleteBehaviour != null)
            settings.Add(new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingDeleteBehaviour }, StringValue = deleteBehaviour });
        return settings;
    }

    private static PendingExport NewAda(params PendingExportAttributeValueChange[] extra) => Create(UserType,
        [Set("userPrincipalName", "ada@contoso.com"), Set("displayName", "Ada Lovelace"), Set("mailNickname", "ada"), Set("accountEnabled", true), .. extra]);

    private static PendingExportAttributeValueChange[] NewGroupAttributes() =>
        [Set("displayName", "Finance", GroupType), Set("mailNickname", "finance", GroupType), Set("mailEnabled", false, GroupType), Set("securityEnabled", true, GroupType)];

    private static PendingExport Create(ConnectedSystemObjectType objectType, params PendingExportAttributeValueChange[] changes)
    {
        foreach (var change in changes)
            change.Attribute.ConnectedSystemObjectType = objectType;
        return new PendingExport { ChangeType = PendingExportChangeType.Create, AttributeValueChanges = new(changes) };
    }

    /// <summary>A Pending Export against an object Entra ID already holds, whose External ID is its Graph id.</summary>
    private static PendingExport Against(string id, ConnectedSystemObjectType objectType, PendingExportChangeType changeType, params PendingExportAttributeValueChange[] changes)
    {
        var idAttribute = Attribute("id", objectType);
        idAttribute.Id = 99;
        idAttribute.IsExternalId = true;
        foreach (var change in changes)
            change.Attribute.ConnectedSystemObjectType = objectType;

        return new PendingExport
        {
            ChangeType = changeType,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Type = objectType,
                TypeId = objectType.Id,
                ExternalIdAttributeId = idAttribute.Id,
                AttributeValues = [new ConnectedSystemObjectAttributeValue { Attribute = idAttribute, AttributeId = idAttribute.Id, StringValue = id }]
            },
            AttributeValueChanges = new(changes)
        };
    }

    /// <summary>Gives the Connected System Object the values it holds of a multi-valued text attribute.</summary>
    private static void Hold(PendingExport export, string attributeName, params string[] values)
    {
        var attribute = Attribute(attributeName, export.ConnectedSystemObject!.Type);
        attribute.Id = 50;
        foreach (var value in values)
            export.ConnectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = attribute, AttributeId = attribute.Id, StringValue = value });
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(string name, ConnectedSystemObjectType? objectType)
    {
        var catalogue = GraphSchemaCatalogue.UserAttributes.Concat(GraphSchemaCatalogue.GroupAttributes).FirstOrDefault(a => a.Name == name);
        return new ConnectedSystemObjectTypeAttribute
        {
            Name = name,
            Type = catalogue?.Type ?? AttributeDataType.Text,
            AttributePlurality = catalogue?.Plurality ?? AttributePlurality.SingleValued,
            ConnectedSystemObjectType = objectType!
        };
    }

    private static PendingExportAttributeValueChange Set(string name, object? value, ConnectedSystemObjectType? objectType = null)
    {
        var change = new PendingExportAttributeValueChange { Attribute = Attribute(name, objectType ?? UserType), ChangeType = PendingExportAttributeChangeType.Update };
        switch (value)
        {
            case bool flag: change.BoolValue = flag; break;
            case string text: change.StringValue = text; break;
        }

        return change;
    }

    private static PendingExportAttributeValueChange Add(string name, string value) =>
        new() { Attribute = Attribute(name, null), StringValue = value, ChangeType = PendingExportAttributeChangeType.Add };

    private static PendingExportAttributeValueChange Remove(string name, string value) =>
        new() { Attribute = Attribute(name, null), StringValue = value, ChangeType = PendingExportAttributeChangeType.Remove };
}
