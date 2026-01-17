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
