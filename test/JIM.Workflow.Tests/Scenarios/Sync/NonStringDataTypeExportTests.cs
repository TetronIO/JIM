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
/// Workflow tests for non-string data type exports.
/// These tests verify that all data types (Guid, Bool, Int, DateTime, Binary) are
/// properly handled through the export pipeline:
/// Source Import → Sync → Export Evaluation → Export → Confirming Import
///
/// Issue: Guid and Bool were previously not first-class citizens through the export
/// process - they were converted to strings causing reconciliation failures.
/// </summary>
[TestFixture]
public class NonStringDataTypeExportTests
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
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
        {
            Console.WriteLine("=== SNAPSHOT DIAGNOSTICS ===");
            _harness.PrintSnapshotSummaries();
        }

        _harness?.Dispose();
    }

    /// <summary>
    /// Verifies that Boolean attribute values are correctly exported and reconciled.
    /// </summary>
    [Test]
    public async Task BoolExport_ValueCorrectlyExportedAndReconciledAsync()
    {
        // Arrange: Set up source and target systems with bool attribute
        await SetUpBoolExportScenarioAsync();
        await _harness.TakeSnapshotAsync("Initial");

        // Act Step 1: Import from source system with bool value = true
        var sourceConnector = _harness.GetConnector("HR");
        sourceConnector.QueueImportObjects(new List<ConnectedSystemImportObject>
        {
            new()
            {
                ChangeType = ObjectChangeType.NotSet,
                ObjectType = "User",
                Attributes = new List<ConnectedSystemImportObjectAttribute>
                {
                    new() { Name = "employeeId", GuidValues = new List<Guid> { Guid.NewGuid() } },
                    new() { Name = "displayName", StringValues = new List<string> { "Test User 1" } },
                    new() { Name = "isActive", BoolValue = true }
                }
            }
        });

        await _harness.ExecuteFullImportAsync("HR");
        var afterImport = await _harness.TakeSnapshotAsync("After Source Import");

        Assert.That(afterImport.GetCsos("HR").Count, Is.EqualTo(1),
            "Should have 1 CSO in HR system after import");

        // Act Step 2: Full sync
        await _harness.ExecuteFullSyncAsync("HR");
        var afterSync = await _harness.TakeSnapshotAsync("After Full Sync");

        Assert.That(afterSync.MvoCount, Is.EqualTo(1), "Should have 1 MVO after sync");
        Assert.That(afterSync.PendingExportCount, Is.EqualTo(1), "Should have 1 PendingExport");

        // Assert: PendingExport has BoolValue set (not converted to string)
        // Query the actual PendingExport from database with AttributeValueChanges included
        var pendingExport = await _harness.DbContext.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .ThenInclude(avc => avc.Attribute)
            .FirstAsync();

        var isActiveChange = pendingExport.AttributeValueChanges
            .FirstOrDefault(avc => avc.Attribute?.Name == "enabled");

        Assert.That(isActiveChange, Is.Not.Null, "Should have enabled attribute value change");
        Assert.That(isActiveChange!.BoolValue, Is.EqualTo(true),
            "BoolValue should be set to true (not converted to string)");
        Assert.That(isActiveChange.StringValue, Is.Null,
            "StringValue should be null (bool should use BoolValue property)");

        // Act Step 3: Execute exports
        await _harness.ExecuteExportAsync("AD");
        var afterExport = await _harness.TakeSnapshotAsync("After Export");

        // Act Step 4: Confirming import
        var adConnector = _harness.GetConnector("AD");
        adConnector.WithConfirmingImportFactory(pe => GenerateConfirmingImportObjectWithBool(pe));
        adConnector.QueueConfirmingImport();

        await _harness.ExecuteConfirmingImportAsync("AD");
        var afterConfirmingImport = await _harness.TakeSnapshotAsync("After Confirming Import");

        // Assert: PendingExport should be reconciled and deleted
        Assert.That(afterConfirmingImport.PendingExportCount, Is.EqualTo(0),
            "PendingExport should be deleted after successful boolean reconciliation");
    }

    /// <summary>
    /// Verifies that Guid attribute values are correctly exported and reconciled.
    /// </summary>
    [Test]
    public async Task GuidExport_ValueCorrectlyExportedAndReconciledAsync()
    {
        // Arrange: Set up source and target systems with guid attribute
        await SetUpGuidExportScenarioAsync();
        await _harness.TakeSnapshotAsync("Initial");

        var testGuid = Guid.NewGuid();

        // Act Step 1: Import from source system with guid value
        var sourceConnector = _harness.GetConnector("HR");
        sourceConnector.QueueImportObjects(new List<ConnectedSystemImportObject>
        {
            new()
            {
                ChangeType = ObjectChangeType.NotSet,
                ObjectType = "User",
                Attributes = new List<ConnectedSystemImportObjectAttribute>
                {
                    new() { Name = "employeeId", GuidValues = new List<Guid> { Guid.NewGuid() } },
                    new() { Name = "displayName", StringValues = new List<string> { "Test User 1" } },
                    new() { Name = "correlationId", GuidValues = new List<Guid> { testGuid } }
                }
            }
        });

        await _harness.ExecuteFullImportAsync("HR");
        var afterImport = await _harness.TakeSnapshotAsync("After Source Import");

        Assert.That(afterImport.GetCsos("HR").Count, Is.EqualTo(1),
            "Should have 1 CSO in HR system after import");

        // Act Step 2: Full sync
        await _harness.ExecuteFullSyncAsync("HR");
        var afterSync = await _harness.TakeSnapshotAsync("After Full Sync");

        Assert.That(afterSync.MvoCount, Is.EqualTo(1), "Should have 1 MVO after sync");
        Assert.That(afterSync.PendingExportCount, Is.EqualTo(1), "Should have 1 PendingExport");

        // Assert: PendingExport has GuidValue set (not converted to string)
        var pendingExport = await _harness.DbContext.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .ThenInclude(avc => avc.Attribute)
            .FirstAsync();

        var correlationIdChange = pendingExport.AttributeValueChanges
            .FirstOrDefault(avc => avc.Attribute?.Name == "externalCorrelationId");

        Assert.That(correlationIdChange, Is.Not.Null, "Should have externalCorrelationId attribute value change");
        Assert.That(correlationIdChange!.GuidValue, Is.EqualTo(testGuid),
            "GuidValue should be set correctly (not converted to string)");
        Assert.That(correlationIdChange.StringValue, Is.Null,
            "StringValue should be null (guid should use GuidValue property)");

        // Act Step 3: Execute exports
        await _harness.ExecuteExportAsync("AD");
        var afterExport = await _harness.TakeSnapshotAsync("After Export");

        // Act Step 4: Confirming import
        var adConnector = _harness.GetConnector("AD");
        adConnector.WithConfirmingImportFactory(pe => GenerateConfirmingImportObjectWithGuid(pe, testGuid));
        adConnector.QueueConfirmingImport();

        await _harness.ExecuteConfirmingImportAsync("AD");
        var afterConfirmingImport = await _harness.TakeSnapshotAsync("After Confirming Import");

        // Assert: PendingExport should be reconciled and deleted
        Assert.That(afterConfirmingImport.PendingExportCount, Is.EqualTo(0),
            "PendingExport should be deleted after successful guid reconciliation");
    }

    /// <summary>
    /// Verifies that DateTime attribute values are correctly exported and reconciled.
    /// </summary>
    [Test]
    public async Task DateTimeExport_ValueCorrectlyExportedAndReconciledAsync()
    {
        // Arrange
        await SetUpDateTimeExportScenarioAsync();
        await _harness.TakeSnapshotAsync("Initial");

        var testDate = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc);

        // Act Step 1: Import from source
        var sourceConnector = _harness.GetConnector("HR");
        sourceConnector.QueueImportObjects(new List<ConnectedSystemImportObject>
        {
            new()
            {
                ChangeType = ObjectChangeType.NotSet,
                ObjectType = "User",
                Attributes = new List<ConnectedSystemImportObjectAttribute>
                {
                    new() { Name = "employeeId", GuidValues = new List<Guid> { Guid.NewGuid() } },
                    new() { Name = "displayName", StringValues = new List<string> { "Test User 1" } },
                    new() { Name = "startDate", DateTimeValue = testDate }
                }
            }
        });

        await _harness.ExecuteFullImportAsync("HR");
        await _harness.ExecuteFullSyncAsync("HR");
        var afterSync = await _harness.TakeSnapshotAsync("After Full Sync");

        // Assert: PendingExport has DateTimeValue set
        var pendingExport = await _harness.DbContext.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .ThenInclude(avc => avc.Attribute)
            .FirstAsync();

        var startDateChange = pendingExport.AttributeValueChanges
            .FirstOrDefault(avc => avc.Attribute?.Name == "hireDate");

        Assert.That(startDateChange, Is.Not.Null, "Should have hireDate attribute value change");
        Assert.That(startDateChange!.DateTimeValue, Is.EqualTo(testDate),
            "DateTimeValue should be set correctly");

        // Export and confirm
        await _harness.ExecuteExportAsync("AD");

        var adConnector = _harness.GetConnector("AD");
        adConnector.WithConfirmingImportFactory(pe => GenerateConfirmingImportObjectWithDateTime(pe, testDate));
        adConnector.QueueConfirmingImport();

        await _harness.ExecuteConfirmingImportAsync("AD");
        var afterConfirmingImport = await _harness.TakeSnapshotAsync("After Confirming Import");

        Assert.That(afterConfirmingImport.PendingExportCount, Is.EqualTo(0),
            "PendingExport should be deleted after successful datetime reconciliation");
    }

    /// <summary>
    /// Verifies that Integer (Number) attribute values are correctly exported and reconciled.
    /// </summary>
    [Test]
    public async Task IntExport_ValueCorrectlyExportedAndReconciledAsync()
    {
        // Arrange
        await SetUpIntExportScenarioAsync();
        await _harness.TakeSnapshotAsync("Initial");

        const int testNumber = 12345;

        // Act Step 1: Import from source
        var sourceConnector = _harness.GetConnector("HR");
        sourceConnector.QueueImportObjects(new List<ConnectedSystemImportObject>
        {
            new()
            {
                ChangeType = ObjectChangeType.NotSet,
                ObjectType = "User",
                Attributes = new List<ConnectedSystemImportObjectAttribute>
                {
                    new() { Name = "employeeId", GuidValues = new List<Guid> { Guid.NewGuid() } },
                    new() { Name = "displayName", StringValues = new List<string> { "Test User 1" } },
                    new() { Name = "badgeNumber", IntValues = new List<int> { testNumber } }
                }
            }
        });

        await _harness.ExecuteFullImportAsync("HR");
        await _harness.ExecuteFullSyncAsync("HR");
        var afterSync = await _harness.TakeSnapshotAsync("After Full Sync");

        // Assert: PendingExport has IntValue set
        var pendingExport = await _harness.DbContext.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .ThenInclude(avc => avc.Attribute)
            .FirstAsync();

        var badgeChange = pendingExport.AttributeValueChanges
            .FirstOrDefault(avc => avc.Attribute?.Name == "employeeNumber");

        Assert.That(badgeChange, Is.Not.Null, "Should have employeeNumber attribute value change");
        Assert.That(badgeChange!.IntValue, Is.EqualTo(testNumber),
            "IntValue should be set correctly");

        // Export and confirm
        await _harness.ExecuteExportAsync("AD");

        var adConnector = _harness.GetConnector("AD");
        adConnector.WithConfirmingImportFactory(pe => GenerateConfirmingImportObjectWithInt(pe, testNumber));
        adConnector.QueueConfirmingImport();

        await _harness.ExecuteConfirmingImportAsync("AD");
        var afterConfirmingImport = await _harness.TakeSnapshotAsync("After Confirming Import");

        Assert.That(afterConfirmingImport.PendingExportCount, Is.EqualTo(0),
            "PendingExport should be deleted after successful int reconciliation");
    }

    #region Setup Helpers

    private async Task SetUpBoolExportScenarioAsync()
    {
        // Create HR (source) system
        await _harness.CreateConnectedSystemAsync("HR");
        await _harness.CreateObjectTypeAsync("HR", "User", t => t
            .WithGuidExternalId("employeeId")
            .WithStringAttribute("displayName")
            .WithBoolAttribute("isActive"));

        // Create AD (target) system
        await _harness.CreateConnectedSystemAsync("AD");
        await _harness.CreateObjectTypeAsync("AD", "User", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn")
            .WithBoolAttribute("enabled"));

        // Create MV type
        var personType = await _harness.CreateMetaverseObjectTypeAsync("Person", t => t
            .WithGuidAttribute("employeeId")
            .WithStringAttribute("displayName")
            .WithBoolAttribute("isActive"));

        // Get attributes for flow rules
        var hrUserType = _harness.GetObjectType("HR", "User");
        var adUserType = _harness.GetObjectType("AD", "User");

        var hrDisplayName = hrUserType.Attributes.First(a => a.Name == "displayName");
        var hrIsActive = hrUserType.Attributes.First(a => a.Name == "isActive");

        var adCn = adUserType.Attributes.First(a => a.Name == "cn");
        var adDn = adUserType.Attributes.First(a => a.Name == "distinguishedName");
        var adEnabled = adUserType.Attributes.First(a => a.Name == "enabled");

        // Get MV attributes
        var mvDisplayName = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "displayName");
        var mvIsActive = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "isActive");

        // Create import sync rule
        await _harness.CreateSyncRuleAsync(
            "HR Import",
            "HR",
            "User",
            personType,
            SyncRuleDirection.Import,
            r => r
                .WithProjection()
                .WithAttributeFlow(mvDisplayName, hrDisplayName)
                .WithAttributeFlow(mvIsActive, hrIsActive));

        // Create export sync rule
        await _harness.CreateSyncRuleAsync(
            "AD Export",
            "AD",
            "User",
            personType,
            SyncRuleDirection.Export,
            r => r
                .WithProvisioning()
                .WithAttributeFlow(mvDisplayName, adCn)
                .WithAttributeFlow(mvIsActive, adEnabled)
                .WithExpressionFlow("\"CN=\" + mv[\"displayName\"] + \",OU=Users,DC=test,DC=local\"", adDn));
    }

    private async Task SetUpGuidExportScenarioAsync()
    {
        // Create HR (source) system
        await _harness.CreateConnectedSystemAsync("HR");
        await _harness.CreateObjectTypeAsync("HR", "User", t => t
            .WithGuidExternalId("employeeId")
            .WithStringAttribute("displayName")
            .WithGuidAttribute("correlationId"));

        // Create AD (target) system
        await _harness.CreateConnectedSystemAsync("AD");
        await _harness.CreateObjectTypeAsync("AD", "User", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn")
            .WithGuidAttribute("externalCorrelationId"));

        // Create MV type
        var personType = await _harness.CreateMetaverseObjectTypeAsync("Person", t => t
            .WithGuidAttribute("employeeId")
            .WithStringAttribute("displayName")
            .WithGuidAttribute("correlationId"));

        // Get attributes for flow rules
        var hrUserType = _harness.GetObjectType("HR", "User");
        var adUserType = _harness.GetObjectType("AD", "User");

        var hrDisplayName = hrUserType.Attributes.First(a => a.Name == "displayName");
        var hrCorrelationId = hrUserType.Attributes.First(a => a.Name == "correlationId");

        var adCn = adUserType.Attributes.First(a => a.Name == "cn");
        var adDn = adUserType.Attributes.First(a => a.Name == "distinguishedName");
        var adCorrelationId = adUserType.Attributes.First(a => a.Name == "externalCorrelationId");

        // Get MV attributes
        var mvDisplayName = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "displayName");
        var mvCorrelationId = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "correlationId");

        // Create import sync rule
        await _harness.CreateSyncRuleAsync(
            "HR Import",
            "HR",
            "User",
            personType,
            SyncRuleDirection.Import,
            r => r
                .WithProjection()
                .WithAttributeFlow(mvDisplayName, hrDisplayName)
                .WithAttributeFlow(mvCorrelationId, hrCorrelationId));

        // Create export sync rule
        await _harness.CreateSyncRuleAsync(
            "AD Export",
            "AD",
            "User",
            personType,
            SyncRuleDirection.Export,
            r => r
                .WithProvisioning()
                .WithAttributeFlow(mvDisplayName, adCn)
                .WithAttributeFlow(mvCorrelationId, adCorrelationId)
                .WithExpressionFlow("\"CN=\" + mv[\"displayName\"] + \",OU=Users,DC=test,DC=local\"", adDn));
    }

    private async Task SetUpDateTimeExportScenarioAsync()
    {
        // Create HR (source) system
        await _harness.CreateConnectedSystemAsync("HR");
        await _harness.CreateObjectTypeAsync("HR", "User", t => t
            .WithGuidExternalId("employeeId")
            .WithStringAttribute("displayName")
            .WithDateTimeAttribute("startDate"));

        // Create AD (target) system
        await _harness.CreateConnectedSystemAsync("AD");
        await _harness.CreateObjectTypeAsync("AD", "User", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn")
            .WithDateTimeAttribute("hireDate"));

        // Create MV type
        var personType = await _harness.CreateMetaverseObjectTypeAsync("Person", t => t
            .WithGuidAttribute("employeeId")
            .WithStringAttribute("displayName")
            .WithDateTimeAttribute("startDate"));

        // Get attributes for flow rules
        var hrUserType = _harness.GetObjectType("HR", "User");
        var adUserType = _harness.GetObjectType("AD", "User");

        var hrDisplayName = hrUserType.Attributes.First(a => a.Name == "displayName");
        var hrStartDate = hrUserType.Attributes.First(a => a.Name == "startDate");

        var adCn = adUserType.Attributes.First(a => a.Name == "cn");
        var adDn = adUserType.Attributes.First(a => a.Name == "distinguishedName");
        var adHireDate = adUserType.Attributes.First(a => a.Name == "hireDate");

        // Get MV attributes
        var mvDisplayName = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "displayName");
        var mvStartDate = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "startDate");

        // Create import sync rule
        await _harness.CreateSyncRuleAsync(
            "HR Import",
            "HR",
            "User",
            personType,
            SyncRuleDirection.Import,
            r => r
                .WithProjection()
                .WithAttributeFlow(mvDisplayName, hrDisplayName)
                .WithAttributeFlow(mvStartDate, hrStartDate));

        // Create export sync rule
        await _harness.CreateSyncRuleAsync(
            "AD Export",
            "AD",
            "User",
            personType,
            SyncRuleDirection.Export,
            r => r
                .WithProvisioning()
                .WithAttributeFlow(mvDisplayName, adCn)
                .WithAttributeFlow(mvStartDate, adHireDate)
                .WithExpressionFlow("\"CN=\" + mv[\"displayName\"] + \",OU=Users,DC=test,DC=local\"", adDn));
    }

    private async Task SetUpIntExportScenarioAsync()
    {
        // Create HR (source) system
        await _harness.CreateConnectedSystemAsync("HR");
        await _harness.CreateObjectTypeAsync("HR", "User", t => t
            .WithGuidExternalId("employeeId")
            .WithStringAttribute("displayName")
            .WithIntAttribute("badgeNumber"));

        // Create AD (target) system
        await _harness.CreateConnectedSystemAsync("AD");
        await _harness.CreateObjectTypeAsync("AD", "User", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn")
            .WithIntAttribute("employeeNumber"));

        // Create MV type
        var personType = await _harness.CreateMetaverseObjectTypeAsync("Person", t => t
            .WithGuidAttribute("employeeId")
            .WithStringAttribute("displayName")
            .WithIntAttribute("badgeNumber"));

        // Get attributes for flow rules
        var hrUserType = _harness.GetObjectType("HR", "User");
        var adUserType = _harness.GetObjectType("AD", "User");

        var hrDisplayName = hrUserType.Attributes.First(a => a.Name == "displayName");
        var hrBadgeNumber = hrUserType.Attributes.First(a => a.Name == "badgeNumber");

        var adCn = adUserType.Attributes.First(a => a.Name == "cn");
        var adDn = adUserType.Attributes.First(a => a.Name == "distinguishedName");
        var adEmployeeNumber = adUserType.Attributes.First(a => a.Name == "employeeNumber");

        // Get MV attributes
        var mvDisplayName = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "displayName");
        var mvBadgeNumber = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "badgeNumber");

        // Create import sync rule
        await _harness.CreateSyncRuleAsync(
            "HR Import",
            "HR",
            "User",
            personType,
            SyncRuleDirection.Import,
            r => r
                .WithProjection()
                .WithAttributeFlow(mvDisplayName, hrDisplayName)
                .WithAttributeFlow(mvBadgeNumber, hrBadgeNumber));

        // Create export sync rule
        await _harness.CreateSyncRuleAsync(
            "AD Export",
            "AD",
            "User",
            personType,
            SyncRuleDirection.Export,
            r => r
                .WithProvisioning()
                .WithAttributeFlow(mvDisplayName, adCn)
                .WithAttributeFlow(mvBadgeNumber, adEmployeeNumber)
                .WithExpressionFlow("\"CN=\" + mv[\"displayName\"] + \",OU=Users,DC=test,DC=local\"", adDn));
    }

    #endregion

    #region Confirming Import Helpers

    private ConnectedSystemImportObject GenerateConfirmingImportObjectWithBool(PendingExport pe)
    {
        var dn = pe.AttributeValueChanges
            .FirstOrDefault(avc => avc.Attribute?.Name == "distinguishedName")
            ?.StringValue ?? "CN=Unknown,OU=Users,DC=test,DC=local";
        var cn = pe.AttributeValueChanges
            .FirstOrDefault(avc => avc.Attribute?.Name == "cn")
            ?.StringValue ?? "Unknown";
        var enabled = pe.AttributeValueChanges
            .FirstOrDefault(avc => avc.Attribute?.Name == "enabled")
            ?.BoolValue ?? false;

        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = "objectGUID", GuidValues = new List<Guid> { Guid.NewGuid() } },
                new() { Name = "distinguishedName", StringValues = new List<string> { dn } },
                new() { Name = "cn", StringValues = new List<string> { cn } },
                new() { Name = "enabled", BoolValue = enabled }
            }
        };
    }

    private ConnectedSystemImportObject GenerateConfirmingImportObjectWithGuid(PendingExport pe, Guid expectedGuid)
    {
        var dn = pe.AttributeValueChanges
            .FirstOrDefault(avc => avc.Attribute?.Name == "distinguishedName")
            ?.StringValue ?? "CN=Unknown,OU=Users,DC=test,DC=local";
        var cn = pe.AttributeValueChanges
            .FirstOrDefault(avc => avc.Attribute?.Name == "cn")
            ?.StringValue ?? "Unknown";

        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = "objectGUID", GuidValues = new List<Guid> { Guid.NewGuid() } },
                new() { Name = "distinguishedName", StringValues = new List<string> { dn } },
                new() { Name = "cn", StringValues = new List<string> { cn } },
                new() { Name = "externalCorrelationId", GuidValues = new List<Guid> { expectedGuid } }
            }
        };
    }

    private ConnectedSystemImportObject GenerateConfirmingImportObjectWithDateTime(PendingExport pe, DateTime expectedDate)
    {
        var dn = pe.AttributeValueChanges
            .FirstOrDefault(avc => avc.Attribute?.Name == "distinguishedName")
            ?.StringValue ?? "CN=Unknown,OU=Users,DC=test,DC=local";
        var cn = pe.AttributeValueChanges
            .FirstOrDefault(avc => avc.Attribute?.Name == "cn")
            ?.StringValue ?? "Unknown";

        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = "objectGUID", GuidValues = new List<Guid> { Guid.NewGuid() } },
                new() { Name = "distinguishedName", StringValues = new List<string> { dn } },
                new() { Name = "cn", StringValues = new List<string> { cn } },
                new() { Name = "hireDate", DateTimeValue = expectedDate }
            }
        };
    }

    private ConnectedSystemImportObject GenerateConfirmingImportObjectWithInt(PendingExport pe, int expectedNumber)
    {
        var dn = pe.AttributeValueChanges
            .FirstOrDefault(avc => avc.Attribute?.Name == "distinguishedName")
            ?.StringValue ?? "CN=Unknown,OU=Users,DC=test,DC=local";
        var cn = pe.AttributeValueChanges
            .FirstOrDefault(avc => avc.Attribute?.Name == "cn")
            ?.StringValue ?? "Unknown";

        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "User",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = "objectGUID", GuidValues = new List<Guid> { Guid.NewGuid() } },
                new() { Name = "distinguishedName", StringValues = new List<string> { dn } },
                new() { Name = "cn", StringValues = new List<string> { cn } },
                new() { Name = "employeeNumber", IntValues = new List<int> { expectedNumber } }
            }
        };
    }

    #endregion
}
