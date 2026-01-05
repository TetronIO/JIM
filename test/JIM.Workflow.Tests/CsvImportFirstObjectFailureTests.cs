using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Workflow.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Workflow.Tests;

/// <summary>
/// Workflow tests for reproducing and debugging the first imported object CSO creation failure.
///
/// Issue: When importing from CSV with 100 objects (Small template), the first Full Import creates:
/// - 100 total RunProfileExecutionItems (RPEIs)
/// - 99 with valid ConnectedSystemObjects (CSOs)
/// - 1 with NULL ConnectedSystemObjectId (ObjectChangeType=1, ErrorType=0)
///
/// The NULL CSO RPEI shows all expected attributes in detail view but no CSO link in list view.
/// This is a SILENT FAILURE - the error is not recorded (ErrorType=0=NotSet).
///
/// Verified in integration test: First Full Import consistently has 99/100 success rate.
/// </summary>
[TestFixture]
public class CsvImportFirstObjectFailureTests
{
    private WorkflowTestHarness _harness = null!;

    [SetUp]
    public async Task SetupAsync()
    {
        _harness = new WorkflowTestHarness();

        // Create connected system (mock CSV connector by default)
        await _harness.CreateConnectedSystemAsync("HR CSV");

        // Create metaverse type
        var personType = await _harness.CreateMetaverseObjectTypeAsync("person", mv =>
        {
            mv.WithStringAttribute("employeeId");
            mv.WithStringAttribute("displayName");
            mv.WithStringAttribute("email");
            mv.WithStringAttribute("department");
            mv.WithStringAttribute("title");
            mv.WithStringAttribute("samAccountName");
            mv.WithStringAttribute("firstName");
            mv.WithStringAttribute("lastName");
            mv.WithStringAttribute("status");
            mv.WithStringAttribute("userPrincipalName");
        });

        // Create connected system object type with same attributes
        await _harness.CreateObjectTypeAsync("HR CSV", "person", cs =>
        {
            cs.WithStringExternalId("employeeId");
            cs.WithStringAttribute("displayName");
            cs.WithStringAttribute("email");
            cs.WithStringAttribute("department");
            cs.WithStringAttribute("title");
            cs.WithStringAttribute("samAccountName");
            cs.WithStringAttribute("firstName");
            cs.WithStringAttribute("lastName");
            cs.WithStringAttribute("status");
            cs.WithStringAttribute("userPrincipalName");
        });

        // Create import sync rule (HR CSV → MV)
        var csvObjectType = _harness.GetObjectType("HR CSV", "person");
        var csvEmployeeId = csvObjectType.Attributes.First(a => a.IsExternalId);
        var csvDisplayName = csvObjectType.Attributes.First(a => a.Name == "displayName");
        var csvEmail = csvObjectType.Attributes.First(a => a.Name == "email");
        var csvDepartment = csvObjectType.Attributes.First(a => a.Name == "department");

        var mvEmployeeId = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "employeeId");
        var mvDisplayName = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "displayName");
        var mvEmail = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "email");
        var mvDepartment = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "department");

        await _harness.CreateSyncRuleAsync(
            "HR CSV Import",
            "HR CSV",
            "person",
            personType,
            SyncRuleDirection.Import,
            r => r
                .WithProjection()
                .WithAttributeFlow(mvEmployeeId, csvEmployeeId)
                .WithAttributeFlow(mvDisplayName, csvDisplayName)
                .WithAttributeFlow(mvEmail, csvEmail)
                .WithAttributeFlow(mvDepartment, csvDepartment));
    }

    [TearDown]
    public void Teardown()
    {
        _harness?.Dispose();
    }

    /// <summary>
    /// Reproduces the issue: first imported object should have a CSO but doesn't.
    /// This test will initially FAIL, demonstrating the bug.
    /// </summary>
    [Test]
    public async Task FullImport_WithTenObjects_AllObjectsCreateCsosAsync()
    {
        // Arrange: Configure the mock connector to return 10 objects (EMP000001 through EMP000010)
        var connector = _harness.GetConnector("HR CSV");
        var objects = GenerateTestImportObjects(count: 10);
        Console.WriteLine($"Queuing {objects.Count} objects");
        connector.QueueImportObjects(objects);
        Console.WriteLine($"Queue now contains: {connector.QueuedImportResultCount}");

        // Act: Execute full import
        var importActivity = await _harness.ExecuteFullImportAsync("HR CSV");

        // Debug info
        Console.WriteLine($"Activity ID: {importActivity.Id}");
        Console.WriteLine($"Activity Status: {importActivity.Status}");
        Console.WriteLine($"RPEI count from activity: {importActivity.RunProfileExecutionItems.Count}");

        // Also check directly from DB
        var dbActivity = await _harness.DbContext.Activities
            .Include(a => a.RunProfileExecutionItems)
            .FirstAsync(a => a.Id == importActivity.Id);
        Console.WriteLine($"RPEI count from DB: {dbActivity.RunProfileExecutionItems.Count}");

        // Assert: All imported objects should have CSOs
        Assert.That(importActivity.RunProfileExecutionItems, Has.Count.EqualTo(10),
            "Should have 10 RPEIs for 10 imported objects");

        var rpeiWithoutCso = importActivity.RunProfileExecutionItems
            .Where(r => r.ConnectedSystemObjectId == null)
            .ToList();

        Assert.That(rpeiWithoutCso, Is.Empty,
            $"No RPEI should have NULL CSO. Found {rpeiWithoutCso.Count} with NULL CSO");

        // Verify first object specifically
        var firstRpei = importActivity.RunProfileExecutionItems
            .OrderBy(r => r.Id)
            .First();

        Assert.That(firstRpei.ConnectedSystemObjectId, Is.Not.Null,
            "First RPEI (EMP000001) should have a CSO");

        Assert.That(firstRpei.ObjectChangeType, Is.EqualTo(ObjectChangeType.Create),
            "First object should be a Create operation");

        // Verify no silent errors
        var rpeiWithErrors = importActivity.RunProfileExecutionItems
            .Where(r => r.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
            .ToList();

        Assert.That(rpeiWithErrors, Is.Empty,
            $"No RPEI should have errors if all data is valid. Found {rpeiWithErrors.Count} with errors");
    }


    /// <summary>
    /// Compares first object processing with subsequent objects to identify differences.
    /// </summary>
    [Test]
    public async Task FullImport_FirstAndSecondObjectsProcessedIdentically_Async()
    {
        // Arrange
        var connector = _harness.GetConnector("HR CSV");
        connector.QueueImportObjects(GenerateTestImportObjects(count: 5));

        // Act
        var importActivity = await _harness.ExecuteFullImportAsync("HR CSV");

        // Assert: All RPEIs should have CSOs
        var rpeisByOrder = importActivity.RunProfileExecutionItems
            .OrderBy(r => r.Id)
            .ToList();

        for (int i = 0; i < rpeisByOrder.Count; i++)
        {
            var rpei = rpeisByOrder[i];
            var employeeId = $"EMP{i + 1:D6}";

            Assert.That(rpei.ConnectedSystemObjectId, Is.Not.Null,
                $"RPEI {i} ({employeeId}) should have a CSO");

            Assert.That(rpei.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet),
                $"RPEI {i} ({employeeId}) should have no errors");

            if (rpei.ConnectedSystemObject != null)
            {
                var externalIdValue = rpei.ConnectedSystemObject.AttributeValues
                    .FirstOrDefault(av => av.Attribute?.IsExternalId == true);

                Assert.That(externalIdValue?.StringValue, Is.EqualTo(employeeId),
                    $"RPEI {i} should have external ID {employeeId}");
            }
        }
    }


    /// <summary>
    /// Generates test import objects matching the CSV data format.
    /// </summary>
    private List<ConnectedSystemImportObject> GenerateTestImportObjects(int count)
    {
        var objects = new List<ConnectedSystemImportObject>();

        for (int i = 1; i <= count; i++)
        {
            var obj = new ConnectedSystemImportObject
            {
                ObjectType = "person",
                ChangeType = ObjectChangeType.NotSet,
                Attributes = new List<ConnectedSystemImportObjectAttribute>
                {
                    CreateStringAttribute("employeeId", $"EMP{i:D6}"),
                    CreateStringAttribute("displayName", $"Test User {i}"),
                    CreateStringAttribute("email", $"user{i}@example.com"),
                    CreateStringAttribute("department", "Operations"),
                    CreateStringAttribute("title", "Director"),
                    CreateStringAttribute("samAccountName", $"user{i}"),
                    CreateStringAttribute("firstName", $"Test"),
                    CreateStringAttribute("lastName", $"User{i}"),
                    CreateStringAttribute("status", "Active"),
                    CreateStringAttribute("userPrincipalName", $"user{i}@example.com")
                }
            };

            objects.Add(obj);
        }

        return objects;
    }

    private ConnectedSystemImportObjectAttribute CreateStringAttribute(string name, string value)
    {
        return new ConnectedSystemImportObjectAttribute
        {
            Name = name,
            StringValues = new List<string> { value }
        };
    }
}
