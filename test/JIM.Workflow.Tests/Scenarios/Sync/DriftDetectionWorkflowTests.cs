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
/// Workflow tests for drift detection - detecting and correcting unauthorised changes in target systems.
/// These tests verify that:
/// 1. Drift is detected when a non-contributor system's CSO value differs from MVO
/// 2. Corrective pending exports are staged to remediate drift
/// 3. EnforceState=false disables drift detection for an export rule
/// 4. Drift is NOT flagged when the system is a legitimate contributor (has import rules)
/// </summary>
[TestFixture]
public class DriftDetectionWorkflowTests
{
    private WorkflowTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new WorkflowTestHarness();
    }

    [TearDown]
    public void TearDown()
    {
        // Print snapshots if test failed for diagnostics
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
        {
            Console.WriteLine("=== SNAPSHOT DIAGNOSTICS ===");
            _harness.PrintSnapshotSummaries();
        }

        _harness?.Dispose();
    }

    #region Drift Detection Tests

    /// <summary>
    /// Verifies that drift is detected when a target system's CSO attribute value
    /// differs from what the export rule expects (based on MVO value).
    /// Scenario: HR is source (contributes to MVO), AD is target (receives from MVO).
    /// Unauthorised change made directly in AD should trigger drift detection.
    /// </summary>
    [Test]
    public async Task DriftDetected_NonContributorSystemChanged_CorrectiveExportCreatedAsync()
    {
        // Arrange: Set up HR (source) -> MV -> AD (target) sync scenario
        await SetUpDriftDetectionScenarioAsync(enforceState: true);

        // Step 1: Initial import from HR and sync to create MVOs
        var hrConnector = _harness.GetConnector("HR");
        hrConnector.QueueImportObjects(GenerateSourceUsers(3, "Initial"));

        await _harness.ExecuteFullImportAsync("HR");
        await _harness.ExecuteFullSyncAsync("HR");
        var afterInitialSync = await _harness.TakeSnapshotAsync("After Initial Sync");

        // Should have pending exports for the 3 users to AD
        Assert.That(afterInitialSync.PendingExportCount, Is.EqualTo(3),
            "Should have 3 pending exports after initial sync");

        // Execute exports to AD and populate CSO values
        await _harness.ExecuteExportAsync("AD");
        await PopulateCsoAttributeValuesFromPendingExportsAsync();

        // Clear pending exports (mark as complete)
        await ClearPendingExportsAsync();
        var beforeDrift = await _harness.TakeSnapshotAsync("Before Drift");
        Assert.That(beforeDrift.PendingExportCount, Is.EqualTo(0));

        // Step 2: Simulate unauthorised change directly in AD (bypass JIM)
        // Change the displayName on AD CSOs to simulate drift
        await SimulateUnauthorisedCsoChangeAsync("AD", "cn", "Drifted Value");

        // Step 3: Run delta sync on AD to detect drift
        // This should:
        // 1. Compare AD CSO values against what export rules expect
        // 2. Detect that AD has drifted from expected state
        // 3. Stage corrective pending exports to fix the drift
        await _harness.ExecuteDeltaSyncAsync("AD");
        var afterDriftDetection = await _harness.TakeSnapshotAsync("After Drift Detection");

        // Assert: Corrective pending exports should be created
        Assert.That(afterDriftDetection.PendingExportCount, Is.GreaterThan(0),
            "Corrective pending exports should be created when drift is detected");

        // Verify the pending exports are corrective (Update type, not Create)
        var pendingExports = await _harness.DbContext.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .ThenInclude(avc => avc.Attribute)
            .ToListAsync();

        Assert.That(pendingExports.All(pe => pe.ChangeType == PendingExportChangeType.Update),
            "Drift correction exports should be Update type");

        // Verify the exports would restore the original value
        foreach (var pe in pendingExports)
        {
            var cnChange = pe.AttributeValueChanges.FirstOrDefault(avc => avc.Attribute?.Name == "cn");
            Assert.That(cnChange, Is.Not.Null, "cn attribute should be in the corrective export");
            Assert.That(cnChange!.StringValue, Does.Contain("Initial"),
                "Corrective export should restore original value from MVO");
        }
    }

    /// <summary>
    /// Verifies that drift is NOT flagged when EnforceState=false on the export rule.
    /// The target system can have different values without triggering correction.
    /// </summary>
    [Test]
    public async Task DriftNotDetected_EnforceStateFalse_NoPendingExportsCreatedAsync()
    {
        // Arrange: Set up scenario with EnforceState=false
        await SetUpDriftDetectionScenarioAsync(enforceState: false);

        // Step 1: Initial sync
        var hrConnector = _harness.GetConnector("HR");
        hrConnector.QueueImportObjects(GenerateSourceUsers(2, "Initial"));

        await _harness.ExecuteFullImportAsync("HR");
        await _harness.ExecuteFullSyncAsync("HR");

        // Execute exports and populate CSO values
        await _harness.ExecuteExportAsync("AD");
        await PopulateCsoAttributeValuesFromPendingExportsAsync();
        await ClearPendingExportsAsync();

        // Step 2: Simulate drift in AD
        await SimulateUnauthorisedCsoChangeAsync("AD", "cn", "Drifted Value");

        // Step 3: Run delta sync - should NOT detect drift because EnforceState=false
        await _harness.ExecuteDeltaSyncAsync("AD");
        var afterDeltaSync = await _harness.TakeSnapshotAsync("After Delta Sync");

        // Assert: No pending exports because EnforceState is disabled
        Assert.That(afterDeltaSync.PendingExportCount, Is.EqualTo(0),
            "No pending exports should be created when EnforceState=false");
    }

    /// <summary>
    /// Verifies that a system that is a legitimate contributor (has import rules)
    /// does NOT trigger drift detection when its values change.
    /// Scenario: Bidirectional sync - AD both imports and exports the same attribute.
    ///
    /// Note: This test requires more complex setup as delta sync also evaluates regular export rules,
    /// not just drift detection. The unit tests in DriftDetectionTests.cs provide more targeted coverage.
    /// </summary>
    [Test]
    [Explicit("Requires bidirectional sync infrastructure - covered by unit tests")]
    public async Task DriftNotDetected_ContributorSystemChanged_NoPendingExportsCreatedAsync()
    {
        // Arrange: Set up bidirectional scenario where AD is both source and target
        await SetUpBidirectionalScenarioAsync();

        // Step 1: Initial sync from HR
        var hrConnector = _harness.GetConnector("HR");
        hrConnector.QueueImportObjects(GenerateSourceUsers(2, "HR"));

        await _harness.ExecuteFullImportAsync("HR");
        await _harness.ExecuteFullSyncAsync("HR");

        // Execute exports to AD and populate CSO values
        await _harness.ExecuteExportAsync("AD");
        await PopulateCsoAttributeValuesFromPendingExportsAsync();
        await ClearPendingExportsAsync();

        // Step 2: Simulate change in AD (but AD is a contributor, so this is legitimate)
        // In a real scenario, this would come from AD import
        await SimulateUnauthorisedCsoChangeAsync("AD", "cn", "AD Updated Value");

        // Step 3: Run delta sync - should NOT flag as drift because AD is a contributor
        await _harness.ExecuteDeltaSyncAsync("AD");
        var afterDeltaSync = await _harness.TakeSnapshotAsync("After Delta Sync");

        // Assert: No corrective exports because AD is a legitimate contributor
        Assert.That(afterDeltaSync.PendingExportCount, Is.EqualTo(0),
            "No corrective exports when system is a legitimate contributor");
    }

    /// <summary>
    /// Verifies that drift detection only evaluates attributes that are in export rules.
    /// Attributes not in any export rule should not trigger drift detection.
    ///
    /// Note: This test requires a more controlled setup as delta sync evaluates regular export rules,
    /// which may create pending exports unrelated to drift detection. The unit tests in
    /// DriftDetectionTests.cs provide more targeted coverage of attribute-level drift detection.
    /// </summary>
    [Test]
    [Explicit("Requires more controlled delta sync setup - covered by unit tests")]
    public async Task DriftNotDetected_AttributeNotInExportRule_NoPendingExportsCreatedAsync()
    {
        // Arrange: Set up scenario where only displayName is exported, not description
        await SetUpDriftDetectionScenarioAsync(enforceState: true);

        // Step 1: Initial sync
        var hrConnector = _harness.GetConnector("HR");
        hrConnector.QueueImportObjects(GenerateSourceUsers(2, "Initial"));

        await _harness.ExecuteFullImportAsync("HR");
        await _harness.ExecuteFullSyncAsync("HR");

        // Execute exports and populate CSO values
        await _harness.ExecuteExportAsync("AD");
        await PopulateCsoAttributeValuesFromPendingExportsAsync();
        await ClearPendingExportsAsync();

        // Step 2: Simulate change to an attribute that is NOT in the export rule
        // The harness uses "cn" for export, so change a different attribute
        await SimulateUnauthorisedCsoChangeAsync("AD", "description", "Some description");

        // Step 3: Run delta sync - should NOT detect drift for unmapped attribute
        await _harness.ExecuteDeltaSyncAsync("AD");
        var afterDeltaSync = await _harness.TakeSnapshotAsync("After Delta Sync");

        // Assert: No pending exports for attributes not in export rules
        Assert.That(afterDeltaSync.PendingExportCount, Is.EqualTo(0),
            "No pending exports when drifted attribute is not in export rules");
    }

    /// <summary>
    /// Verifies that drift detection on an export-only target system correctly identifies
    /// source systems as contributors and does NOT create false positive drift correction exports.
    ///
    /// Scenario:
    /// - HR is source system (has import rules → contributes to MVO)
    /// - AD is target system (has ONLY export rules → receives from MVO, not a contributor)
    /// - Initial sync creates MVOs from HR and pending exports to AD
    /// - After confirming imports, AD CSOs have values from MVO
    /// - Running full sync on AD should NOT flag these values as drift because HR is the contributor
    ///
    /// This test specifically covers the bug where BuildDriftDetectionCache only loaded sync rules
    /// for the current system, causing the import mapping cache to be empty for export-only systems,
    /// leading to false positive drift detection.
    /// </summary>
    [Test]
    public async Task DriftNotDetected_ExportOnlyTargetSystem_FullSync_NoFalsePositiveExportsAsync()
    {
        // Arrange: Set up HR (source) -> MV -> AD (export-only target) scenario
        await SetUpDriftDetectionScenarioAsync(enforceState: true);

        // Step 1: Initial import from HR and sync to create MVOs
        var hrConnector = _harness.GetConnector("HR");
        hrConnector.QueueImportObjects(GenerateSourceUsers(5, "HR"));

        await _harness.ExecuteFullImportAsync("HR");
        await _harness.ExecuteFullSyncAsync("HR");
        var afterInitialSync = await _harness.TakeSnapshotAsync("After Initial Sync");

        // Should have 5 pending exports for AD
        Assert.That(afterInitialSync.PendingExportCount, Is.EqualTo(5),
            "Should have 5 pending exports after initial sync");

        // Execute exports to AD
        await _harness.ExecuteExportAsync("AD");

        // Populate CSO attribute values from pending exports (simulating confirming import)
        await PopulateCsoAttributeValuesFromPendingExportsAsync();

        // Clear pending exports (mark as complete)
        await ClearPendingExportsAsync();
        var beforeFullSync = await _harness.TakeSnapshotAsync("Before Full Sync on AD");
        Assert.That(beforeFullSync.PendingExportCount, Is.EqualTo(0),
            "Should have no pending exports before full sync");

        // Step 2: Run FULL SYNC on AD (export-only target system)
        // This tests the fix: BuildDriftDetectionCache must load ALL sync rules from ALL systems
        // to correctly identify HR as a contributor to the MVO attributes.
        // Without the fix, the import mapping cache would be empty for AD (no import rules),
        // causing drift detection to incorrectly flag ALL objects as drift.
        await _harness.ExecuteFullSyncAsync("AD");
        var afterFullSync = await _harness.TakeSnapshotAsync("After Full Sync on AD");

        // Assert: NO drift correction exports should be created
        // The values in AD CSOs match the MVO values (both came from HR), so there's no drift.
        // The drift detection should correctly identify HR as the contributor and not flag this.
        Assert.That(afterFullSync.PendingExportCount, Is.EqualTo(0),
            "Should have NO pending exports - values match MVO (no drift). " +
            "If there are pending exports, drift detection may not be correctly identifying contributors.");

        // If there were pending exports (bug scenario), it would mean drift was incorrectly detected
        // Additional verification: check there are no pending exports in database
        var pendingExportCount = await _harness.DbContext.PendingExports.CountAsync();
        Assert.That(pendingExportCount, Is.EqualTo(0),
            "Database should have no pending exports - drift detection should not create false positives");
    }

    /// <summary>
    /// Verifies that legitimate drift IS detected on export-only target systems when
    /// values actually differ from what the MVO specifies.
    ///
    /// This is the positive test case - drift detection should work correctly.
    /// </summary>
    [Test]
    public async Task DriftDetected_ExportOnlyTargetSystem_FullSync_CorrectivePendingExportsCreatedAsync()
    {
        // Arrange: Set up HR (source) -> MV -> AD (export-only target) scenario
        await SetUpDriftDetectionScenarioAsync(enforceState: true);

        // Step 1: Initial import from HR and sync to create MVOs
        var hrConnector = _harness.GetConnector("HR");
        hrConnector.QueueImportObjects(GenerateSourceUsers(3, "HR"));

        await _harness.ExecuteFullImportAsync("HR");
        await _harness.ExecuteFullSyncAsync("HR");

        // Execute exports to AD and populate CSO values
        await _harness.ExecuteExportAsync("AD");
        await PopulateCsoAttributeValuesFromPendingExportsAsync();
        await ClearPendingExportsAsync();

        // Step 2: Simulate ACTUAL drift - modify AD CSO values to differ from MVO
        // This represents an unauthorised change made directly in AD
        await SimulateUnauthorisedCsoChangeAsync("AD", "cn", "Drifted Value");

        // Step 3: Run full sync on AD - should detect the drift
        await _harness.ExecuteFullSyncAsync("AD");
        var afterFullSync = await _harness.TakeSnapshotAsync("After Full Sync with Drift");

        // Assert: Corrective pending exports SHOULD be created for the drifted objects
        Assert.That(afterFullSync.PendingExportCount, Is.GreaterThan(0),
            "Should have pending exports to correct the drift");

        // Verify the pending exports are corrective (Update type)
        var pendingExports = await _harness.DbContext.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .ThenInclude(avc => avc.Attribute)
            .ToListAsync();

        Assert.That(pendingExports.All(pe => pe.ChangeType == PendingExportChangeType.Update),
            "Drift correction exports should be Update type");

        // Verify the exports would restore the original MVO value
        foreach (var pe in pendingExports)
        {
            var cnChange = pe.AttributeValueChanges.FirstOrDefault(avc => avc.Attribute?.Name == "cn");
            Assert.That(cnChange, Is.Not.Null, "cn attribute should be in the corrective export");
            Assert.That(cnChange!.StringValue, Does.Contain("HR"),
                "Corrective export should restore original value from MVO (sourced from HR)");
        }
    }

    /// <summary>
    /// Verifies that drift detection correctly handles multi-valued reference attributes.
    /// This is a regression test for a bug where drift detection only compared the first value
    /// in a multi-valued attribute collection, causing false positive drift detection for ALL
    /// groups even when only one group had actual drift.
    ///
    /// Bug scenario:
    /// - Group has 5 members: [A, B, C, D, E]
    /// - Drift detection should compare ALL members, not just the first one
    /// - If drift detection uses FirstOrDefault(), it only compares member A, and different
    ///   collection ordering would cause false positive drift detection
    ///
    /// Expected behaviour:
    /// - When group has identical members in CSO and expected from MVO → No drift
    /// - When group has different members → Drift detected, but only for that group
    /// </summary>
    [Test]
    public async Task DriftDetection_MultiValuedReferenceAttribute_CompareAllValuesNotJustFirstAsync()
    {
        // Arrange: Set up source → MV → target with group membership
        await SetUpEntitlementDriftDetectionScenarioAsync(enforceState: true);

        // Generate test data: 3 users and 1 group with all 3 users as members
        var user1Guid = Guid.NewGuid();
        var user2Guid = Guid.NewGuid();
        var user3Guid = Guid.NewGuid();
        var groupGuid = Guid.NewGuid();

        var sourceConnector = _harness.GetConnector("Source");

        // Import users first
        var users = new List<ConnectedSystemImportObject>
        {
            new()
            {
                ChangeType = ObjectChangeType.NotSet,
                ObjectType = "User",
                Attributes = new List<ConnectedSystemImportObjectAttribute>
                {
                    new() { Name = "objectGUID", GuidValues = new List<Guid> { user1Guid } },
                    new() { Name = "distinguishedName", StringValues = new List<string> { "CN=User1,OU=Users,DC=source,DC=local" } },
                    new() { Name = "cn", StringValues = new List<string> { "User1" } }
                }
            },
            new()
            {
                ChangeType = ObjectChangeType.NotSet,
                ObjectType = "User",
                Attributes = new List<ConnectedSystemImportObjectAttribute>
                {
                    new() { Name = "objectGUID", GuidValues = new List<Guid> { user2Guid } },
                    new() { Name = "distinguishedName", StringValues = new List<string> { "CN=User2,OU=Users,DC=source,DC=local" } },
                    new() { Name = "cn", StringValues = new List<string> { "User2" } }
                }
            },
            new()
            {
                ChangeType = ObjectChangeType.NotSet,
                ObjectType = "User",
                Attributes = new List<ConnectedSystemImportObjectAttribute>
                {
                    new() { Name = "objectGUID", GuidValues = new List<Guid> { user3Guid } },
                    new() { Name = "distinguishedName", StringValues = new List<string> { "CN=User3,OU=Users,DC=source,DC=local" } },
                    new() { Name = "cn", StringValues = new List<string> { "User3" } }
                }
            }
        };

        // Import group with all 3 members
        var group = new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "Group",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = "objectGUID", GuidValues = new List<Guid> { groupGuid } },
                new() { Name = "distinguishedName", StringValues = new List<string> { "CN=TestGroup,OU=Groups,DC=source,DC=local" } },
                new() { Name = "cn", StringValues = new List<string> { "TestGroup" } },
                new() { Name = "member", ReferenceValues = new List<string>
                {
                    "CN=User1,OU=Users,DC=source,DC=local",
                    "CN=User2,OU=Users,DC=source,DC=local",
                    "CN=User3,OU=Users,DC=source,DC=local"
                }}
            }
        };

        // Queue all objects in a single call (required for proper import processing)
        sourceConnector.QueueImportObjects(users.Concat(new List<ConnectedSystemImportObject> { group }).ToList());

        // Step 1: Initial import and sync
        await _harness.ExecuteFullImportAsync("Source");
        await _harness.ExecuteFullSyncAsync("Source");
        var afterInitialSync = await _harness.TakeSnapshotAsync("After Initial Sync");

        // Should have pending exports for 3 users + 1 group to Target
        Assert.That(afterInitialSync.PendingExportCount, Is.EqualTo(4),
            "Should have 4 pending exports (3 users + 1 group) after initial sync");

        // Capture pending export data BEFORE export execution (export will clear these)
        var pendingExportData = await CapturePendingExportDataAsync();

        // Execute exports to Target
        await _harness.ExecuteExportAsync("Target");

        // Now populate CSO values using the captured data
        await PopulateEntitlementCsoAttributeValuesFromCapturedDataAsync(pendingExportData);

        // Clear pending exports
        await ClearPendingExportsAsync();
        var beforeDriftTest = await _harness.TakeSnapshotAsync("Before Drift Test");
        Assert.That(beforeDriftTest.PendingExportCount, Is.EqualTo(0),
            "Should have no pending exports before drift test");

        // Step 2: Run full sync on Target (export-only) to test drift detection
        // With the bug, this would incorrectly flag the group as drifted because drift detection
        // only compared the first member value, not all members.
        await _harness.ExecuteFullSyncAsync("Target");
        var afterTargetSync = await _harness.TakeSnapshotAsync("After Target Full Sync");

        // Assert: NO drift should be detected because all members match
        // This is the key assertion - with the bug, pending exports would be created
        Assert.That(afterTargetSync.PendingExportCount, Is.EqualTo(0),
            "Should have NO pending exports - multi-valued member attribute should compare ALL values, not just first. " +
            $"Found {afterTargetSync.PendingExportCount} pending exports, which indicates drift detection bug.");

        // Additional verification: no pending exports in database
        var pendingExportCount = await _harness.DbContext.PendingExports.CountAsync();
        Assert.That(pendingExportCount, Is.EqualTo(0),
            "Database should have no pending exports - drift detection should correctly compare all member values");
    }

    /// <summary>
    /// Verifies that legitimate drift in multi-valued reference attributes IS detected.
    /// This is the positive test case to ensure drift detection works for actual changes.
    ///
    /// Scenario:
    /// - Group has members [A, B, C] in Source (authoritative)
    /// - Unauthorised change in Target adds member D (drift)
    /// - Drift detection should detect the extra member and create corrective export
    /// </summary>
    [Test]
    public async Task DriftDetection_MultiValuedReferenceAttribute_ActualDriftDetectedAsync()
    {
        // Arrange: Set up source → MV → target with group membership
        await SetUpEntitlementDriftDetectionScenarioAsync(enforceState: true);

        // Generate test data
        var user1Guid = Guid.NewGuid();
        var user2Guid = Guid.NewGuid();
        var user3Guid = Guid.NewGuid();
        var user4Guid = Guid.NewGuid(); // Extra user for drift
        var groupGuid = Guid.NewGuid();

        var sourceConnector = _harness.GetConnector("Source");

        // Import 4 users
        var users = new List<ConnectedSystemImportObject>
        {
            CreateUserImportObject(user1Guid, "User1"),
            CreateUserImportObject(user2Guid, "User2"),
            CreateUserImportObject(user3Guid, "User3"),
            CreateUserImportObject(user4Guid, "User4") // Will be added as drift
        };

        // Import group with only 3 members (User1, User2, User3) - NOT User4
        var group = new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "Group",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = "objectGUID", GuidValues = new List<Guid> { groupGuid } },
                new() { Name = "distinguishedName", StringValues = new List<string> { "CN=TestGroup,OU=Groups,DC=source,DC=local" } },
                new() { Name = "cn", StringValues = new List<string> { "TestGroup" } },
                new() { Name = "member", ReferenceValues = new List<string>
                {
                    "CN=User1,OU=Users,DC=source,DC=local",
                    "CN=User2,OU=Users,DC=source,DC=local",
                    "CN=User3,OU=Users,DC=source,DC=local"
                }}
            }
        };

        // Queue all objects in a single call (required for proper import processing)
        sourceConnector.QueueImportObjects(users.Concat(new List<ConnectedSystemImportObject> { group }).ToList());

        // Step 1: Initial import and sync
        await _harness.ExecuteFullImportAsync("Source");
        await _harness.ExecuteFullSyncAsync("Source");

        // Capture pending export data BEFORE execution
        var pendingExportData = await CapturePendingExportDataAsync();

        // Execute exports and populate CSO values
        await _harness.ExecuteExportAsync("Target");
        await PopulateEntitlementCsoAttributeValuesFromCapturedDataAsync(pendingExportData);
        await ClearPendingExportsAsync();

        // Step 2: Simulate drift - add User4 to the group membership in Target CSO
        // (bypassing JIM - simulating unauthorised change directly in Target AD)
        await SimulateMembershipDriftAsync(groupGuid, user4Guid, addMember: true);

        // Step 3: Run full sync on Target to detect drift
        await _harness.ExecuteFullSyncAsync("Target");
        var afterDriftDetection = await _harness.TakeSnapshotAsync("After Drift Detection");

        // Assert: Drift SHOULD be detected - group has extra member
        Assert.That(afterDriftDetection.PendingExportCount, Is.GreaterThan(0),
            "Should detect drift when group has unauthorised extra member");

        // Verify it's a corrective export for the group
        var pendingExports = await _harness.DbContext.PendingExports
            .Include(pe => pe.ConnectedSystemObject)
            .ThenInclude(cso => cso!.Type)
            .Include(pe => pe.AttributeValueChanges)
            .ThenInclude(avc => avc.Attribute)
            .ToListAsync();

        // Find the group export - look for an export on a Group type CSO
        var groupExport = pendingExports.FirstOrDefault(pe =>
            pe.ConnectedSystemObject?.Type?.Name == "Group");

        Assert.That(groupExport, Is.Not.Null,
            "Should have corrective pending export for the drifted group");
        Assert.That(groupExport!.ChangeType, Is.EqualTo(PendingExportChangeType.Update),
            "Corrective export should be Update type");
    }

    private ConnectedSystemImportObject CreateUserImportObject(Guid objectGuid, string userName)
    {
        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = "objectGUID", GuidValues = new List<Guid> { objectGuid } },
                new() { Name = "distinguishedName", StringValues = new List<string> { $"CN={userName},OU=Users,DC=source,DC=local" } },
                new() { Name = "cn", StringValues = new List<string> { userName } }
            }
        };
    }

    #endregion

    #region Setup Helpers

    private async Task SetUpDriftDetectionScenarioAsync(bool enforceState)
    {
        // Create HR (source) system
        await _harness.CreateConnectedSystemAsync("HR");
        await _harness.CreateObjectTypeAsync("HR", "User", t => t
            .WithGuidExternalId("employeeId")
            .WithStringAttribute("displayName"));

        // Create AD (target) system
        await _harness.CreateConnectedSystemAsync("AD");
        await _harness.CreateObjectTypeAsync("AD", "User", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn")
            .WithStringAttribute("description"));

        // Create MV type
        var personType = await _harness.CreateMetaverseObjectTypeAsync("Person", t => t
            .WithGuidAttribute("employeeId")
            .WithStringAttribute("displayName"));

        // Get attributes for flow rules
        var hrUserType = _harness.GetObjectType("HR", "User");
        var adUserType = _harness.GetObjectType("AD", "User");

        var hrDisplayName = hrUserType.Attributes.First(a => a.Name == "displayName");
        var adCn = adUserType.Attributes.First(a => a.Name == "cn");
        var adDn = adUserType.Attributes.First(a => a.Name == "distinguishedName");

        // Get MV attributes
        var mvDisplayName = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "displayName");

        // Create import sync rule (HR → MV) - HR is the contributor
        await _harness.CreateSyncRuleAsync(
            "HR Import",
            "HR",
            "User",
            personType,
            SyncRuleDirection.Import,
            r => r
                .WithProjection()
                .WithAttributeFlow(mvDisplayName, hrDisplayName));

        // Create export sync rule (MV → AD) - AD is the target (non-contributor)
        await _harness.CreateSyncRuleAsync(
            "AD Export",
            "AD",
            "User",
            personType,
            SyncRuleDirection.Export,
            r => r
                .WithProvisioning()
                .WithEnforceState(enforceState)
                .WithAttributeFlow(mvDisplayName, adCn)
                .WithExpressionFlow("\"CN=\" + mv[\"displayName\"] + \",OU=Users,DC=test,DC=local\"", adDn));
    }

    private async Task SetUpBidirectionalScenarioAsync()
    {
        // Create HR (source) system
        await _harness.CreateConnectedSystemAsync("HR");
        await _harness.CreateObjectTypeAsync("HR", "User", t => t
            .WithGuidExternalId("employeeId")
            .WithStringAttribute("displayName"));

        // Create AD (bidirectional) system - both imports and exports
        await _harness.CreateConnectedSystemAsync("AD");
        await _harness.CreateObjectTypeAsync("AD", "User", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn"));

        // Create MV type
        var personType = await _harness.CreateMetaverseObjectTypeAsync("Person", t => t
            .WithGuidAttribute("employeeId")
            .WithStringAttribute("displayName"));

        // Get attributes
        var hrUserType = _harness.GetObjectType("HR", "User");
        var adUserType = _harness.GetObjectType("AD", "User");

        var hrDisplayName = hrUserType.Attributes.First(a => a.Name == "displayName");
        var adCn = adUserType.Attributes.First(a => a.Name == "cn");
        var adDn = adUserType.Attributes.First(a => a.Name == "distinguishedName");

        var mvDisplayName = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "displayName");

        // HR Import - HR contributes to displayName
        await _harness.CreateSyncRuleAsync(
            "HR Import",
            "HR",
            "User",
            personType,
            SyncRuleDirection.Import,
            r => r
                .WithProjection()
                .WithAttributeFlow(mvDisplayName, hrDisplayName));

        // AD Import - AD ALSO contributes to displayName (bidirectional)
        await _harness.CreateSyncRuleAsync(
            "AD Import",
            "AD",
            "User",
            personType,
            SyncRuleDirection.Import,
            r => r
                .WithAttributeFlow(mvDisplayName, adCn));

        // AD Export - exports displayName to AD
        await _harness.CreateSyncRuleAsync(
            "AD Export",
            "AD",
            "User",
            personType,
            SyncRuleDirection.Export,
            r => r
                .WithProvisioning()
                .WithEnforceState(true)
                .WithAttributeFlow(mvDisplayName, adCn)
                .WithExpressionFlow("\"CN=\" + mv[\"displayName\"] + \",OU=Users,DC=test,DC=local\"", adDn));
    }

    #endregion

    #region Test Data Generators

    private static readonly Dictionary<int, Guid> _stableEmployeeIds = new();

    private List<ConnectedSystemImportObject> GenerateSourceUsers(int count, string namePrefix)
    {
        var users = new List<ConnectedSystemImportObject>();

        for (int i = 0; i < count; i++)
        {
            // Use stable employee IDs so the same "user" is recognised across imports
            if (!_stableEmployeeIds.ContainsKey(i))
            {
                _stableEmployeeIds[i] = Guid.NewGuid();
            }

            users.Add(new ConnectedSystemImportObject
            {
                ChangeType = ObjectChangeType.NotSet,
                ObjectType = "User",
                Attributes = new List<ConnectedSystemImportObjectAttribute>
                {
                    new()
                    {
                        Name = "employeeId",
                        GuidValues = new List<Guid> { _stableEmployeeIds[i] }
                    },
                    new()
                    {
                        Name = "displayName",
                        StringValues = new List<string> { $"{namePrefix} User {i}" }
                    }
                }
            });
        }

        return users;
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Populates CSO attribute values based on pending exports.
    /// This simulates what would happen after a confirming import.
    /// </summary>
    private async Task PopulateCsoAttributeValuesFromPendingExportsAsync()
    {
        var pendingExports = await _harness.DbContext.PendingExports
            .Include(pe => pe.ConnectedSystemObject)
            .ThenInclude(cso => cso!.Type)
            .ThenInclude(csot => csot.Attributes)
            .Include(pe => pe.AttributeValueChanges)
            .ThenInclude(avc => avc.Attribute)
            .ToListAsync();

        foreach (var pe in pendingExports)
        {
            if (pe.ConnectedSystemObject == null) continue;

            var cso = pe.ConnectedSystemObject;

            foreach (var avc in pe.AttributeValueChanges)
            {
                if (avc.Attribute == null) continue;

                // Find or create CSO attribute value
                var existingAttrValue = await _harness.DbContext.ConnectedSystemObjectAttributeValues
                    .FirstOrDefaultAsync(av =>
                        av.ConnectedSystemObject.Id == cso.Id &&
                        av.AttributeId == avc.AttributeId);

                if (existingAttrValue == null)
                {
                    existingAttrValue = new ConnectedSystemObjectAttributeValue
                    {
                        ConnectedSystemObject = cso,
                        AttributeId = avc.AttributeId
                    };
                    _harness.DbContext.ConnectedSystemObjectAttributeValues.Add(existingAttrValue);
                }

                // Copy values from pending export
                existingAttrValue.StringValue = avc.StringValue;
                existingAttrValue.IntValue = avc.IntValue;
                existingAttrValue.DateTimeValue = avc.DateTimeValue;
                existingAttrValue.ByteValue = avc.ByteValue;
                existingAttrValue.UnresolvedReferenceValue = avc.UnresolvedReferenceValue;
            }

            // Transition CSO from PendingProvisioning to Normal (simulating confirming import)
            if (cso.Status == ConnectedSystemObjectStatus.PendingProvisioning)
            {
                cso.Status = ConnectedSystemObjectStatus.Normal;
            }
        }

        await _harness.DbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Clears all pending exports from the database.
    /// </summary>
    private async Task ClearPendingExportsAsync()
    {
        var pendingExports = await _harness.DbContext.PendingExports.ToListAsync();
        _harness.DbContext.PendingExports.RemoveRange(pendingExports);
        await _harness.DbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Sets up the entitlement scenario with Source (import) and Target (export) systems
    /// for testing drift detection with multi-valued reference attributes (group membership).
    /// </summary>
    private async Task SetUpEntitlementDriftDetectionScenarioAsync(bool enforceState)
    {
        // Create Source system (LDAP-like)
        await _harness.CreateConnectedSystemAsync("Source");
        await _harness.CreateObjectTypeAsync("Source", "User", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn"));

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
            .WithStringAttribute("cn"));

        await _harness.CreateObjectTypeAsync("Target", "Group", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn")
            .WithReferenceAttribute("member", isMultiValued: true));

        // Create MV types
        var personType = await _harness.CreateMetaverseObjectTypeAsync("Person", t => t
            .WithGuidAttribute("objectId")
            .WithStringAttribute("cn"));

        var groupType = await _harness.CreateMetaverseObjectTypeAsync("Group", t => t
            .WithGuidAttribute("objectId")
            .WithStringAttribute("cn")
            .WithReferenceAttribute("member", isMultiValued: true));

        // Get attributes for sync rules
        var sourceUserType = _harness.GetObjectType("Source", "User");
        var sourceGroupType = _harness.GetObjectType("Source", "Group");
        var targetUserType = _harness.GetObjectType("Target", "User");
        var targetGroupType = _harness.GetObjectType("Target", "Group");

        var sourceUserCn = sourceUserType.Attributes.First(a => a.Name == "cn");
        var sourceUserDn = sourceUserType.Attributes.First(a => a.Name == "distinguishedName");
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

        // Create Source import sync rules (Source is the contributor)
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

        // Create Target export sync rules (Target is export-only, non-contributor)
        await _harness.CreateSyncRuleAsync(
            "Target User Export",
            "Target",
            "User",
            personType,
            SyncRuleDirection.Export,
            r => r
                .WithProvisioning()
                .WithEnforceState(enforceState)
                .WithAttributeFlow(mvPersonCn, targetUserCn)
                .WithExpressionFlow("\"CN=\" + mv[\"cn\"] + \",OU=Users,DC=target,DC=local\"", targetUserDn));

        await _harness.CreateSyncRuleAsync(
            "Target Group Export",
            "Target",
            "Group",
            groupType,
            SyncRuleDirection.Export,
            r => r
                .WithProvisioning()
                .WithEnforceState(enforceState)
                .WithAttributeFlow(mvGroupCn, targetGroupCn)
                .WithAttributeFlow(mvGroupMember, targetGroupMember)
                .WithExpressionFlow("\"CN=\" + mv[\"cn\"] + \",OU=Groups,DC=target,DC=local\"", targetGroupDn));
    }

    /// <summary>
    /// Captures pending export data before export execution.
    /// </summary>
    private async Task<List<CapturedPendingExport>> CapturePendingExportDataAsync()
    {
        var pendingExports = await _harness.DbContext.PendingExports
            .Include(pe => pe.ConnectedSystemObject)
            .ThenInclude(cso => cso!.Type)
            .ThenInclude(csot => csot.Attributes)
            .Include(pe => pe.AttributeValueChanges)
            .ThenInclude(avc => avc.Attribute)
            .ToListAsync();

        var result = new List<CapturedPendingExport>();

        foreach (var pe in pendingExports)
        {
            if (pe.ConnectedSystemObject == null) continue;

            var captured = new CapturedPendingExport
            {
                CsoId = pe.ConnectedSystemObject.Id,
                ConnectedSystemId = pe.ConnectedSystemObject.ConnectedSystemId,
                CsoStatus = pe.ConnectedSystemObject.Status,
                AttributeValues = new List<CapturedAttributeValue>()
            };

            foreach (var avc in pe.AttributeValueChanges)
            {
                if (avc.Attribute == null) continue;

                captured.AttributeValues.Add(new CapturedAttributeValue
                {
                    AttributeId = avc.AttributeId,
                    AttributeName = avc.Attribute.Name,
                    AttributeType = avc.Attribute.Type,
                    StringValue = avc.StringValue,
                    IntValue = avc.IntValue,
                    DateTimeValue = avc.DateTimeValue,
                    ByteValue = avc.ByteValue,
                    UnresolvedReferenceValue = avc.UnresolvedReferenceValue
                });
            }

            result.Add(captured);
        }

        return result;
    }

    private class CapturedPendingExport
    {
        public Guid CsoId { get; set; }
        public int ConnectedSystemId { get; set; }
        public ConnectedSystemObjectStatus CsoStatus { get; set; }
        public List<CapturedAttributeValue> AttributeValues { get; set; } = new();
    }

    private class CapturedAttributeValue
    {
        public int AttributeId { get; set; }
        public string AttributeName { get; set; } = "";
        public AttributeDataType AttributeType { get; set; }
        public string? StringValue { get; set; }
        public int? IntValue { get; set; }
        public DateTime? DateTimeValue { get; set; }
        public byte[]? ByteValue { get; set; }
        public string? UnresolvedReferenceValue { get; set; }
    }

    /// <summary>
    /// Populates CSO attribute values from captured pending export data.
    /// </summary>
    private async Task PopulateEntitlementCsoAttributeValuesFromCapturedDataAsync(List<CapturedPendingExport> capturedData)
    {
        foreach (var captured in capturedData)
        {
            var cso = await _harness.DbContext.ConnectedSystemObjects
                .Include(c => c.AttributeValues)
                .FirstOrDefaultAsync(c => c.Id == captured.CsoId);

            if (cso == null) continue;

            foreach (var av in captured.AttributeValues)
            {
                // Handle reference attributes
                if (av.AttributeType == AttributeDataType.Reference && !string.IsNullOrEmpty(av.UnresolvedReferenceValue))
                {
                    if (Guid.TryParse(av.UnresolvedReferenceValue, out var mvoId))
                    {
                        // Find the CSO that represents this MVO in the target system
                        var referencedCso = await _harness.DbContext.ConnectedSystemObjects
                            .FirstOrDefaultAsync(c =>
                                c.MetaverseObjectId == mvoId &&
                                c.ConnectedSystemId == captured.ConnectedSystemId);

                        if (referencedCso != null)
                        {
                            var refAttrValue = new ConnectedSystemObjectAttributeValue
                            {
                                ConnectedSystemObject = cso,
                                AttributeId = av.AttributeId,
                                ReferenceValue = referencedCso
                            };
                            _harness.DbContext.ConnectedSystemObjectAttributeValues.Add(refAttrValue);
                        }
                    }
                }
                else
                {
                    // Non-reference attributes
                    var existingAttrValue = cso.AttributeValues
                        .FirstOrDefault(v => v.AttributeId == av.AttributeId);

                    if (existingAttrValue == null)
                    {
                        existingAttrValue = new ConnectedSystemObjectAttributeValue
                        {
                            ConnectedSystemObject = cso,
                            AttributeId = av.AttributeId
                        };
                        _harness.DbContext.ConnectedSystemObjectAttributeValues.Add(existingAttrValue);
                    }

                    existingAttrValue.StringValue = av.StringValue;
                    existingAttrValue.IntValue = av.IntValue;
                    existingAttrValue.DateTimeValue = av.DateTimeValue;
                    existingAttrValue.ByteValue = av.ByteValue;
                }
            }

            // Transition CSO from PendingProvisioning to Normal
            if (captured.CsoStatus == ConnectedSystemObjectStatus.PendingProvisioning)
            {
                cso.Status = ConnectedSystemObjectStatus.Normal;
            }
        }

        await _harness.DbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Populates CSO attribute values for entitlement scenario, including reference attributes.
    /// Handles multi-valued references by creating separate attribute values for each member.
    /// </summary>
    private async Task PopulateEntitlementCsoAttributeValuesAsync()
    {
        var pendingExports = await _harness.DbContext.PendingExports
            .Include(pe => pe.ConnectedSystemObject)
            .ThenInclude(cso => cso!.Type)
            .ThenInclude(csot => csot.Attributes)
            .Include(pe => pe.ConnectedSystemObject)
            .ThenInclude(cso => cso!.MetaverseObject)
            .ThenInclude(mvo => mvo!.AttributeValues)
            .Include(pe => pe.AttributeValueChanges)
            .ThenInclude(avc => avc.Attribute)
            .ToListAsync();

        foreach (var pe in pendingExports)
        {
            if (pe.ConnectedSystemObject == null) continue;

            var cso = pe.ConnectedSystemObject;

            foreach (var avc in pe.AttributeValueChanges)
            {
                if (avc.Attribute == null) continue;

                // Handle reference attributes differently - they may have multiple values
                if (avc.Attribute.Type == AttributeDataType.Reference && !string.IsNullOrEmpty(avc.UnresolvedReferenceValue))
                {
                    // For references, we need to resolve the MVO ID to a CSO reference
                    if (Guid.TryParse(avc.UnresolvedReferenceValue, out var mvoId))
                    {
                        // Find the CSO that represents this MVO in the target system
                        var referencedCso = await _harness.DbContext.ConnectedSystemObjects
                            .FirstOrDefaultAsync(c =>
                                c.MetaverseObjectId == mvoId &&
                                c.ConnectedSystemId == cso.ConnectedSystemId);

                        if (referencedCso != null)
                        {
                            // Create a reference attribute value pointing to the CSO
                            var refAttrValue = new ConnectedSystemObjectAttributeValue
                            {
                                ConnectedSystemObject = cso,
                                AttributeId = avc.AttributeId,
                                ReferenceValue = referencedCso
                            };
                            _harness.DbContext.ConnectedSystemObjectAttributeValues.Add(refAttrValue);
                        }
                    }
                }
                else
                {
                    // Find or create CSO attribute value for non-reference attributes
                    var existingAttrValue = await _harness.DbContext.ConnectedSystemObjectAttributeValues
                        .FirstOrDefaultAsync(av =>
                            av.ConnectedSystemObject.Id == cso.Id &&
                            av.AttributeId == avc.AttributeId);

                    if (existingAttrValue == null)
                    {
                        existingAttrValue = new ConnectedSystemObjectAttributeValue
                        {
                            ConnectedSystemObject = cso,
                            AttributeId = avc.AttributeId
                        };
                        _harness.DbContext.ConnectedSystemObjectAttributeValues.Add(existingAttrValue);
                    }

                    // Copy values from pending export
                    existingAttrValue.StringValue = avc.StringValue;
                    existingAttrValue.IntValue = avc.IntValue;
                    existingAttrValue.DateTimeValue = avc.DateTimeValue;
                    existingAttrValue.ByteValue = avc.ByteValue;
                }
            }

            // Transition CSO from PendingProvisioning to Normal
            if (cso.Status == ConnectedSystemObjectStatus.PendingProvisioning)
            {
                cso.Status = ConnectedSystemObjectStatus.Normal;
            }
        }

        await _harness.DbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Simulates drift in group membership by adding or removing a member directly in the Target CSO.
    /// This bypasses JIM and simulates an unauthorised change in the target system.
    /// </summary>
    private async Task SimulateMembershipDriftAsync(Guid groupObjectGuid, Guid userObjectGuid, bool addMember)
    {
        var targetSystem = _harness.GetConnectedSystem("Target");
        var sourceSystem = _harness.GetConnectedSystem("Source");

        // First, find the source CSO that has this objectGUID
        var sourceGroupCso = await _harness.DbContext.ConnectedSystemObjects
            .Include(cso => cso.AttributeValues)
            .FirstOrDefaultAsync(cso =>
                cso.ConnectedSystemId == sourceSystem.Id &&
                cso.AttributeValues.Any(av => av.AttributeId == cso.ExternalIdAttributeId && av.GuidValue == groupObjectGuid));

        if (sourceGroupCso == null)
        {
            throw new InvalidOperationException($"Source Group CSO with objectGUID {groupObjectGuid} not found");
        }

        // Find the target CSO joined to the same MVO
        var groupCso = await _harness.DbContext.ConnectedSystemObjects
            .Include(cso => cso.Type)
            .ThenInclude(csot => csot.Attributes)
            .Include(cso => cso.AttributeValues)
            .FirstOrDefaultAsync(cso =>
                cso.ConnectedSystemId == targetSystem.Id &&
                cso.MetaverseObjectId == sourceGroupCso.MetaverseObjectId);

        if (groupCso == null)
        {
            throw new InvalidOperationException($"Target Group CSO for MVO {sourceGroupCso.MetaverseObjectId} not found");
        }

        // Find the source user CSO
        var sourceUserCso = await _harness.DbContext.ConnectedSystemObjects
            .Include(cso => cso.AttributeValues)
            .FirstOrDefaultAsync(cso =>
                cso.ConnectedSystemId == sourceSystem.Id &&
                cso.AttributeValues.Any(av => av.AttributeId == cso.ExternalIdAttributeId && av.GuidValue == userObjectGuid));

        if (sourceUserCso == null)
        {
            throw new InvalidOperationException($"Source User CSO with objectGUID {userObjectGuid} not found");
        }

        // Find the target CSO for the user (joined to the same MVO)
        var userCso = await _harness.DbContext.ConnectedSystemObjects
            .FirstOrDefaultAsync(cso =>
                cso.ConnectedSystemId == targetSystem.Id &&
                cso.MetaverseObjectId == sourceUserCso.MetaverseObjectId);

        if (userCso == null)
        {
            throw new InvalidOperationException($"Target User CSO for MVO {sourceUserCso.MetaverseObjectId} not found");
        }

        var memberAttribute = groupCso.Type.Attributes.First(a => a.Name == "member");

        if (addMember)
        {
            // Add the user as a new member (simulating unauthorised addition)
            var newMemberValue = new ConnectedSystemObjectAttributeValue
            {
                ConnectedSystemObject = groupCso,
                AttributeId = memberAttribute.Id,
                ReferenceValue = userCso
            };
            _harness.DbContext.ConnectedSystemObjectAttributeValues.Add(newMemberValue);
        }
        else
        {
            // Remove the user from members (simulating unauthorised removal)
            var existingMemberValue = groupCso.AttributeValues
                .FirstOrDefault(av => av.AttributeId == memberAttribute.Id && av.ReferenceValue?.Id == userCso.Id);

            if (existingMemberValue != null)
            {
                _harness.DbContext.ConnectedSystemObjectAttributeValues.Remove(existingMemberValue);
            }
        }

        // Mark CSO as updated to trigger delta sync processing
        groupCso.LastUpdated = DateTime.UtcNow;

        await _harness.DbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Simulates an unauthorised change made directly in the target system (bypassing JIM).
    /// This modifies CSO attribute values to simulate drift.
    /// </summary>
    private async Task SimulateUnauthorisedCsoChangeAsync(string systemName, string attributeName, string newValue)
    {
        var system = _harness.GetConnectedSystem(systemName);

        var csos = await _harness.DbContext.ConnectedSystemObjects
            .Where(cso => cso.ConnectedSystemId == system.Id)
            .Include(cso => cso.Type)
            .ThenInclude(csot => csot.Attributes)
            .Include(cso => cso.AttributeValues)
            .ToListAsync();

        foreach (var cso in csos)
        {
            var attribute = cso.Type.Attributes.FirstOrDefault(a => a.Name == attributeName);
            if (attribute == null) continue;

            var attrValue = cso.AttributeValues.FirstOrDefault(av => av.AttributeId == attribute.Id);

            if (attrValue == null)
            {
                // Create new attribute value (simulating attribute added directly in target)
                attrValue = new ConnectedSystemObjectAttributeValue
                {
                    ConnectedSystemObject = cso,
                    AttributeId = attribute.Id
                };
                _harness.DbContext.ConnectedSystemObjectAttributeValues.Add(attrValue);
            }

            // Update to drifted value
            attrValue.StringValue = newValue;

            // Mark CSO as having been updated (to trigger delta sync processing)
            cso.LastUpdated = DateTime.UtcNow;
        }

        await _harness.DbContext.SaveChangesAsync();
    }

    #endregion
}
