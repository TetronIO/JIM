using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Workflow.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Workflow.Tests.Scenarios.Sync;

/// <summary>
/// Workflow tests that verify delta sync correctly processes CSOs that were modified during delta import.
///
/// Scenario being tested:
/// 1. Initial sync cycle (Full Import → Full Sync → Export → Confirming Import)
/// 2. Delta Import detects changes (e.g., group membership modification)
/// 3. Delta Sync should process the changed CSOs and flow changes to MVOs
///
/// This test addresses the issue where delta sync shows 0 changes after delta import
/// detects changes, which then causes exports to fail or behave unexpectedly.
/// </summary>
[TestFixture]
public class DeltaSyncAfterImportWorkflowTests
{
    private WorkflowTestHarness _harness = null!;

    // Reference users (consistent across test runs)
    private static readonly List<ReferenceUser> ReferenceUsers = new()
    {
        new("user1", "Alice Johnson", Guid.Parse("11111111-1111-1111-1111-111111111111")),
        new("user2", "Bob Smith", Guid.Parse("22222222-2222-2222-2222-222222222222")),
        new("user3", "Charlie Brown", Guid.Parse("33333333-3333-3333-3333-333333333333")),
        new("user4", "Diana Ross", Guid.Parse("44444444-4444-4444-4444-444444444444")),
    };

    // Reference group with initial members
    private static readonly ReferenceGroup TestGroup = new(
        "group1",
        "Project-Alpha",
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        new[] { "user1", "user2" } // Initial members: Alice, Bob
    );

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
    /// Verifies that delta sync correctly processes a group CSO that was modified during delta import
    /// with a membership change (add member).
    ///
    /// Expected flow:
    /// 1. Initial sync establishes baseline with group having 2 members
    /// 2. Delta import detects group membership change (add 1 member)
    /// 3. Delta sync should process the changed group CSO
    /// 4. MVO member attribute should be updated
    /// 5. Pending export should be created for the target system
    /// </summary>
    [Test]
    public async Task DeltaSync_AfterDeltaImportWithMembershipChange_ProcessesChangedCsoAsync()
    {
        // Arrange: Set up source and target systems with user and group schema
        await SetUpEntitlementScenarioAsync();

        // Step 1: Perform initial sync cycle
        await PerformInitialSyncAsync();
        var afterInitialSync = await _harness.TakeSnapshotAsync("After Initial Sync");

        // Verify initial state - users and group should be synced
        Assert.That(afterInitialSync.MvoCount, Is.EqualTo(ReferenceUsers.Count + 1),
            "Should have MVOs for all users and the group");

        // Verify initial group membership in MVO
        var groupMvo = await GetGroupMvoAsync();
        Assert.That(groupMvo, Is.Not.Null, "Group MVO should exist");

        var initialMemberCount = groupMvo!.AttributeValues
            .Count(av => av.Attribute?.Name == "member");
        Console.WriteLine($"Initial group MVO has {initialMemberCount} member values");

        // Verify no pending exports remain after initial sync
        Assert.That(afterInitialSync.PendingExportCount, Is.EqualTo(0),
            "No pending exports should remain after initial sync");

        // Record the watermark time (when delta sync was last completed)
        var watermarkBefore = await GetSystemWatermarkAsync("Source");
        Console.WriteLine($"Delta sync watermark before: {watermarkBefore}");

        // Step 2: Simulate delta import with group membership change (add user3)
        // Include all objects in the import batch to avoid Full Import marking users as obsolete
        // (this simulates a delta import where unchanged objects are still present)
        var sourceConnector = _harness.GetConnector("Source");
        var allObjects = GenerateSourceUsers().ToList();
        allObjects.Add(GenerateUpdatedGroupWithNewMember("user3"));
        sourceConnector.QueueImportObjects(allObjects);

        // Execute import
        var importActivity = await _harness.ExecuteFullImportAsync("Source");
        var afterDeltaImport = await _harness.TakeSnapshotAsync("After Delta Import");

        // Verify the CSO was modified during import
        var groupCso = await GetGroupCsoAsync("Source");
        Assert.That(groupCso, Is.Not.Null, "Group CSO should exist");
        Console.WriteLine($"Group CSO LastUpdated: {groupCso!.LastUpdated}");
        Console.WriteLine($"Group CSO status: {groupCso.Status}");

        // Verify the group CSO has the new member
        var csoMemberCount = groupCso.AttributeValues
            .Count(av => av.Attribute?.Name == "member");
        Console.WriteLine($"Group CSO has {csoMemberCount} member attribute values after import");
        Assert.That(csoMemberCount, Is.EqualTo(3), "Group CSO should have 3 members after import (added user3)");

        // Step 3: Execute delta sync
        var syncActivity = await _harness.ExecuteDeltaSyncAsync("Source");
        var afterDeltaSync = await _harness.TakeSnapshotAsync("After Delta Sync");

        // Log sync activity stats
        Console.WriteLine($"Delta sync ObjectsToProcess: {syncActivity.ObjectsToProcess}");
        Console.WriteLine($"Delta sync ObjectsProcessed: {syncActivity.ObjectsProcessed}");
        Console.WriteLine($"Delta sync RunProfileExecutionItems count: {syncActivity.RunProfileExecutionItems?.Count ?? 0}");

        // Log RPEI change types for debugging
        if (syncActivity.RunProfileExecutionItems != null)
        {
            foreach (var rpei in syncActivity.RunProfileExecutionItems)
            {
                Console.WriteLine($"  RPEI: CSO={rpei.ConnectedSystemObjectId}, ChangeType={rpei.ObjectChangeType}");
            }
        }

        // CRITICAL ASSERTION: Delta sync should have processed at least 1 CSO (the group)
        Assert.That(syncActivity.ObjectsProcessed, Is.GreaterThan(0),
            "Delta sync should have processed at least one CSO (the modified group)");

        // CRITICAL ASSERTION: RPEI should be created with AttributeFlow change type for reference changes
        Assert.That(syncActivity.RunProfileExecutionItems, Is.Not.Null.And.Not.Empty,
            "Delta sync should create RunProfileExecutionItems for changes");

        var attributeFlowRpeis = syncActivity.RunProfileExecutionItems!
            .Where(r => r.ObjectChangeType == ObjectChangeType.AttributeFlow)
            .ToList();
        Console.WriteLine($"Delta sync AttributeFlow RPEIs: {attributeFlowRpeis.Count}");

        Assert.That(attributeFlowRpeis, Has.Count.GreaterThan(0),
            "Delta sync should create at least one RPEI with AttributeFlow change type for the group membership change");

        // Verify Activity stats are correct (this is what the UI displays)
        // TotalObjectChangeCount should equal the number of RPEIs with actual changes
        var totalObjectChangeCount = syncActivity.RunProfileExecutionItems!.Count;
        var totalUnchanged = Math.Max(0, syncActivity.ObjectsProcessed - totalObjectChangeCount);

        Console.WriteLine($"Activity Stats: ObjectsProcessed={syncActivity.ObjectsProcessed}, " +
            $"TotalObjectChangeCount={totalObjectChangeCount}, TotalUnchanged={totalUnchanged}");

        // CRITICAL: With the fix, TotalUnchanged should be 0 (not 1) because we now create RPEIs for reference-only changes
        Assert.That(totalUnchanged, Is.EqualTo(0),
            "TotalUnchanged should be 0 because RPEI is created for reference attribute flow");
        Assert.That(attributeFlowRpeis.Count, Is.EqualTo(1),
            "Should have exactly 1 AttributeFlow RPEI for the group membership change");

        // Verify MVO was updated with new member
        var updatedGroupMvo = await GetGroupMvoAsync();
        Assert.That(updatedGroupMvo, Is.Not.Null, "Group MVO should still exist");

        var updatedMemberCount = updatedGroupMvo!.AttributeValues
            .Count(av => av.Attribute?.Name == "member");
        Console.WriteLine($"Group MVO has {updatedMemberCount} member values after delta sync");

        Assert.That(updatedMemberCount, Is.EqualTo(3),
            "Group MVO should have 3 members after delta sync (import flowed new member)");

        // Verify pending export was created for target system
        Assert.That(afterDeltaSync.PendingExportCount, Is.GreaterThan(0),
            "Pending export should be created for target system after member change");

        // Verify the pending export is for the group with member changes
        var groupPe = afterDeltaSync.PendingExports
            .FirstOrDefault(pe => pe.AttributeValueChanges.Any(av => av.AttributeInfo?.Name == "member"));
        Assert.That(groupPe, Is.Not.Null, "Pending export with member changes should exist");

        Console.WriteLine("=== Test Complete: Delta sync correctly processed group membership change ===");
    }

    /// <summary>
    /// Verifies that delta sync correctly processes a group CSO that was modified during delta import
    /// with a membership removal.
    /// </summary>
    [Test]
    public async Task DeltaSync_AfterDeltaImportWithMemberRemoval_ProcessesChangedCsoAsync()
    {
        // Arrange: Set up source and target systems with user and group schema
        await SetUpEntitlementScenarioAsync();

        // Step 1: Perform initial sync cycle
        await PerformInitialSyncAsync();
        var afterInitialSync = await _harness.TakeSnapshotAsync("After Initial Sync");

        // Verify initial group membership
        var groupMvo = await GetGroupMvoAsync();
        Assert.That(groupMvo, Is.Not.Null, "Group MVO should exist");

        var initialMemberCount = groupMvo!.AttributeValues
            .Count(av => av.Attribute?.Name == "member");
        Console.WriteLine($"Initial group MVO has {initialMemberCount} member values");
        Assert.That(initialMemberCount, Is.EqualTo(2), "Initial group should have 2 members");

        // Step 2: Simulate delta import with group membership change (remove user2)
        // Include all objects in the import batch to avoid Full Import marking users as obsolete
        var sourceConnector = _harness.GetConnector("Source");
        var allObjects = GenerateSourceUsers().ToList();
        allObjects.Add(GenerateUpdatedGroupWithMemberRemoved("user2"));
        sourceConnector.QueueImportObjects(allObjects);

        // Execute import
        await _harness.ExecuteFullImportAsync("Source");
        var afterDeltaImport = await _harness.TakeSnapshotAsync("After Delta Import");

        // Verify the group CSO has one less member
        var groupCso = await GetGroupCsoAsync("Source");
        var csoMemberCount = groupCso!.AttributeValues
            .Count(av => av.Attribute?.Name == "member");
        Console.WriteLine($"Group CSO has {csoMemberCount} member attribute values after import");
        Assert.That(csoMemberCount, Is.EqualTo(1), "Group CSO should have 1 member after import (removed user2)");

        // Step 3: Execute delta sync
        var syncActivity = await _harness.ExecuteDeltaSyncAsync("Source");
        var afterDeltaSync = await _harness.TakeSnapshotAsync("After Delta Sync");

        // Log sync activity stats
        Console.WriteLine($"Delta sync ObjectsToProcess: {syncActivity.ObjectsToProcess}");
        Console.WriteLine($"Delta sync ObjectsProcessed: {syncActivity.ObjectsProcessed}");
        Console.WriteLine($"Delta sync RunProfileExecutionItems count: {syncActivity.RunProfileExecutionItems?.Count ?? 0}");

        // Log RPEI change types for debugging
        if (syncActivity.RunProfileExecutionItems != null)
        {
            foreach (var rpei in syncActivity.RunProfileExecutionItems)
            {
                Console.WriteLine($"  RPEI: CSO={rpei.ConnectedSystemObjectId}, ChangeType={rpei.ObjectChangeType}");
            }
        }

        // CRITICAL ASSERTION: Delta sync should have processed at least 1 CSO (the group)
        Assert.That(syncActivity.ObjectsProcessed, Is.GreaterThan(0),
            "Delta sync should have processed at least one CSO (the modified group)");

        // CRITICAL ASSERTION: RPEI should be created with AttributeFlow change type for reference changes
        Assert.That(syncActivity.RunProfileExecutionItems, Is.Not.Null.And.Not.Empty,
            "Delta sync should create RunProfileExecutionItems for changes");

        var attributeFlowRpeis = syncActivity.RunProfileExecutionItems!
            .Where(r => r.ObjectChangeType == ObjectChangeType.AttributeFlow)
            .ToList();
        Console.WriteLine($"Delta sync AttributeFlow RPEIs: {attributeFlowRpeis.Count}");

        Assert.That(attributeFlowRpeis, Has.Count.GreaterThan(0),
            "Delta sync should create at least one RPEI with AttributeFlow change type for the group membership removal");

        // Verify Activity stats are correct (this is what the UI displays)
        var totalObjectChangeCount = syncActivity.RunProfileExecutionItems!.Count;
        var totalUnchanged = Math.Max(0, syncActivity.ObjectsProcessed - totalObjectChangeCount);

        Console.WriteLine($"Activity Stats: ObjectsProcessed={syncActivity.ObjectsProcessed}, " +
            $"TotalObjectChangeCount={totalObjectChangeCount}, TotalUnchanged={totalUnchanged}");

        // CRITICAL: With the fix, TotalUnchanged should be 0 (not 1) because we now create RPEIs for reference-only changes
        Assert.That(totalUnchanged, Is.EqualTo(0),
            "TotalUnchanged should be 0 because RPEI is created for reference attribute flow");
        Assert.That(attributeFlowRpeis.Count, Is.EqualTo(1),
            "Should have exactly 1 AttributeFlow RPEI for the group membership removal");

        // Verify MVO was updated (member removed)
        var updatedGroupMvo = await GetGroupMvoAsync();
        var updatedMemberCount = updatedGroupMvo!.AttributeValues
            .Count(av => av.Attribute?.Name == "member");
        Console.WriteLine($"Group MVO has {updatedMemberCount} member values after delta sync");

        Assert.That(updatedMemberCount, Is.EqualTo(1),
            "Group MVO should have 1 member after delta sync (member removed)");

        // Verify pending export was created
        Assert.That(afterDeltaSync.PendingExportCount, Is.GreaterThan(0),
            "Pending export should be created after member removal");

        Console.WriteLine("=== Test Complete: Delta sync correctly processed group membership removal ===");
    }

    #region Setup Helpers

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
            .WithStringAttribute("cn"));

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

        // Create Source import sync rules
        await _harness.CreateSyncRuleAsync(
            "Source User Import",
            "Source",
            "User",
            personType,
            SyncRuleDirection.Import,
            r => r
                .WithProjection()
                .WithAttributeFlow(mvPersonCn, sourceUserCn));

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

    private async Task PerformInitialSyncAsync()
    {
        // Import all users and groups together
        var sourceConnector = _harness.GetConnector("Source");
        var allObjects = GenerateSourceUsers().Concat(GenerateSourceGroups()).ToList();
        sourceConnector.QueueImportObjects(allObjects);

        await _harness.ExecuteFullImportAsync("Source");
        await _harness.ExecuteFullSyncAsync("Source");
        await _harness.ExecuteExportAsync("Target");

        // Confirming import from target
        var targetConnector = _harness.GetConnector("Target");
        targetConnector.WithConfirmingImportFactory(pe => GenerateConfirmingImportObject(pe));
        targetConnector.QueueConfirmingImport();
        await _harness.ExecuteConfirmingImportAsync("Target");

        // Clear any remaining pending exports
        var pendingExports = await _harness.DbContext.PendingExports.ToListAsync();
        _harness.DbContext.PendingExports.RemoveRange(pendingExports);
        await _harness.DbContext.SaveChangesAsync();

        // Update the delta sync watermark so delta sync knows what's "new"
        var sourceSystem = _harness.GetConnectedSystem("Source");
        sourceSystem.LastDeltaSyncCompletedAt = DateTime.UtcNow;
        await _harness.DbContext.SaveChangesAsync();
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
        var memberDns = TestGroup.InitialMembers
            .Select(userId =>
            {
                var user = ReferenceUsers.First(u => u.UserId == userId);
                return $"CN={user.DisplayName},OU=Users,DC=test,DC=local";
            })
            .ToList();

        return new List<ConnectedSystemImportObject>
        {
            new()
            {
                ChangeType = ObjectChangeType.NotSet,
                ObjectType = "Group",
                Attributes = new List<ConnectedSystemImportObjectAttribute>
                {
                    new() { Name = "objectGUID", GuidValues = new List<Guid> { TestGroup.ObjectGuid } },
                    new() { Name = "distinguishedName", StringValues = new List<string> { $"CN={TestGroup.Name},OU=Groups,DC=test,DC=local" } },
                    new() { Name = "cn", StringValues = new List<string> { TestGroup.Name } },
                    new() { Name = "member", ReferenceValues = memberDns }
                }
            }
        };
    }

    private ConnectedSystemImportObject GenerateUpdatedGroupWithNewMember(string newMemberUserId)
    {
        var newMember = ReferenceUsers.First(u => u.UserId == newMemberUserId);
        var allMemberDns = TestGroup.InitialMembers
            .Concat(new[] { newMemberUserId })
            .Select(userId =>
            {
                var user = ReferenceUsers.First(u => u.UserId == userId);
                return $"CN={user.DisplayName},OU=Users,DC=test,DC=local";
            })
            .ToList();

        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Updated, // Delta import - Update
            ObjectType = "Group",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = "objectGUID", GuidValues = new List<Guid> { TestGroup.ObjectGuid } },
                new() { Name = "distinguishedName", StringValues = new List<string> { $"CN={TestGroup.Name},OU=Groups,DC=test,DC=local" } },
                new() { Name = "cn", StringValues = new List<string> { TestGroup.Name } },
                new() { Name = "member", ReferenceValues = allMemberDns }
            }
        };
    }

    private ConnectedSystemImportObject GenerateUpdatedGroupWithMemberRemoved(string removedMemberUserId)
    {
        var remainingMemberDns = TestGroup.InitialMembers
            .Where(userId => userId != removedMemberUserId)
            .Select(userId =>
            {
                var user = ReferenceUsers.First(u => u.UserId == userId);
                return $"CN={user.DisplayName},OU=Users,DC=test,DC=local";
            })
            .ToList();

        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Updated, // Delta import - Update
            ObjectType = "Group",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = "objectGUID", GuidValues = new List<Guid> { TestGroup.ObjectGuid } },
                new() { Name = "distinguishedName", StringValues = new List<string> { $"CN={TestGroup.Name},OU=Groups,DC=test,DC=local" } },
                new() { Name = "cn", StringValues = new List<string> { TestGroup.Name } },
                new() { Name = "member", ReferenceValues = remainingMemberDns }
            }
        };
    }

    private ConnectedSystemImportObject GenerateConfirmingImportObject(PendingExport pe)
    {
        var cnChange = pe.AttributeValueChanges.FirstOrDefault(av => av.Attribute?.Name == "cn");
        var dnChange = pe.AttributeValueChanges.FirstOrDefault(av => av.Attribute?.Name == "distinguishedName");
        var memberChange = pe.AttributeValueChanges.FirstOrDefault(av => av.Attribute?.Name == "member");

        var objectType = memberChange != null ? "Group" : "User";
        var cn = cnChange?.StringValue ?? "Unknown";
        var dn = dnChange?.StringValue ?? $"CN={cn},OU=Unknown,DC=test,DC=local";

        var attributes = new List<ConnectedSystemImportObjectAttribute>
        {
            new() { Name = "objectGUID", GuidValues = new List<Guid> { Guid.NewGuid() } },
            new() { Name = "distinguishedName", StringValues = new List<string> { dn } },
            new() { Name = "cn", StringValues = new List<string> { cn } }
        };

        if (objectType == "Group")
        {
            var memberValues = pe.AttributeValueChanges
                .Where(av => av.Attribute?.Name == "member")
                .Select(av => av.UnresolvedReferenceValue ?? av.StringValue ?? "")
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();

            if (memberValues.Count > 0)
            {
                attributes.Add(new ConnectedSystemImportObjectAttribute
                {
                    Name = "member",
                    ReferenceValues = memberValues
                });
            }
        }

        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = objectType,
            Attributes = attributes
        };
    }

    private async Task<MetaverseObject?> GetGroupMvoAsync()
    {
        return await _harness.DbContext.MetaverseObjects
            .Include(m => m.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(m => m.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .FirstOrDefaultAsync(m => m.AttributeValues.Any(av =>
                av.Attribute != null && av.Attribute.Name == "cn" && av.StringValue == TestGroup.Name));
    }

    private async Task<ConnectedSystemObject?> GetGroupCsoAsync(string systemName)
    {
        var system = _harness.GetConnectedSystem(systemName);
        return await _harness.DbContext.ConnectedSystemObjects
            .Include(c => c.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(c => c.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .FirstOrDefaultAsync(c =>
                c.ConnectedSystemId == system.Id &&
                c.AttributeValues.Any(av =>
                    av.Attribute != null && av.Attribute.Name == "cn" && av.StringValue == TestGroup.Name));
    }

    private async Task<DateTime?> GetSystemWatermarkAsync(string systemName)
    {
        var system = await _harness.DbContext.ConnectedSystems
            .FirstAsync(s => s.Name == systemName);
        return system.LastDeltaSyncCompletedAt;
    }

    #endregion

    #region Reference Data Classes

    private record ReferenceUser(string UserId, string DisplayName, Guid ObjectGuid);
    private record ReferenceGroup(string GroupId, string Name, Guid ObjectGuid, string[] InitialMembers);

    #endregion
}
