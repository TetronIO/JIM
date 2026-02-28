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
/// Workflow tests for attribute recall with supplemental connected systems.
///
/// Validates the representative topology for attribute recall:
/// - HR Source (primary) — contributes identity-critical attributes (DisplayName, EmployeeId).
///   Attribute recall is DISABLED (default for primary sources that drive identity).
/// - Training Source (supplemental) — contributes non-critical attributes (Description).
///   Attribute recall is ENABLED (default — safe because these attributes don't feed expressions).
/// - AD Target — exports via direct mappings and an expression-based DN mapping.
///
/// When the Training CSO goes obsolete, the recalled Description attribute is cleared on the
/// target via a null-clearing export. The DN expression is still evaluated because its inputs
/// (HR-contributed attributes) remain on the MVO. If an admin misconfigures recall on a primary
/// source causing an invalid DN, the LDAP connector's DN validation catches it.
/// </summary>
[TestFixture]
public class AttributeRecallExpressionWorkflowTests : WorkflowTestBase
{
    /// <summary>
    /// Full workflow test using representative multi-source topology:
    /// HR import → Full Sync (project + provision) → Training import → Full Sync (join + contribute) →
    /// Mark Training CSO obsolete → Delta Sync → verify supplemental attributes cleared,
    /// identity attributes and expression outputs retained.
    /// </summary>
    [Test]
    public async Task AttributeRecall_SupplementalSource_ClearsRecalledAttributesAndRetainsExpressionOutputsAsync()
    {
        // Arrange: Create HR source system (primary — contributes identity attributes)
        var hrSystem = await CreateConnectedSystemAsync("HR Source");
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "HrUser");
        // Disable attribute recall on HR source — primary sources should not have recall enabled
        hrType.RemoveContributedAttributesOnObsoletion = false;
        await DbContext.SaveChangesAsync();

        // Create Training source system (supplemental — contributes non-critical attributes)
        // Uses SyncRule matching mode because the join rule is defined on the sync rule
        var trainingSystem = await CreateConnectedSystemAsync("Training Source");
        trainingSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;
        await DbContext.SaveChangesAsync();
        var trainingDescriptionAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "TrainingStatus",
            Type = AttributeDataType.Text,
            Selected = true
        };
        var trainingExternalIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "ExternalId",
            Type = AttributeDataType.Guid,
            IsExternalId = true,
            Selected = true
        };
        var trainingEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "EmployeeId",
            Type = AttributeDataType.Text,
            Selected = true
        };
        var trainingType = await CreateCsoTypeAsync(trainingSystem.Id, "TrainingRecord",
            new List<ConnectedSystemObjectTypeAttribute>
            {
                trainingExternalIdAttr,
                trainingDescriptionAttr,
                trainingEmployeeIdAttr
            });
        // Attribute recall ON for Training source (this is the default, being explicit for clarity)
        trainingType.RemoveContributedAttributesOnObsoletion = true;
        await DbContext.SaveChangesAsync();

        // Create target AD system
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
        var targetDescriptionAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "Description",
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
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "TargetUser",
            new List<ConnectedSystemObjectTypeAttribute>
            {
                targetExternalIdAttr,
                targetDnAttr,
                targetDisplayNameAttr,
                targetDescriptionAttr
            });

        // Create MVO type with Description attribute in addition to the defaults
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = await DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = await DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "EmployeeId");

        // Add Description attribute to the MV type
        var mvDescriptionAttr = new MetaverseAttribute
        {
            Name = "Description",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(mvDescriptionAttr);
        await DbContext.SaveChangesAsync();

        // Create HR import sync rule (HR Source → MV: DisplayName, EmployeeId)
        var hrImportRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        var hrDisplayNameAttr = hrType.Attributes.Single(a => a.Name == "DisplayName");
        var hrEmployeeIdAttr = hrType.Attributes.Single(a => a.Name == "EmployeeId");
        hrImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = hrImportRule,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = hrDisplayNameAttr,
                ConnectedSystemAttributeId = hrDisplayNameAttr.Id
            }}
        });
        hrImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = hrImportRule,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = hrEmployeeIdAttr,
                ConnectedSystemAttributeId = hrEmployeeIdAttr.Id
            }}
        });
        await DbContext.SaveChangesAsync();

        // Create Training import sync rule (Training Source → MV: Description)
        // Uses join (not projection) — joins to existing MVO via EmployeeId matching
        var trainingImportRule = await CreateImportSyncRuleAsync(
            trainingSystem.Id, trainingType, mvType, "Training Import", enableProjection: false);
        trainingImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = trainingImportRule,
            TargetMetaverseAttribute = mvDescriptionAttr,
            TargetMetaverseAttributeId = mvDescriptionAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = trainingDescriptionAttr,
                ConnectedSystemAttributeId = trainingDescriptionAttr.Id
            }}
        });
        // Add join rule: match on EmployeeId
        // CaseSensitive = true is required for in-memory DB tests (ILike is PostgreSQL-specific)
        trainingImportRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = trainingImportRule,
            SyncRuleId = trainingImportRule.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Order = 0,
                    ConnectedSystemAttribute = trainingEmployeeIdAttr,
                    ConnectedSystemAttributeId = trainingEmployeeIdAttr.Id
                }
            }
        });
        await DbContext.SaveChangesAsync();

        // Create export sync rule (MV → AD Target)
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

        // Direct mapping: DisplayName
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

        // Direct mapping: Description (contributed by Training source)
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDescriptionAttr,
            TargetConnectedSystemAttributeId = targetDescriptionAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                MetaverseAttribute = mvDescriptionAttr,
                MetaverseAttributeId = mvDescriptionAttr.Id
            }}
        });

        // Expression-based mapping: DN uses HR-contributed attributes
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

        // --- Step 1: Import HR source CSO ---
        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", "EMP001");

        // --- Step 2: Full Sync on HR source — projects MVO, evaluates export ---
        var hrFullSyncProfile = await CreateRunProfileAsync(hrSystem.Id, "HR Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var hrFullSyncActivity = await CreateActivityAsync(hrSystem.Id, hrFullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(Jim, hrSystem, hrFullSyncProfile, hrFullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        // Verify MVO was created with HR attributes
        hrCso = await DbContext.ConnectedSystemObjects
            .Include(c => c.MetaverseObject)
            .ThenInclude(m => m!.AttributeValues)
            .FirstAsync(c => c.Id == hrCso.Id);
        Assert.That(hrCso.MetaverseObjectId, Is.Not.Null, "HR CSO should be joined to MVO after Full Sync");
        var mvoId = hrCso.MetaverseObjectId!.Value;

        // Verify provisioning pending export was created with DN
        var provisioningExports = await DbContext.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .ThenInclude(avc => avc.Attribute)
            .Where(pe => pe.ConnectedSystemObject!.ConnectedSystemId == targetSystem.Id)
            .ToListAsync();
        Assert.That(provisioningExports, Has.Count.EqualTo(1), "Should create one provisioning export");
        var provisioningDnChange = provisioningExports[0].AttributeValueChanges
            .FirstOrDefault(c => c.AttributeId == targetDnAttr.Id);
        Assert.That(provisioningDnChange, Is.Not.Null, "Provisioning export should include DN");
        Assert.That(provisioningDnChange!.StringValue, Is.EqualTo("CN=John Smith,OU=Users,DC=testdomain,DC=local"));

        // Simulate that the provisioning export was executed
        var targetCso = await DbContext.ConnectedSystemObjects
            .Include(c => c.AttributeValues)
            .FirstAsync(c => c.ConnectedSystemId == targetSystem.Id);
        targetCso.Status = ConnectedSystemObjectStatus.Normal;
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = targetDisplayNameAttr.Id,
            StringValue = "John Smith",
            ConnectedSystemObject = targetCso
        });
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = targetDnAttr.Id,
            StringValue = "CN=John Smith,OU=Users,DC=testdomain,DC=local",
            ConnectedSystemObject = targetCso
        });
        DbContext.PendingExports.RemoveRange(provisioningExports);
        await DbContext.SaveChangesAsync();

        // --- Step 3: Import Training source CSO ---
        // CreateCsoAsync maps to "DisplayName" and "EmployeeId" attributes by name.
        // Training type doesn't have "DisplayName" but does have "EmployeeId" — the helper handles this.
        var trainingCso = await CreateCsoAsync(trainingSystem.Id, trainingType, "unused", "EMP001");
        // Set the TrainingStatus attribute (not covered by the helper)
        trainingCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = trainingDescriptionAttr.Id,
            StringValue = "Completed Advanced Training",
            ConnectedSystemObject = trainingCso
        });
        await DbContext.SaveChangesAsync();

        // --- Step 4: Full Sync on Training source — joins to existing MVO, contributes Description ---
        var trainingFullSyncProfile = await CreateRunProfileAsync(
            trainingSystem.Id, "Training Full Sync", ConnectedSystemRunType.FullSynchronisation);
        trainingSystem = await ReloadEntityAsync(trainingSystem);
        var trainingFullSyncActivity = await CreateActivityAsync(
            trainingSystem.Id, trainingFullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(Jim, trainingSystem, trainingFullSyncProfile, trainingFullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        // Verify Training CSO joined to the same MVO
        trainingCso = await DbContext.ConnectedSystemObjects
            .Include(c => c.MetaverseObject)
            .ThenInclude(m => m!.AttributeValues)
            .FirstAsync(c => c.Id == trainingCso.Id);
        Assert.That(trainingCso.MetaverseObjectId, Is.EqualTo(mvoId),
            "Training CSO should join to the same MVO as the HR CSO");

        // Verify MVO now has Description from Training
        var mvo = await DbContext.MetaverseObjects
            .Include(m => m.AttributeValues)
            .FirstAsync(m => m.Id == mvoId);
        var descriptionValue = mvo.AttributeValues.FirstOrDefault(av => av.AttributeId == mvDescriptionAttr.Id);
        Assert.That(descriptionValue, Is.Not.Null, "MVO should have Description attribute from Training");
        Assert.That(descriptionValue!.StringValue, Is.EqualTo("Completed Advanced Training"));

        // Simulate that Description export was executed
        targetCso = await DbContext.ConnectedSystemObjects
            .Include(c => c.AttributeValues)
            .FirstAsync(c => c.ConnectedSystemId == targetSystem.Id);
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = targetDescriptionAttr.Id,
            StringValue = "Completed Advanced Training",
            ConnectedSystemObject = targetCso
        });
        // Clear any pending exports from the Training sync
        var trainingExports = await DbContext.PendingExports
            .Where(pe => pe.ConnectedSystemObject!.ConnectedSystemId == targetSystem.Id)
            .ToListAsync();
        DbContext.PendingExports.RemoveRange(trainingExports);
        await DbContext.SaveChangesAsync();

        // --- Step 5: Mark Training CSO as Obsolete (training record removed) ---
        await MarkCsoAsObsoleteAsync(trainingCso);

        // --- Step 6: Delta Sync on Training source — processes obsolete CSO, recalls Training attributes ---
        var trainingDeltaSyncProfile = await CreateRunProfileAsync(
            trainingSystem.Id, "Training Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        trainingSystem = await ReloadEntityAsync(trainingSystem);
        var trainingDeltaSyncActivity = await CreateActivityAsync(
            trainingSystem.Id, trainingDeltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(Jim, trainingSystem, trainingDeltaSyncProfile, trainingDeltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        // --- Assert: Check pending exports after Training recall ---
        var recallExports = await DbContext.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .ThenInclude(avc => avc.Attribute)
            .Where(pe => pe.ConnectedSystemObject!.ConnectedSystemId == targetSystem.Id)
            .ToListAsync();

        Assert.That(recallExports, Has.Count.EqualTo(1),
            "Expected exactly one Update pending export for the target system after Training attribute recall");

        var recallExport = recallExports[0];
        Assert.That(recallExport.ChangeType, Is.EqualTo(JIM.Models.Transactional.PendingExportChangeType.Update),
            "Expected an Update pending export");

        // Description (Training-contributed) should be null-cleared
        var descriptionChange = recallExport.AttributeValueChanges
            .FirstOrDefault(c => c.AttributeId == targetDescriptionAttr.Id);
        Assert.That(descriptionChange, Is.Not.Null,
            "Description (Training-contributed) should produce a null-clearing change");
        Assert.That(descriptionChange!.StringValue, Is.Null,
            "Description change should be null (clearing the recalled attribute)");

        // DN expression should be evaluated but filtered out by no-net-change detection:
        // the expression result ("CN=John Smith,...") matches the existing CSO value because
        // HR-contributed attributes (which feed the DN expression) haven't changed.
        var dnChange = recallExport.AttributeValueChanges
            .FirstOrDefault(c => c.AttributeId == targetDnAttr.Id);
        Assert.That(dnChange, Is.Null,
            "DN should not be in the export — expression evaluates successfully but the value " +
            "hasn't changed (HR attributes unchanged), so no-net-change detection filters it out");

        // MVO should still retain HR-contributed attributes
        mvo = await DbContext.MetaverseObjects
            .Include(m => m.AttributeValues)
            .FirstAsync(m => m.Id == mvoId);
        var mvDisplayName = mvo.AttributeValues.FirstOrDefault(av => av.AttributeId == mvDisplayNameAttr.Id);
        Assert.That(mvDisplayName, Is.Not.Null, "DisplayName (HR-contributed) should be retained on MVO");
        Assert.That(mvDisplayName!.StringValue, Is.EqualTo("John Smith"));

        var mvEmployeeId = mvo.AttributeValues.FirstOrDefault(av => av.AttributeId == mvEmployeeIdAttr.Id);
        Assert.That(mvEmployeeId, Is.Not.Null, "EmployeeId (HR-contributed) should be retained on MVO");

        // Description should be recalled (removed from MVO)
        var mvDescription = mvo.AttributeValues.FirstOrDefault(av => av.AttributeId == mvDescriptionAttr.Id);
        Assert.That(mvDescription, Is.Null, "Description (Training-contributed) should be recalled from MVO");

        // Disconnected RPEI should be created for the Training CSO
        var disconnectedRpei = trainingDeltaSyncActivity.RunProfileExecutionItems
            .FirstOrDefault(r => r.ObjectChangeType == ObjectChangeType.Disconnected);
        Assert.That(disconnectedRpei, Is.Not.Null,
            "Delta Sync should produce a Disconnected RPEI when a joined CSO is obsoleted");
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
