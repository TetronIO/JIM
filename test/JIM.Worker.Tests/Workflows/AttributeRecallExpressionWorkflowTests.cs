using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Workflow tests for expression-based export mappings during attribute recall.
/// Verifies that when a source CSO goes obsolete (leaver scenario), expression-based
/// export mappings (e.g., DN expressions) are NOT re-evaluated against incomplete MVO state.
///
/// Previously, the DN expression would produce invalid values like "OU=,OU=Users,..."
/// because recalled attributes were missing from the MVO. The fix ensures expression
/// mappings are skipped during pure attribute recall, while direct mappings still
/// produce correct null-clearing changes.
/// </summary>
[TestFixture]
public class AttributeRecallExpressionWorkflowTests : WorkflowTestBase
{
    /// <summary>
    /// Full workflow test: source CSO import → full sync (project + provision) → mark obsolete →
    /// delta sync (recall + export evaluation) → verify expression-based DN mapping is skipped.
    /// </summary>
    [Test]
    public async Task AttributeRecall_ExpressionDnMapping_SkippedDuringRecallAsync()
    {
        // Arrange: Create source HR system
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "SourceUser");

        // Create target AD system with distinguishedName attribute
        var targetSystem = await CreateConnectedSystemAsync("AD Target");
        var targetDnAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName",
            Type = AttributeDataType.Text,
            IsSecondaryExternalId = true,
            Selected = true
        };
        var targetDisplayNameAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "DisplayName",
            Type = AttributeDataType.Text,
            Selected = true
        };
        var targetEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "EmployeeId",
            Type = AttributeDataType.Text,
            Selected = true
        };
        var targetExternalIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "ExternalId",
            Type = AttributeDataType.Guid,
            IsExternalId = true,
            Selected = true
        };
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "TargetUser", new List<ConnectedSystemObjectTypeAttribute>
        {
            targetExternalIdAttr,
            targetDnAttr,
            targetDisplayNameAttr,
            targetEmployeeIdAttr
        });

        // Create MVO type
        var mvType = await CreateMvObjectTypeAsync("Person");
        // Query attributes from DB to avoid EF Core in-memory auto-tracking duplicates on navigation property
        var mvDisplayNameAttr = await DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = await DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "EmployeeId");

        // Create import sync rule (source → MV)
        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");

        // Add import attribute flow mappings
        var sourceDisplayNameAttr = sourceType.Attributes.Single(a => a.Name == "DisplayName");
        var sourceEmployeeIdAttr = sourceType.Attributes.Single(a => a.Name == "EmployeeId");

        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = sourceDisplayNameAttr,
                ConnectedSystemAttributeId = sourceDisplayNameAttr.Id
            }}
        });
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = sourceEmployeeIdAttr,
                ConnectedSystemAttributeId = sourceEmployeeIdAttr.Id
            }}
        });
        await DbContext.SaveChangesAsync();

        // Create export sync rule (MV → target) with BOTH direct and expression mappings
        var exportRule = new SyncRule
        {
            ConnectedSystemId = targetSystem.Id,
            Name = "AD Export",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemObjectTypeId = targetType.Id,
            ConnectedSystemObjectType = targetType,
            MetaverseObjectTypeId = mvType.Id,
            MetaverseObjectType = mvType,
            ProvisionToConnectedSystem = true
        };

        // Direct mapping: Display Name
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                MetaverseAttribute = mvDisplayNameAttr,
                MetaverseAttributeId = mvDisplayNameAttr.Id
            }}
        });

        // Direct mapping: Employee ID
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetEmployeeIdAttr,
            TargetConnectedSystemAttributeId = targetEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                MetaverseAttribute = mvEmployeeIdAttr,
                MetaverseAttributeId = mvEmployeeIdAttr.Id
            }}
        });

        // Expression-based mapping: DN expression (the root cause of the bug)
        var dnMapping = new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDnAttr,
            TargetConnectedSystemAttributeId = targetDnAttr.Id
        };
        dnMapping.Sources.Add(new SyncRuleMappingSource
        {
            Order = 0,
            Expression = "\"CN=\" + mv[\"DisplayName\"] + \",OU=Users,DC=testdomain,DC=local\""
        });
        exportRule.AttributeFlowRules.Add(dnMapping);

        DbContext.SyncRules.Add(exportRule);
        await DbContext.SaveChangesAsync();

        // Create a source CSO (will be projected and provisioned)
        var sourceCso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        // Step 1: Full Sync on source system — projects MVO, evaluates export, creates provisioning CSO
        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(Jim, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        // Verify the MVO was created with attributes
        sourceCso = await DbContext.ConnectedSystemObjects
            .Include(c => c.MetaverseObject)
            .ThenInclude(m => m!.AttributeValues)
            .FirstAsync(c => c.Id == sourceCso.Id);
        Assert.That(sourceCso.MetaverseObjectId, Is.Not.Null, "Source CSO should be joined to MVO after Full Sync");
        var mvoId = sourceCso.MetaverseObjectId!.Value;

        // Verify a provisioning pending export was created with DN
        var provisioningExports = await DbContext.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .ThenInclude(avc => avc.Attribute)
            .Where(pe => pe.ConnectedSystemObject!.ConnectedSystemId == targetSystem.Id)
            .ToListAsync();
        Assert.That(provisioningExports, Has.Count.EqualTo(1), "Should create one provisioning export for target system");
        var provisioningDnChange = provisioningExports[0].AttributeValueChanges
            .FirstOrDefault(c => c.AttributeId == targetDnAttr.Id);
        Assert.That(provisioningDnChange, Is.Not.Null, "Provisioning export should include DN");
        Assert.That(provisioningDnChange!.StringValue, Is.EqualTo("CN=John Smith,OU=Users,DC=testdomain,DC=local"),
            "Provisioning DN should be correctly generated");

        // Simulate that the provisioning export was executed: update CSO to Normal status
        var targetCso = await DbContext.ConnectedSystemObjects
            .Include(c => c.AttributeValues)
            .FirstAsync(c => c.ConnectedSystemId == targetSystem.Id);
        targetCso.Status = ConnectedSystemObjectStatus.Normal;

        // Add the CSO attribute values as if the export was confirmed
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = targetDisplayNameAttr.Id,
            StringValue = "John Smith",
            ConnectedSystemObject = targetCso
        });
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = targetEmployeeIdAttr.Id,
            StringValue = "EMP001",
            ConnectedSystemObject = targetCso
        });
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = targetDnAttr.Id,
            StringValue = "CN=John Smith,OU=Users,DC=testdomain,DC=local",
            ConnectedSystemObject = targetCso
        });

        // Clear old provisioning pending exports
        DbContext.PendingExports.RemoveRange(provisioningExports);
        await DbContext.SaveChangesAsync();

        // Step 2: Mark source CSO as Obsolete (simulating leaver — user removed from HR)
        await MarkCsoAsObsoleteAsync(sourceCso);

        // Step 3: Delta Sync on source system — processes obsolete CSO, recalls attributes, evaluates exports
        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(Jim, sourceSystem, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        // Assert: Check pending exports created for the target system after recall
        var recallExports = await DbContext.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .ThenInclude(avc => avc.Attribute)
            .Where(pe => pe.ConnectedSystemObject!.ConnectedSystemId == targetSystem.Id)
            .ToListAsync();

        Assert.That(recallExports, Has.Count.EqualTo(1),
            "Expected exactly one Update pending export for the target system after attribute recall.");

        var recallExport = recallExports[0];
        Assert.That(recallExport.ChangeType, Is.EqualTo(JIM.Models.Transactional.PendingExportChangeType.Update),
            "Expected an Update pending export (direct-mapped attribute clearings).");

        // CRITICAL ASSERTION: The DN expression should NOT be in the pending export.
        // Previously, this would have produced "CN=,OU=Users,DC=testdomain,DC=local" (empty CN
        // because Display Name was recalled) which the LDAP server would reject.
        var dnChange = recallExport.AttributeValueChanges
            .FirstOrDefault(c => c.AttributeId == targetDnAttr.Id);
        Assert.That(dnChange, Is.Null,
            "Expression-based DN mapping should be SKIPPED during pure attribute recall. " +
            "Previously, this produced an invalid DN with empty RDN components.");

        // Assert: direct-mapped attributes should have null-clearing changes
        var displayNameChange = recallExport.AttributeValueChanges
            .FirstOrDefault(c => c.AttributeId == targetDisplayNameAttr.Id);
        Assert.That(displayNameChange, Is.Not.Null,
            "Direct-mapped Display Name should produce a null-clearing change.");
        Assert.That(displayNameChange!.StringValue, Is.Null,
            "Display Name change should be null (clearing the attribute).");

        var employeeIdChange = recallExport.AttributeValueChanges
            .FirstOrDefault(c => c.AttributeId == targetEmployeeIdAttr.Id);
        Assert.That(employeeIdChange, Is.Not.Null,
            "Direct-mapped Employee ID should produce a null-clearing change.");
        Assert.That(employeeIdChange!.StringValue, Is.Null,
            "Employee ID change should be null (clearing the attribute).");

        // Assert: Disconnected RPEI should have been created
        var disconnectedRpei = deltaSyncActivity.RunProfileExecutionItems
            .FirstOrDefault(r => r.ObjectChangeType == ObjectChangeType.Disconnected);
        Assert.That(disconnectedRpei, Is.Not.Null,
            "Delta Sync should produce a Disconnected RPEI when a joined CSO is obsoleted.");
        Assert.That(disconnectedRpei!.AttributeFlowCount, Is.GreaterThan(0),
            "Disconnected RPEI should show attribute flow count (recalled attributes).");
    }

    /// <summary>
    /// Marks a CSO as Obsolete (simulating a Delete from delta import).
    /// </summary>
    private async Task MarkCsoAsObsoleteAsync(ConnectedSystemObject cso)
    {
        var trackedCso = await DbContext.ConnectedSystemObjects.FindAsync(cso.Id);
        if (trackedCso != null)
        {
            trackedCso.Status = ConnectedSystemObjectStatus.Obsolete;
            trackedCso.LastUpdated = DateTime.UtcNow;
            await DbContext.SaveChangesAsync();
            cso.Status = trackedCso.Status;
            cso.LastUpdated = trackedCso.LastUpdated;
        }
    }
}
