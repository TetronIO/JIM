// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Workflow.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Workflow.Tests.Scenarios.Sync;

/// <summary>
/// GAL synchronisation (#2021) puts both people and mail-enabled groups from one forest into the other forest's address
/// book as mail contacts. There is no Contact Metaverse Object Type: a person and a group are each one identity, and two
/// export rules, one from each Metaverse Object Type, provision into the same <c>contact</c> Object Type, each scoped to
/// the identities with an object in the source forest.
/// </summary>
[TestFixture]
public class GalContactProvisioningWorkflowTests
{
    private WorkflowTestHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new WorkflowTestHarness();

    [TearDown]
    public void TearDown()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
            _harness.PrintSnapshotSummaries();
        _harness?.Dispose();
    }

    [Test]
    public async Task FullSync_PeopleAndGroupsFromTheSourceForest_ProvisionsAContactForEachAsync()
    {
        await SetUpAsync();
        _harness.GetConnector("PANOPLY").QueueImportObjects(
        [
            SourceObject("User", "sAMAccountName", "jane.smith", "Jane Smith"),
            SourceObject("User", "sAMAccountName", "raj.patel", "Raj Patel"),
            SourceObject("Group", "cn", "Finance Team", "Finance Team")
        ]);

        await _harness.ExecuteFullImportAsync("PANOPLY");
        await _harness.ExecuteFullSyncAsync("PANOPLY");
        var after = await _harness.TakeSnapshotAsync("After Full Sync");

        var displayNameId = _harness.GetObjectType("RESURGAM", "contact").Attributes.First(a => a.Name == "displayName").Id;
        var contactsStaged = after.PendingExports
            .Where(pe => pe.ChangeType == PendingExportChangeType.Create)
            .Select(pe => pe.AttributeValueChanges.Single(c => c.AttributeId == displayNameId).StringValue)
            .ToList();
        Assert.That(contactsStaged, Is.EquivalentTo(new[] { "Jane Smith", "Raj Patel", "Finance Team" }),
            "each person and each group from PANOPLY becomes one mail contact in RESURGAM");
    }

    private async Task SetUpAsync()
    {
        await _harness.CreateConnectedSystemAsync("PANOPLY");
        await _harness.CreateObjectTypeAsync("PANOPLY", "User", t => t.WithGuidExternalId("objectGUID").WithStringAttribute("sAMAccountName").WithStringAttribute("displayName"));
        await _harness.CreateObjectTypeAsync("PANOPLY", "Group", t => t.WithGuidExternalId("objectGUID").WithStringAttribute("cn").WithStringAttribute("displayName"));
        await _harness.CreateConnectedSystemAsync("RESURGAM");
        await _harness.CreateObjectTypeAsync("RESURGAM", "contact", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("displayName"));

        var person = await _harness.CreateMetaverseObjectTypeAsync("Person", t => t.WithStringAttribute("personName"));
        var group = await _harness.CreateMetaverseObjectTypeAsync("Group", t => t.WithStringAttribute("groupName"));
        var personName = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "personName");
        var groupName = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "groupName");

        var sourceUser = _harness.GetObjectType("PANOPLY", "User");
        var sourceGroup = _harness.GetObjectType("PANOPLY", "Group");
        var contact = _harness.GetObjectType("RESURGAM", "contact");

        await _harness.CreateSyncRuleAsync("PANOPLY Users", "PANOPLY", "User", person, SyncRuleDirection.Import, r => r
            .WithProjection()
            .WithAttributeFlow(personName, sourceUser.Attributes.First(a => a.Name == "displayName")));
        await _harness.CreateSyncRuleAsync("PANOPLY Groups", "PANOPLY", "Group", group, SyncRuleDirection.Import, r => r
            .WithProjection()
            .WithAttributeFlow(groupName, sourceGroup.Attributes.First(a => a.Name == "displayName")));

        var peopleContacts = await _harness.CreateSyncRuleAsync("GAL Contacts: people", "RESURGAM", "contact", person, SyncRuleDirection.Export, r => r
            .WithProvisioning()
            .WithAttributeFlow(personName, contact.Attributes.First(a => a.Name == "displayName"))
            .WithExpressionFlow("\"CN=\" + mv[\"personName\"] + \",OU=GALSync,DC=resurgam,DC=local\"",
                contact.Attributes.First(a => a.Name == "distinguishedName")));
        var groupContacts = await _harness.CreateSyncRuleAsync("GAL Contacts: groups", "RESURGAM", "contact", group, SyncRuleDirection.Export, r => r
            .WithProvisioning()
            .WithAttributeFlow(groupName, contact.Attributes.First(a => a.Name == "displayName"))
            .WithExpressionFlow("\"CN=\" + mv[\"groupName\"] + \",OU=GALSync,DC=resurgam,DC=local\"",
                contact.Attributes.First(a => a.Name == "distinguishedName")));

        ScopeToHaving(peopleContacts, sourceUser);
        ScopeToHaving(groupContacts, sourceGroup);
        await _harness.DbContext.SaveChangesAsync();
    }

    private static void ScopeToHaving(SyncRule rule, ConnectedSystemObjectType sourceType) =>
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = [new SyncRuleScopingCriteria { ConnectedObjectType = sourceType, ConnectedObjectTypeId = sourceType.Id, ComparisonType = SearchComparisonType.HasValue }]
        });

    private static ConnectedSystemImportObject SourceObject(string objectType, string namingAttribute, string naming, string displayName) => new()
    {
        ChangeType = ObjectChangeType.NotSet,
        ObjectType = objectType,
        Attributes =
        [
            new() { Name = "objectGUID", GuidValues = [Guid.NewGuid()] },
            new() { Name = namingAttribute, StringValues = [naming] },
            new() { Name = "displayName", StringValues = [displayName] }
        ]
    };
}
