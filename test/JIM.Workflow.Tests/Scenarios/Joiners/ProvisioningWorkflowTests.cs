using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Workflow.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Workflow.Tests.Scenarios.Joiners;

/// <summary>
/// Workflow tests for provisioning scenarios.
/// These tests verify the complete provisioning cycle:
/// Source Import → Sync → Export Evaluation → Export → Confirming Import
///
/// Issue #234: Medium template integration test fails with ~540 DN lookup errors.
/// These tests help diagnose where in the provisioning flow the CSO FK is being lost.
/// </summary>
[TestFixture]
public class ProvisioningWorkflowTests
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

    /// <summary>
    /// Verifies the basic provisioning workflow with a small number of objects.
    /// This is the happy path test that should pass.
    /// </summary>
    [Test]
    public async Task ProvisioningWorkflow_SmallScale_CreatesAndConfirmsCsosAsync()
    {
        // Arrange: Set up source and target systems
        await SetUpProvisioningScenarioAsync(objectCount: 10);

        // Take initial snapshot
        await _harness.TakeSnapshotAsync("Initial");

        // Act Step 1: Import from source system
        var sourceConnector = _harness.GetConnector("HR");
        sourceConnector.QueueImportObjects(GenerateSourceUsers(10));

        await _harness.ExecuteFullImportAsync("HR");
        var afterImport = await _harness.TakeSnapshotAsync("After Source Import");

        // Assert Step 1: CSOs created in source system
        Assert.That(afterImport.GetCsos("HR").Count, Is.EqualTo(10),
            "Should have 10 CSOs in HR system after import");

        // Act Step 2: Full sync to create MVOs
        await _harness.ExecuteFullSyncAsync("HR");
        var afterSync = await _harness.TakeSnapshotAsync("After Full Sync");

        // Assert Step 2: MVOs created
        Assert.That(afterSync.MvoCount, Is.EqualTo(10),
            "Should have 10 MVOs after sync");

        // Act Step 3: Export evaluation (creates PendingExports and PendingProvisioning CSOs)
        await _harness.ExecuteExportEvaluationAsync("HR");
        var afterExportEval = await _harness.TakeSnapshotAsync("After Export Evaluation");

        // Assert Step 3: PendingExports created with CSO FKs
        Assert.That(afterExportEval.PendingExportCount, Is.EqualTo(10),
            "Should have 10 PendingExports for AD provisioning");

        var pendingExportsWithNullCso = afterExportEval.GetPendingExportsWithNullCsoFk();
        Assert.That(pendingExportsWithNullCso, Is.Empty,
            $"All PendingExports should have CSO FK set. Found {pendingExportsWithNullCso.Count} with NULL CSO FK.");

        // Verify PendingProvisioning CSOs created
        var pendingProvisioningCsos = afterExportEval.GetCsosWithStatus(ConnectedSystemObjectStatus.PendingProvisioning);
        Assert.That(pendingProvisioningCsos.Count, Is.EqualTo(10),
            "Should have 10 PendingProvisioning CSOs in AD system");

        // Act Step 4: Execute exports
        await _harness.ExecuteExportAsync("AD");
        var afterExport = await _harness.TakeSnapshotAsync("After Export");

        // Assert Step 4: PendingExports marked as Exported
        var exportedPendingExports = afterExport.GetPendingExportsWithStatus(PendingExportStatus.Exported);
        Assert.That(exportedPendingExports.Count, Is.EqualTo(10),
            "All 10 PendingExports should be in Exported status");

        // Act Step 5: Confirming import from AD
        var adConnector = _harness.GetConnector("AD");

        // Configure confirming import to return objects matching what we exported
        adConnector.WithConfirmingImportFactory(pe => GenerateConfirmingImportObject(pe));
        adConnector.QueueConfirmingImport();

        await _harness.ExecuteConfirmingImportAsync("AD");
        var afterConfirmingImport = await _harness.TakeSnapshotAsync("After Confirming Import");

        // Assert Step 5: CSOs transitioned to Normal, PendingExports reconciled
        var stillPendingProvisioning = afterConfirmingImport.GetCsosWithStatus(ConnectedSystemObjectStatus.PendingProvisioning);
        Assert.That(stillPendingProvisioning, Is.Empty,
            $"All CSOs should be Normal after confirming import. Found {stillPendingProvisioning.Count} still PendingProvisioning.");

        var normalCsos = afterConfirmingImport.GetCsos("AD").Where(c => c.Status == ConnectedSystemObjectStatus.Normal).ToList();
        Assert.That(normalCsos.Count, Is.EqualTo(10),
            "Should have 10 Normal CSOs in AD system after confirming import");

        // PendingExports should be deleted after successful confirmation
        Assert.That(afterConfirmingImport.PendingExportCount, Is.EqualTo(0),
            "All PendingExports should be deleted after successful confirmation");
    }

    /// <summary>
    /// Tests the provisioning workflow at medium scale (1000 objects).
    /// This replicates the scale of the failing integration test in issue #234.
    /// </summary>
    [Test]
    public async Task ProvisioningWorkflow_MediumScale_AllPendingExportsHaveCsoFkAsync()
    {
        const int objectCount = 1000;

        // Arrange
        await SetUpProvisioningScenarioAsync(objectCount);

        // Take initial snapshot
        await _harness.TakeSnapshotAsync("Initial");

        // Act Step 1: Import from source
        var sourceConnector = _harness.GetConnector("HR");
        sourceConnector.QueueImportObjects(GenerateSourceUsers(objectCount));

        await _harness.ExecuteFullImportAsync("HR");
        var afterImport = await _harness.TakeSnapshotAsync("After Source Import");

        Assert.That(afterImport.GetCsos("HR").Count, Is.EqualTo(objectCount),
            $"Should have {objectCount} CSOs in HR system");

        // Act Step 2: Full sync
        await _harness.ExecuteFullSyncAsync("HR");
        var afterSync = await _harness.TakeSnapshotAsync("After Full Sync");

        Assert.That(afterSync.MvoCount, Is.EqualTo(objectCount),
            $"Should have {objectCount} MVOs");

        // Act Step 3: Export evaluation - THIS IS WHERE ISSUE #234 SYMPTOMS APPEAR
        await _harness.ExecuteExportEvaluationAsync("HR");
        var afterExportEval = await _harness.TakeSnapshotAsync("After Export Evaluation");

        // KEY ASSERTION FOR ISSUE #234
        var pendingExportsWithNullCso = afterExportEval.GetPendingExportsWithNullCsoFk();

        if (pendingExportsWithNullCso.Count > 0)
        {
            Console.WriteLine($"=== ISSUE #234 REPRODUCED ===");
            Console.WriteLine($"Found {pendingExportsWithNullCso.Count} PendingExports with NULL CSO FK");
            Console.WriteLine($"Total PendingExports: {afterExportEval.PendingExportCount}");
            Console.WriteLine($"PendingProvisioning CSOs: {afterExportEval.GetCsosWithStatus(ConnectedSystemObjectStatus.PendingProvisioning).Count}");

            // Print first few for debugging
            foreach (var pe in pendingExportsWithNullCso.Take(5))
            {
                Console.WriteLine($"  PE {pe.Id}: ChangeType={pe.ChangeType}, Status={pe.Status}, MvoId={pe.SourceMetaverseObjectId}");
            }
        }

        Assert.That(pendingExportsWithNullCso, Is.Empty,
            $"ISSUE #234: {pendingExportsWithNullCso.Count} of {afterExportEval.PendingExportCount} PendingExports have NULL CSO FK");

        // Verify all PendingProvisioning CSOs were created
        var pendingProvisioningCsos = afterExportEval.GetCsosWithStatus(ConnectedSystemObjectStatus.PendingProvisioning);
        Assert.That(pendingProvisioningCsos.Count, Is.EqualTo(objectCount),
            $"Should have {objectCount} PendingProvisioning CSOs");
    }

    /// <summary>
    /// Tests that CSOs are not incorrectly deleted during confirming import.
    /// This tests the deletion detection logic that was identified as problematic in issue #234.
    ///
    /// KNOWN ISSUE: This test currently fails because PendingProvisioning CSOs are incorrectly
    /// being marked as Obsolete during confirming import deletion detection. This is a bug
    /// that needs to be fixed - see GitHub issue #234.
    /// </summary>
    [Test]
    [Explicit("Known issue #234: PendingProvisioning CSOs incorrectly marked Obsolete during confirming import")]
    public async Task ProvisioningWorkflow_ConfirmingImport_DoesNotDeletePendingProvisioningCsosAsync()
    {
        const int objectCount = 100;

        // Arrange
        await SetUpProvisioningScenarioAsync(objectCount);

        // Import, sync, export eval
        var sourceConnector = _harness.GetConnector("HR");
        sourceConnector.QueueImportObjects(GenerateSourceUsers(objectCount));

        await _harness.ExecuteFullImportAsync("HR");
        await _harness.ExecuteFullSyncAsync("HR");
        await _harness.ExecuteExportEvaluationAsync("HR");

        var beforeExport = await _harness.TakeSnapshotAsync("Before Export");

        // Execute exports
        await _harness.ExecuteExportAsync("AD");

        // Set up confirming import - but only return HALF the objects
        // This simulates a partial import that might trigger deletion logic
        var adConnector = _harness.GetConnector("AD");
        var exportedItems = adConnector.ExportedItems.Take(objectCount / 2).ToList();

        var confirmingObjects = exportedItems
            .Where(pe => pe.ChangeType == PendingExportChangeType.Create)
            .Select(pe => GenerateConfirmingImportObject(pe))
            .ToList();

        adConnector.QueueImportObjects(confirmingObjects);

        await _harness.ExecuteConfirmingImportAsync("AD");
        var afterConfirmingImport = await _harness.TakeSnapshotAsync("After Partial Confirming Import");

        // Assert: PendingProvisioning CSOs should NOT be deleted by deletion detection
        // They should be excluded from the deletion logic
        var adCsoCount = afterConfirmingImport.GetCsos("AD").Count;
        var obsoleteCsos = afterConfirmingImport.GetCsosWithStatus(ConnectedSystemObjectStatus.Obsolete);

        Assert.That(obsoleteCsos, Is.Empty,
            $"No CSOs should be marked Obsolete. PendingProvisioning CSOs should be excluded from deletion detection. Found {obsoleteCsos.Count} Obsolete.");

        // All CSOs should still exist
        Assert.That(adCsoCount, Is.EqualTo(objectCount),
            $"All {objectCount} CSOs should still exist. Found {adCsoCount}.");
    }

    /// <summary>
    /// Verifies the step-by-step state transitions during provisioning.
    /// Useful for detailed debugging of the provisioning flow.
    /// </summary>
    [Test]
    public async Task ProvisioningWorkflow_StateTransitions_AreCorrectAsync()
    {
        const int objectCount = 5; // Small number for detailed inspection

        // Arrange
        await SetUpProvisioningScenarioAsync(objectCount);

        var sourceConnector = _harness.GetConnector("HR");
        sourceConnector.QueueImportObjects(GenerateSourceUsers(objectCount));

        // Step 1: Import
        await _harness.ExecuteFullImportAsync("HR");
        var s1 = await _harness.TakeSnapshotAsync("Step 1: After Import");

        Console.WriteLine("=== Step 1: After Import ===");
        Console.WriteLine($"HR CSOs: {s1.GetCsos("HR").Count}");
        Console.WriteLine($"AD CSOs: {s1.GetCsos("AD").Count}");
        Console.WriteLine($"MVOs: {s1.MvoCount}");
        Console.WriteLine($"PendingExports: {s1.PendingExportCount}");

        Assert.That(s1.GetCsos("HR").Count, Is.EqualTo(objectCount));
        Assert.That(s1.GetCsos("AD").Count, Is.EqualTo(0), "No AD CSOs before sync");
        Assert.That(s1.MvoCount, Is.EqualTo(0), "No MVOs before sync");

        // Step 2: Sync
        await _harness.ExecuteFullSyncAsync("HR");
        var s2 = await _harness.TakeSnapshotAsync("Step 2: After Sync");

        Console.WriteLine("=== Step 2: After Sync ===");
        Console.WriteLine($"HR CSOs: {s2.GetCsos("HR").Count}");
        Console.WriteLine($"AD CSOs: {s2.GetCsos("AD").Count}");
        Console.WriteLine($"MVOs: {s2.MvoCount}");
        Console.WriteLine($"PendingExports: {s2.PendingExportCount}");

        Assert.That(s2.MvoCount, Is.EqualTo(objectCount), "MVOs created by sync");
        Assert.That(s2.GetCsos("AD").Count, Is.EqualTo(0), "No AD CSOs until export eval");

        // Step 3: Export Evaluation
        await _harness.ExecuteExportEvaluationAsync("HR");
        var s3 = await _harness.TakeSnapshotAsync("Step 3: After Export Eval");

        Console.WriteLine("=== Step 3: After Export Eval ===");
        Console.WriteLine($"HR CSOs: {s3.GetCsos("HR").Count}");
        Console.WriteLine($"AD CSOs: {s3.GetCsos("AD").Count}");
        Console.WriteLine($"  PendingProvisioning: {s3.GetCsosWithStatus(ConnectedSystemObjectStatus.PendingProvisioning).Count}");
        Console.WriteLine($"MVOs: {s3.MvoCount}");
        Console.WriteLine($"PendingExports: {s3.PendingExportCount}");
        Console.WriteLine($"  With NULL CSO FK: {s3.GetPendingExportsWithNullCsoFk().Count}");

        Assert.That(s3.GetCsos("AD").Count, Is.EqualTo(objectCount), "AD CSOs created for provisioning");
        Assert.That(s3.GetCsosWithStatus(ConnectedSystemObjectStatus.PendingProvisioning).Count, Is.EqualTo(objectCount));
        Assert.That(s3.PendingExportCount, Is.EqualTo(objectCount));
        Assert.That(s3.GetPendingExportsWithNullCsoFk(), Is.Empty, "All PendingExports should have CSO FK");

        // Print detailed PendingExport info
        foreach (var pe in s3.PendingExports)
        {
            Console.WriteLine($"  PE {pe.Id}: CsoId={pe.ConnectedSystemObjectId}, ChangeType={pe.ChangeType}");
        }
    }

    #region Setup Helpers

    private async Task SetUpProvisioningScenarioAsync(int objectCount)
    {
        // Create HR (source) system
        await _harness.CreateConnectedSystemAsync("HR");
        await _harness.CreateObjectTypeAsync("HR", "User", t => t
            .WithGuidExternalId("employeeId")
            .WithStringAttribute("displayName")
            .WithStringAttribute("department"));

        // Create AD (target) system
        await _harness.CreateConnectedSystemAsync("AD");
        await _harness.CreateObjectTypeAsync("AD", "User", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn")
            .WithStringAttribute("department"));

        // Create MV type
        var personType = await _harness.CreateMetaverseObjectTypeAsync("Person", t => t
            .WithGuidAttribute("employeeId")
            .WithStringAttribute("displayName")
            .WithStringAttribute("department"));

        // Get attributes for flow rules
        var hrUserType = _harness.GetObjectType("HR", "User");
        var adUserType = _harness.GetObjectType("AD", "User");

        var hrEmployeeId = hrUserType.Attributes.First(a => a.Name == "employeeId");
        var hrDisplayName = hrUserType.Attributes.First(a => a.Name == "displayName");

        var adCn = adUserType.Attributes.First(a => a.Name == "cn");
        var adDn = adUserType.Attributes.First(a => a.Name == "distinguishedName");

        // Get MV attributes
        var mvEmployeeId = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "employeeId");
        var mvDisplayName = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "displayName");

        // Create import sync rule (HR → MV)
        await _harness.CreateSyncRuleAsync(
            "HR Import",
            "HR",
            "User",
            personType,
            SyncRuleDirection.Import,
            r => r.WithProjection());

        // Create export sync rule (MV → AD) with provisioning enabled
        await _harness.CreateSyncRuleAsync(
            "AD Export",
            "AD",
            "User",
            personType,
            SyncRuleDirection.Export,
            r => r
                .WithProvisioning()
                .WithAttributeFlow(mvDisplayName, adCn)
                .WithExpressionFlow("\"CN=\" + displayName + \",OU=Users,DC=test,DC=local\"", adDn));
    }

    private List<ConnectedSystemImportObject> GenerateSourceUsers(int count)
    {
        var users = new List<ConnectedSystemImportObject>();

        for (int i = 0; i < count; i++)
        {
            users.Add(new ConnectedSystemImportObject
            {
                ChangeType = ObjectChangeType.Create,
                ObjectType = "User",
                Attributes = new List<ConnectedSystemImportObjectAttribute>
                {
                    new()
                    {
                        Name = "employeeId",
                        GuidValues = new List<Guid> { Guid.NewGuid() }
                    },
                    new()
                    {
                        Name = "displayName",
                        StringValues = new List<string> { $"User {i}" }
                    },
                    new()
                    {
                        Name = "department",
                        StringValues = new List<string> { $"Dept {i % 10}" }
                    }
                }
            });
        }

        return users;
    }

    private ConnectedSystemImportObject GenerateConfirmingImportObject(PendingExport pe)
    {
        // Generate an import object that confirms the export was successful
        // The primary external ID is system-assigned (simulating AD's objectGUID)
        // The secondary external ID (DN) should match what we exported

        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Create,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new()
                {
                    Name = "objectGUID",
                    GuidValues = new List<Guid> { Guid.NewGuid() } // System-assigned
                },
                new()
                {
                    Name = "distinguishedName",
                    StringValues = new List<string>
                    {
                        // Match the DN we exported
                        pe.AttributeValueChanges
                            .FirstOrDefault(avc => avc.Attribute?.Name == "distinguishedName")
                            ?.StringValue ?? $"CN=Unknown,OU=Users,DC=test,DC=local"
                    }
                },
                new()
                {
                    Name = "cn",
                    StringValues = new List<string>
                    {
                        pe.AttributeValueChanges
                            .FirstOrDefault(avc => avc.Attribute?.Name == "cn")
                            ?.StringValue ?? "Unknown"
                    }
                }
            }
        };
    }

    #endregion
}
