using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Workflow.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Workflow.Tests.Scenarios.Sync;

/// <summary>
/// Tests for full sync after initial import.
/// Verifies that full sync creates correct RPEIs for projections only,
/// without unexpected AttributeFlow entries.
///
/// Scenario: Initial import of users and groups, followed by full sync.
/// Expected behaviour:
/// - Full Import: N objects Added
/// - Full Sync: N objects Projected (no AttributeFlow)
///
/// This test was created to investigate an issue where full sync was showing
/// both Projected AND AttributeFlow RPEIs, when it should only show Projected.
/// </summary>
[TestFixture]
public class FullSyncAfterImportWorkflowTests
{
    private WorkflowTestHarness _harness = null!;

    // Reference users (consistent across test runs)
    private static readonly List<ReferenceUser> ReferenceUsers = new()
    {
        new("user1", "Alice Johnson", Guid.Parse("11111111-1111-1111-1111-111111111111")),
        new("user2", "Bob Smith", Guid.Parse("22222222-2222-2222-2222-222222222222")),
        new("user3", "Charlie Brown", Guid.Parse("33333333-3333-3333-3333-333333333333")),
        new("user4", "Diana Ross", Guid.Parse("44444444-4444-4444-4444-444444444444")),
        new("user5", "Eve Wilson", Guid.Parse("55555555-5555-5555-5555-555555555555")),
    };

    // Reference groups with initial members
    private static readonly List<ReferenceGroup> ReferenceGroups = new()
    {
        new("group1", "Engineering", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), new[] { "user1", "user2" }),
        new("group2", "Marketing", Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), new[] { "user3", "user4" }),
        new("group3", "All Staff", Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), new[] { "user1", "user2", "user3", "user4", "user5" }),
    };

    [SetUp]
    public void SetUp()
    {
        _harness = new WorkflowTestHarness();
    }

    [TearDown]
    public void TearDown()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
        {
            Console.WriteLine("=== SNAPSHOT DIAGNOSTICS ===");
            _harness.PrintSnapshotSummaries();
        }

        _harness?.Dispose();
    }

    /// <summary>
    /// Tests that full sync after initial import creates only Projected RPEIs for users,
    /// not AttributeFlow RPEIs.
    ///
    /// Expected:
    /// - Full Import: 5 Added
    /// - Full Sync: 5 Projected, 0 AttributeFlow
    /// </summary>
    [Test]
    public async Task FullSync_AfterInitialImport_UsersOnly_CreatesOnlyProjectedRpeisAsync()
    {
        // Arrange - Create source system with users only (no groups)
        await SetUpSourceSystemUsersOnlyAsync();

        // Queue all users for import
        var sourceConnector = _harness.GetConnector("Source");
        sourceConnector.QueueImportObjects(GenerateSourceUsers());

        // Step 1: Execute full import
        var importActivity = await _harness.ExecuteFullImportAsync("Source");

        Console.WriteLine($"Full Import - ObjectsToProcess: {importActivity.ObjectsToProcess}");
        Console.WriteLine($"Full Import - ObjectsProcessed: {importActivity.ObjectsProcessed}");
        Console.WriteLine($"Full Import - RPEIs: {importActivity.RunProfileExecutionItems?.Count ?? 0}");

        if (importActivity.RunProfileExecutionItems != null)
        {
            var importChangeTypes = importActivity.RunProfileExecutionItems
                .GroupBy(r => r.ObjectChangeType)
                .Select(g => $"{g.Key}: {g.Count()}")
                .ToList();
            Console.WriteLine($"Full Import - Change types: {string.Join(", ", importChangeTypes)}");
        }

        Assert.That(importActivity.RunProfileExecutionItems, Has.Count.EqualTo(ReferenceUsers.Count),
            $"Full import should create {ReferenceUsers.Count} RPEIs (one per user)");

        var importAdded = importActivity.RunProfileExecutionItems!
            .Count(r => r.ObjectChangeType == ObjectChangeType.Added);
        Assert.That(importAdded, Is.EqualTo(ReferenceUsers.Count),
            $"Full import should have {ReferenceUsers.Count} Added RPEIs");

        // Step 2: Execute full sync
        var syncActivity = await _harness.ExecuteFullSyncAsync("Source");

        Console.WriteLine($"Full Sync - ObjectsToProcess: {syncActivity.ObjectsToProcess}");
        Console.WriteLine($"Full Sync - ObjectsProcessed: {syncActivity.ObjectsProcessed}");
        Console.WriteLine($"Full Sync - RPEIs: {syncActivity.RunProfileExecutionItems?.Count ?? 0}");

        if (syncActivity.RunProfileExecutionItems != null)
        {
            var syncChangeTypes = syncActivity.RunProfileExecutionItems
                .GroupBy(r => r.ObjectChangeType)
                .Select(g => $"{g.Key}: {g.Count()}")
                .ToList();
            Console.WriteLine($"Full Sync - Change types: {string.Join(", ", syncChangeTypes)}");

            // Log individual RPEIs for debugging
            foreach (var rpei in syncActivity.RunProfileExecutionItems)
            {
                Console.WriteLine($"  RPEI: CSO={rpei.ConnectedSystemObjectId}, ChangeType={rpei.ObjectChangeType}");
            }
        }

        // CRITICAL ASSERTIONS
        Assert.That(syncActivity.RunProfileExecutionItems, Is.Not.Null.And.Not.Empty,
            "Full sync should create RPEIs");

        var projectedCount = syncActivity.RunProfileExecutionItems!
            .Count(r => r.ObjectChangeType == ObjectChangeType.Projected);
        var attributeFlowCount = syncActivity.RunProfileExecutionItems!
            .Count(r => r.ObjectChangeType == ObjectChangeType.AttributeFlow);

        Console.WriteLine($"Full Sync - Projected: {projectedCount}, AttributeFlow: {attributeFlowCount}");

        // CRITICAL: Full sync after initial import should ONLY have Projected RPEIs
        Assert.That(projectedCount, Is.EqualTo(ReferenceUsers.Count),
            $"Full sync should create {ReferenceUsers.Count} Projected RPEIs");

        // THIS IS THE BUG: We're seeing unexpected AttributeFlow RPEIs
        Assert.That(attributeFlowCount, Is.EqualTo(0),
            "Full sync after initial import should NOT have any AttributeFlow RPEIs - " +
            "attribute flow happens as part of projection, not as a separate operation");

        // Verify MVOs were created
        var mvoType = await _harness.DbContext.MetaverseObjectTypes.FirstAsync(t => t.Name == "Person");
        var mvos = await _harness.DbContext.MetaverseObjects
            .Where(m => m.Type!.Id == mvoType.Id)
            .ToListAsync();
        Assert.That(mvos, Has.Count.EqualTo(ReferenceUsers.Count),
            $"Should have {ReferenceUsers.Count} person MVOs");

        Console.WriteLine("=== Test Complete: Full sync with users only ===");
    }

    /// <summary>
    /// Tests full sync with users and groups (with membership references).
    /// This more closely matches the actual Scenario 8 with 131 objects.
    ///
    /// This test was created to investigate the issue where Full Sync shows:
    /// - 131 Projected + 31 AttributeFlow (incorrect)
    /// When it should show:
    /// - 131 Projected, 0 AttributeFlow (correct)
    /// </summary>
    [Test]
    public async Task FullSync_WithUsersAndGroups_CreatesOnlyProjectedRpeisAsync()
    {
        // Arrange - Create source and target systems with users and groups
        await SetUpEntitlementScenarioAsync();

        // Queue all users and groups for import
        var sourceConnector = _harness.GetConnector("Source");
        sourceConnector.QueueImportObjects(GenerateSourceUsers().Concat(GenerateSourceGroups()).ToList());

        var totalObjects = ReferenceUsers.Count + ReferenceGroups.Count;

        // Step 1: Execute full import
        var importActivity = await _harness.ExecuteFullImportAsync("Source");

        Console.WriteLine($"Full Import - ObjectsToProcess: {importActivity.ObjectsToProcess}");
        Console.WriteLine($"Full Import - ObjectsProcessed: {importActivity.ObjectsProcessed}");
        Console.WriteLine($"Full Import - RPEIs: {importActivity.RunProfileExecutionItems?.Count ?? 0}");

        if (importActivity.RunProfileExecutionItems != null)
        {
            var importChangeTypes = importActivity.RunProfileExecutionItems
                .GroupBy(r => r.ObjectChangeType)
                .Select(g => $"{g.Key}: {g.Count()}")
                .ToList();
            Console.WriteLine($"Full Import - Change types: {string.Join(", ", importChangeTypes)}");
        }

        Assert.That(importActivity.RunProfileExecutionItems, Has.Count.EqualTo(totalObjects),
            $"Full import should create {totalObjects} RPEIs");

        // Step 2: Execute full sync
        var syncActivity = await _harness.ExecuteFullSyncAsync("Source");

        Console.WriteLine($"Full Sync - ObjectsToProcess: {syncActivity.ObjectsToProcess}");
        Console.WriteLine($"Full Sync - ObjectsProcessed: {syncActivity.ObjectsProcessed}");
        Console.WriteLine($"Full Sync - RPEIs: {syncActivity.RunProfileExecutionItems?.Count ?? 0}");

        if (syncActivity.RunProfileExecutionItems != null)
        {
            var syncChangeTypes = syncActivity.RunProfileExecutionItems
                .GroupBy(r => r.ObjectChangeType)
                .Select(g => $"{g.Key}: {g.Count()}")
                .ToList();
            Console.WriteLine($"Full Sync - Change types: {string.Join(", ", syncChangeTypes)}");

            // Log individual RPEIs for debugging
            foreach (var rpei in syncActivity.RunProfileExecutionItems)
            {
                Console.WriteLine($"  RPEI: CSO={rpei.ConnectedSystemObjectId}, ChangeType={rpei.ObjectChangeType}");
            }
        }

        // CRITICAL ASSERTIONS
        var projectedCount = syncActivity.RunProfileExecutionItems!
            .Count(r => r.ObjectChangeType == ObjectChangeType.Projected);
        var attributeFlowCount = syncActivity.RunProfileExecutionItems!
            .Count(r => r.ObjectChangeType == ObjectChangeType.AttributeFlow);

        Console.WriteLine($"Full Sync - Projected: {projectedCount}, AttributeFlow: {attributeFlowCount}");

        // Full sync after initial import should ONLY have Projected RPEIs
        Assert.That(projectedCount, Is.EqualTo(totalObjects),
            $"Full sync should create {totalObjects} Projected RPEIs (one per object)");

        // THIS IS THE BUG: We're seeing unexpected AttributeFlow RPEIs
        // The issue seems to be related to groups with reference attributes
        Assert.That(attributeFlowCount, Is.EqualTo(0),
            "Full sync after initial import should NOT have any AttributeFlow RPEIs - " +
            "reference attribute flow during projection should not create separate RPEIs");

        Console.WriteLine("=== Test Complete: Full sync with users and groups ===");
    }

    #region Setup Helpers

    private async Task SetUpSourceSystemUsersOnlyAsync()
    {
        // Create Source system
        await _harness.CreateConnectedSystemAsync("Source");
        await _harness.CreateObjectTypeAsync("Source", "User", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn")
            .WithStringAttribute("displayName"));

        // Create MV type
        var personType = await _harness.CreateMetaverseObjectTypeAsync("Person", t => t
            .WithGuidAttribute("objectId")
            .WithStringAttribute("displayName")
            .WithStringAttribute("cn")
            .WithStringAttribute("Type"));

        // Get attributes for sync rules
        var sourceUserType = _harness.GetObjectType("Source", "User");
        var sourceUserCn = sourceUserType.Attributes.First(a => a.Name == "cn");
        var mvPersonCn = await _harness.DbContext.MetaverseAttributes
            .FirstAsync(a => a.Name == "cn" && a.MetaverseObjectTypes.Any(t => t.Name == "Person"));
        var mvType = await _harness.DbContext.MetaverseAttributes
            .FirstAsync(a => a.Name == "Type" && a.MetaverseObjectTypes.Any(t => t.Name == "Person"));

        // Create Source import sync rule
        await _harness.CreateSyncRuleAsync(
            "Source User Import",
            "Source",
            "User",
            personType,
            SyncRuleDirection.Import,
            r => r
                .WithProjection()
                .WithAttributeFlow(mvPersonCn, sourceUserCn)
                .WithExpressionFlow("\"PersonEntity\"", mvType));
    }

    private async Task SetUpEntitlementScenarioAsync()
    {
        // Create Source system (LDAP-like)
        await _harness.CreateConnectedSystemAsync("Source");
        await _harness.CreateObjectTypeAsync("Source", "User", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn")
            .WithStringAttribute("displayName"));

        await _harness.CreateObjectTypeAsync("Source", "Group", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn")
            .WithReferenceAttribute("member", isMultiValued: true));

        // Create Target system (LDAP-like)
        await _harness.CreateConnectedSystemAsync("Target");
        await _harness.CreateObjectTypeAsync("Target", "User", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn")
            .WithStringAttribute("displayName"));

        await _harness.CreateObjectTypeAsync("Target", "Group", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn")
            .WithReferenceAttribute("member", isMultiValued: true));

        // Create MV types
        var personType = await _harness.CreateMetaverseObjectTypeAsync("Person", t => t
            .WithGuidAttribute("objectId")
            .WithStringAttribute("displayName")
            .WithStringAttribute("cn")
            .WithStringAttribute("Type"));

        var groupType = await _harness.CreateMetaverseObjectTypeAsync("Group", t => t
            .WithGuidAttribute("objectId")
            .WithStringAttribute("displayName")
            .WithStringAttribute("cn")
            .WithReferenceAttribute("member", isMultiValued: true));

        // Get attributes for sync rules
        var sourceUserType = _harness.GetObjectType("Source", "User");
        var sourceGroupType = _harness.GetObjectType("Source", "Group");
        var targetUserType = _harness.GetObjectType("Target", "User");
        var targetGroupType = _harness.GetObjectType("Target", "Group");

        var sourceUserCn = sourceUserType.Attributes.First(a => a.Name == "cn");
        var sourceGroupCn = sourceGroupType.Attributes.First(a => a.Name == "cn");
        var sourceGroupMember = sourceGroupType.Attributes.First(a => a.Name == "member");

        var targetUserCn = targetUserType.Attributes.First(a => a.Name == "cn");
        var targetUserDn = targetUserType.Attributes.First(a => a.Name == "distinguishedName");
        var targetGroupCn = targetGroupType.Attributes.First(a => a.Name == "cn");
        var targetGroupDn = targetGroupType.Attributes.First(a => a.Name == "distinguishedName");
        var targetGroupMember = targetGroupType.Attributes.First(a => a.Name == "member");

        // Get MV attributes
        var mvPersonCn = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "cn" && a.MetaverseObjectTypes.Any(t => t.Name == "Person"));
        var mvGroupCn = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "cn" && a.MetaverseObjectTypes.Any(t => t.Name == "Group"));
        var mvGroupMember = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "member");
        var mvType = await _harness.DbContext.MetaverseAttributes
            .FirstAsync(a => a.Name == "Type" && a.MetaverseObjectTypes.Any(t => t.Name == "Person"));

        // Create Source import sync rules
        await _harness.CreateSyncRuleAsync(
            "Source User Import",
            "Source",
            "User",
            personType,
            SyncRuleDirection.Import,
            r => r
                .WithProjection()
                .WithAttributeFlow(mvPersonCn, sourceUserCn)
                .WithExpressionFlow("\"PersonEntity\"", mvType));

        await _harness.CreateSyncRuleAsync(
            "Source Group Import",
            "Source",
            "Group",
            groupType,
            SyncRuleDirection.Import,
            r => r
                .WithProjection()
                .WithAttributeFlow(mvGroupCn, sourceGroupCn)
                .WithAttributeFlow(mvGroupMember, sourceGroupMember));

        // Create Target export sync rules
        await _harness.CreateSyncRuleAsync(
            "Target User Export",
            "Target",
            "User",
            personType,
            SyncRuleDirection.Export,
            r => r
                .WithProvisioning()
                .WithAttributeFlow(mvPersonCn, targetUserCn)
                .WithExpressionFlow("\"CN=\" + mv[\"cn\"] + \",OU=Users,DC=test,DC=local\"", targetUserDn));

        await _harness.CreateSyncRuleAsync(
            "Target Group Export",
            "Target",
            "Group",
            groupType,
            SyncRuleDirection.Export,
            r => r
                .WithProvisioning()
                .WithAttributeFlow(mvGroupCn, targetGroupCn)
                .WithAttributeFlow(mvGroupMember, targetGroupMember)
                .WithExpressionFlow("\"CN=\" + mv[\"cn\"] + \",OU=Groups,DC=test,DC=local\"", targetGroupDn));
    }

    private List<ConnectedSystemImportObject> GenerateSourceUsers()
    {
        return ReferenceUsers.Select(user => new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = "objectGUID", GuidValues = new List<Guid> { user.ObjectGuid } },
                new() { Name = "distinguishedName", StringValues = new List<string> { $"CN={user.DisplayName},OU=Users,DC=test,DC=local" } },
                new() { Name = "cn", StringValues = new List<string> { user.DisplayName } },
                new() { Name = "displayName", StringValues = new List<string> { user.DisplayName } }
            }
        }).ToList();
    }

    private List<ConnectedSystemImportObject> GenerateSourceGroups()
    {
        return ReferenceGroups.Select(group =>
        {
            var memberDns = group.InitialMembers
                .Select(userId =>
                {
                    var user = ReferenceUsers.First(u => u.UserId == userId);
                    return $"CN={user.DisplayName},OU=Users,DC=test,DC=local";
                })
                .ToList();

            return new ConnectedSystemImportObject
            {
                ChangeType = ObjectChangeType.NotSet,
                ObjectType = "Group",
                Attributes = new List<ConnectedSystemImportObjectAttribute>
                {
                    new() { Name = "objectGUID", GuidValues = new List<Guid> { group.ObjectGuid } },
                    new() { Name = "distinguishedName", StringValues = new List<string> { $"CN={group.Name},OU=Groups,DC=test,DC=local" } },
                    new() { Name = "cn", StringValues = new List<string> { group.Name } },
                    new() { Name = "member", ReferenceValues = memberDns }
                }
            };
        }).ToList();
    }

    #endregion

    #region Reference Data Classes

    private record ReferenceUser(string UserId, string DisplayName, Guid ObjectGuid);
    private record ReferenceGroup(string GroupId, string Name, Guid ObjectGuid, string[] InitialMembers);

    #endregion
}
