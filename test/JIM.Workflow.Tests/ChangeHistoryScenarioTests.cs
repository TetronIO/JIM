using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Workflow.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Workflow.Tests;

/// <summary>
/// Workflow tests for generating realistic change history scenarios to test the Change History UI.
/// These tests create enterprise-representative data with extensive CSO and MVO changes across
/// multiple sync operations, covering all attribute types and edge cases.
///
/// Run this test to generate realistic data, then access the UI to view change history:
/// - Alice: /t/people/v/{alice-mvo-id}
/// - Bob: /t/people/v/{bob-mvo-id}
/// - Engineers Group: /t/groups/v/{engineers-mvo-id}
/// - Platform Team Group: /t/groups/v/{platform-mvo-id}
/// </summary>
[TestFixture]
public class ChangeHistoryScenarioTests
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
    /// Creates a comprehensive change history scenario with:
    /// - HR system contributing 2 users with multiple updates across all attribute types
    /// - AD system contributing 2 groups with varying membership changes (small and large-scale)
    /// - Manager reference attribute changes (set and remove)
    /// - Realistic enterprise naming and attributes
    ///
    /// This test generates a rich dataset for UI testing and validation.
    /// </summary>
    [Test]
    public async Task ChangeHistoryScenario_EnterpriseUsers_MultipleUpdatesAndReferences()
    {
        Console.WriteLine("=== PHASE 1: Initial Setup - Create Connected Systems and Types ===");

        // Create HR Connected System (source of truth for users)
        var hrSystem = await _harness.CreateConnectedSystemAsync("HR System", connector =>
        {
            // HR connector will provide user data via mock
        });

        // Create AD Connected System (source of truth for groups)
        var adSystem = await _harness.CreateConnectedSystemAsync("Active Directory", connector =>
        {
            // AD connector will provide group data via mock
        });

        // Create Person metaverse object type with all attribute types
        var personType = await _harness.CreateMetaverseObjectTypeAsync("Person", builder =>
        {
            builder.SetPluralName("People");
            builder.AddAttribute("EmployeeID", AttributeDataType.Text);
            builder.AddAttribute("FirstName", AttributeDataType.Text);
            builder.AddAttribute("LastName", AttributeDataType.Text);
            builder.AddAttribute("Email", AttributeDataType.Text);
            builder.AddAttribute("Department", AttributeDataType.Text);
            builder.AddAttribute("JobTitle", AttributeDataType.Text);
            builder.AddAttribute("EmployeeNumber", AttributeDataType.Number);
            builder.AddAttribute("HireDate", AttributeDataType.DateTime);
            builder.AddAttribute("Salary", AttributeDataType.LongNumber);
            builder.AddAttribute("IsActive", AttributeDataType.Boolean);
            builder.AddReferenceAttribute("Manager", "Person");
        });

        // Create Group metaverse object type
        var groupType = await _harness.CreateMetaverseObjectTypeAsync("Group", builder =>
        {
            builder.SetPluralName("Groups");
            builder.AddAttribute("GroupID", AttributeDataType.Text);
            builder.AddAttribute("Name", AttributeDataType.Text);
            builder.AddAttribute("Description", AttributeDataType.Text);
            builder.AddMultiValuedReferenceAttribute("Members", "Person");
        });

        // Create CSO types for HR
        var hrPersonType = await _harness.CreateObjectTypeAsync("HR System", "Person", builder =>
        {
            builder.SetMetaverseType(personType);
            builder.AddAttribute("EmployeeID", AttributeDataType.Text, isExternalId: true);
            builder.AddAttribute("FirstName", AttributeDataType.Text);
            builder.AddAttribute("LastName", AttributeDataType.Text);
            builder.AddAttribute("Email", AttributeDataType.Text);
            builder.AddAttribute("Department", AttributeDataType.Text);
            builder.AddAttribute("JobTitle", AttributeDataType.Text);
            builder.AddAttribute("EmployeeNumber", AttributeDataType.Number);
            builder.AddAttribute("HireDate", AttributeDataType.DateTime);
            builder.AddAttribute("Salary", AttributeDataType.LongNumber);
            builder.AddAttribute("IsActive", AttributeDataType.Boolean);
            builder.AddAttribute("ManagerEmployeeID", AttributeDataType.Text);
        });

        // Create CSO type for AD groups
        var adGroupType = await _harness.CreateObjectTypeAsync("Active Directory", "Group", builder =>
        {
            builder.SetMetaverseType(groupType);
            builder.AddAttribute("DistinguishedName", AttributeDataType.Text, isExternalId: true);
            builder.AddAttribute("Name", AttributeDataType.Text);
            builder.AddAttribute("Description", AttributeDataType.Text);
            builder.AddMultiValuedReferenceAttribute("Members", hrPersonType);
        });

        // Create sync rule: HR Person -> MV Person
        var hrSyncRule = await _harness.CreateSyncRuleAsync(
            "HR to Metaverse - Person",
            "HR System",
            "Person",
            personType,
            builder =>
            {
                builder.SetDirection(SyncRuleDirection.Bidirectional);
                builder.SetPrecedence(100);
                builder.AddObjectMatchingRule("EmployeeID", "EmployeeID");

                // Direct attribute flows
                builder.AddAttributeFlow("EmployeeID", "EmployeeID", AttributeFlowDirection.Import);
                builder.AddAttributeFlow("FirstName", "FirstName", AttributeFlowDirection.Import);
                builder.AddAttributeFlow("LastName", "LastName", AttributeFlowDirection.Import);
                builder.AddAttributeFlow("Email", "Email", AttributeFlowDirection.Import);
                builder.AddAttributeFlow("Department", "Department", AttributeFlowDirection.Import);
                builder.AddAttributeFlow("JobTitle", "JobTitle", AttributeFlowDirection.Import);
                builder.AddAttributeFlow("EmployeeNumber", "EmployeeNumber", AttributeFlowDirection.Import);
                builder.AddAttributeFlow("HireDate", "HireDate", AttributeFlowDirection.Import);
                builder.AddAttributeFlow("Salary", "Salary", AttributeFlowDirection.Import);
                builder.AddAttributeFlow("IsActive", "IsActive", AttributeFlowDirection.Import);

                // Reference attribute flow for Manager
                builder.AddReferenceAttributeFlow("ManagerEmployeeID", "Manager", AttributeFlowDirection.Import, personType, "EmployeeID");
            });

        // Create sync rule: AD Group -> MV Group
        var adSyncRule = await _harness.CreateSyncRuleAsync(
            "AD to Metaverse - Group",
            "Active Directory",
            "Group",
            groupType,
            builder =>
            {
                builder.SetDirection(SyncRuleDirection.Bidirectional);
                builder.SetPrecedence(100);
                builder.AddObjectMatchingRule("DistinguishedName", "GroupID");

                builder.AddAttributeFlow("DistinguishedName", "GroupID", AttributeFlowDirection.Import);
                builder.AddAttributeFlow("Name", "Name", AttributeFlowDirection.Import);
                builder.AddAttributeFlow("Description", "Description", AttributeFlowDirection.Import);
                builder.AddAttributeFlow("Members", "Members", AttributeFlowDirection.Import);
            });

        Console.WriteLine("✓ Setup complete - HR and AD systems configured with sync rules");

        // ============================================================
        // PHASE 2: Initial Import - Two Users (Alice and Bob)
        // ============================================================

        Console.WriteLine("\n=== PHASE 2: Initial User Import ===");

        var hrConnector = _harness.GetConnector("HR System");
        hrConnector.QueueImportObjects(new[]
        {
            CreateMockUser("EMP001", "Alice", "Anderson", "alice.anderson@contoso.com",
                "Engineering", "Senior Software Engineer", 12345, new DateTime(2018, 3, 15), 125000L, true),
            CreateMockUser("EMP002", "Bob", "Brown", "bob.brown@contoso.com",
                "Engineering", "Software Engineer", 12346, new DateTime(2020, 7, 1), 95000L, true)
        });

        await _harness.ExecuteFullImportAsync("HR System");
        await _harness.ExecuteFullSyncAsync("HR System");
        Console.WriteLine("✓ Alice and Bob imported and synced to metaverse");

        // Get the CSOs and MVOs for reference
        var alice = await _harness.DbContext.ConnectedSystemObjects
            .Include(cso => cso.MetaverseObject)
            .FirstAsync(cso => cso.ExternalIdAttributeValue!.StringValue == "EMP001");
        var bob = await _harness.DbContext.ConnectedSystemObjects
            .Include(cso => cso.MetaverseObject)
            .FirstAsync(cso => cso.ExternalIdAttributeValue!.StringValue == "EMP002");

        Console.WriteLine($"  Alice MVO ID: {alice.MetaverseObject!.Id}");
        Console.WriteLine($"  Bob MVO ID: {bob.MetaverseObject!.Id}");

        // ============================================================
        // PHASE 3: Multiple Updates - Alice and Manager Relationships
        // ============================================================

        Console.WriteLine("\n=== PHASE 3: Alice Updates (7 iterations) ===");

        // Update 1: Promotion
        hrConnector.QueueImportObjects(new[]
        {
            CreateMockUser("EMP001", "Alice", "Anderson", "alice.anderson@contoso.com",
                "Engineering", "Lead Software Engineer", 12345, new DateTime(2018, 3, 15), 135000L, true)
        });
        await _harness.ExecuteFullImportAsync("HR System");
        await _harness.ExecuteFullSyncAsync("HR System");
        Console.WriteLine("✓ Update 1: Alice promoted to Lead Engineer");

        // Update 2: Manager assignment (Bob reports to Alice)
        hrConnector.QueueImportObjects(new[]
        {
            CreateMockUser("EMP002", "Bob", "Brown", "bob.brown@contoso.com",
                "Engineering", "Software Engineer", 12346, new DateTime(2020, 7, 1), 95000L, true, managerEmployeeId: "EMP001")
        });
        await _harness.ExecuteFullImportAsync("HR System");
        await _harness.ExecuteFullSyncAsync("HR System");
        Console.WriteLine("✓ Update 2: Bob now reports to Alice");

        // Update 3-7: Continue with more updates
        hrConnector.QueueImportObjects(new[]
        {
            CreateMockUser("EMP001", "Alice", "Anderson", "alice.anderson@contoso.com",
                "Engineering - Platform Team", "Lead Software Engineer", 12345, new DateTime(2018, 3, 15), 135000L, true)
        });
        await _harness.ExecuteFullImportAsync("HR System");
        await _harness.ExecuteFullSyncAsync("HR System");
        Console.WriteLine("✓ Update 3: Alice moved to Platform Team");

        hrConnector.QueueImportObjects(new[]
        {
            CreateMockUser("EMP001", "Alice", "Anderson", "alice.anderson@contoso.enterprise.com",
                "Engineering - Platform Team", "Lead Software Engineer", 12345, new DateTime(2018, 3, 15), 135000L, true)
        });
        await _harness.ExecuteFullImportAsync("HR System");
        await _harness.ExecuteFullSyncAsync("HR System");
        Console.WriteLine("✓ Update 4: Alice email updated");

        hrConnector.QueueImportObjects(new[]
        {
            CreateMockUser("EMP001", "Alice", "Anderson", "alice.anderson@contoso.enterprise.com",
                "Engineering - Platform Team", "Engineering Manager", 12345, new DateTime(2018, 3, 15), 155000L, true)
        });
        await _harness.ExecuteFullImportAsync("HR System");
        await _harness.ExecuteFullSyncAsync("HR System");
        Console.WriteLine("✓ Update 5: Alice promoted to Engineering Manager");

        // Update 6: Remove manager relationship
        hrConnector.QueueImportObjects(new[]
        {
            CreateMockUser("EMP002", "Bob", "Brown", "bob.brown@contoso.com",
                "Engineering", "Software Engineer", 12346, new DateTime(2020, 7, 1), 95000L, true, managerEmployeeId: null)
        });
        await _harness.ExecuteFullImportAsync("HR System");
        await _harness.ExecuteFullSyncAsync("HR System");
        Console.WriteLine("✓ Update 6: Bob's manager reference removed");

        // Update 7: Reassign manager
        hrConnector.QueueImportObjects(new[]
        {
            CreateMockUser("EMP002", "Bob", "Brown", "bob.brown@contoso.com",
                "Engineering", "Software Engineer", 12346, new DateTime(2020, 7, 1), 95000L, true, managerEmployeeId: "EMP001")
        });
        await _harness.ExecuteFullImportAsync("HR System");
        await _harness.ExecuteFullSyncAsync("HR System");
        Console.WriteLine("✓ Update 7: Bob's manager reassigned to Alice");

        // ============================================================
        // PHASE 4: Multiple Updates - Bob
        // ============================================================

        Console.WriteLine("\n=== PHASE 4: Bob Updates (6 iterations) ===");

        var bobUpdates = new[]
        {
            ("Engineering - Backend Services", "Software Engineer", 95000L, "bob.brown@contoso.com", "Update 1: Bob moved to Backend Services"),
            ("Engineering - Backend Services", "Senior Software Engineer", 95000L, "bob.brown@contoso.com", "Update 2: Bob promoted"),
            ("Engineering - Backend Services", "Senior Software Engineer", 110000L, "bob.brown@contoso.com", "Update 3: Salary increased"),
            ("Engineering - Backend Services", "Senior Software Engineer", 110000L, "bob.brown@contoso.enterprise.com", "Update 4: Email updated"),
            ("Engineering - Platform Team", "Senior Software Engineer", 110000L, "bob.brown@contoso.enterprise.com", "Update 5: Moved to Platform Team"),
            ("Engineering - Platform Team", "Senior Software Engineer", 120000L, "bob.brown@contoso.enterprise.com", "Update 6: Salary increased again")
        };

        foreach (var (dept, title, salary, email, message) in bobUpdates)
        {
            hrConnector.QueueImportObjects(new[]
            {
                CreateMockUser("EMP002", "Bob", "Brown", email, dept, title, 12346, new DateTime(2020, 7, 1), salary, true, managerEmployeeId: "EMP001")
            });
            await _harness.ExecuteFullImportAsync("HR System");
            await _harness.ExecuteFullSyncAsync("HR System");
            Console.WriteLine($"✓ {message}");
        }

        // ============================================================
        // PHASE 5: Groups (simpler for now - can expand later)
        // ============================================================

        Console.WriteLine("\n=== PHASE 5: Group Import and Updates ===");

        var adConnector = _harness.GetConnector("Active Directory");

        // Initial import of two groups
        adConnector.QueueImportObjects(new[]
        {
            CreateMockGroup("CN=Engineers,OU=Groups,DC=contoso,DC=com", "Engineers", "All engineering staff", Array.Empty<string>()),
            CreateMockGroup("CN=Platform-Team,OU=Groups,DC=contoso,DC=com", "Platform Team", "Platform engineering team members", Array.Empty<string>())
        });

        await _harness.ExecuteFullImportAsync("Active Directory");
        await _harness.ExecuteFullSyncAsync("Active Directory");
        Console.WriteLine("✓ Engineers and Platform Team groups imported");

        // Get group MVOs
        var engineersGroup = await _harness.DbContext.ConnectedSystemObjects
            .Include(cso => cso.MetaverseObject)
            .FirstAsync(cso => cso.ExternalIdAttributeValue!.StringValue == "CN=Engineers,OU=Groups,DC=contoso,DC=com");
        var platformGroup = await _harness.DbContext.ConnectedSystemObjects
            .Include(cso => cso.MetaverseObject)
            .FirstAsync(cso => cso.ExternalIdAttributeValue!.StringValue == "CN=Platform-Team,OU=Groups,DC=contoso,DC=com");

        Console.WriteLine($"  Engineers Group MVO ID: {engineersGroup.MetaverseObject!.Id}");
        Console.WriteLine($"  Platform Team MVO ID: {platformGroup.MetaverseObject!.Id}");

        // Small-scale membership changes for Engineers group
        Console.WriteLine("\n=== Engineers Group Updates (5 iterations) ===");

        adConnector.QueueImportObjects(new[]
        {
            CreateMockGroup("CN=Engineers,OU=Groups,DC=contoso,DC=com", "Software Engineers", "All engineering staff", Array.Empty<string>())
        });
        await _harness.ExecuteFullImportAsync("Active Directory");
        await _harness.ExecuteFullSyncAsync("Active Directory");
        Console.WriteLine("✓ Update 1: Engineers renamed");

        adConnector.QueueImportObjects(new[]
        {
            CreateMockGroup("CN=Engineers,OU=Groups,DC=contoso,DC=com", "Software Engineers", "Software engineering department", new[] { "EMP001" })
        });
        await _harness.ExecuteFullImportAsync("Active Directory");
        await _harness.ExecuteFullSyncAsync("Active Directory");
        Console.WriteLine("✓ Update 2: Alice added to Engineers");

        adConnector.QueueImportObjects(new[]
        {
            CreateMockGroup("CN=Engineers,OU=Groups,DC=contoso,DC=com", "Software Engineers", "Software engineering department", new[] { "EMP001", "EMP002" })
        });
        await _harness.ExecuteFullImportAsync("Active Directory");
        await _harness.ExecuteFullSyncAsync("Active Directory");
        Console.WriteLine("✓ Update 3: Bob added to Engineers");

        adConnector.QueueImportObjects(new[]
        {
            CreateMockGroup("CN=Engineers,OU=Groups,DC=contoso,DC=com", "Software Engineers", "Software engineering department", new[] { "EMP002" })
        });
        await _harness.ExecuteFullImportAsync("Active Directory");
        await _harness.ExecuteFullSyncAsync("Active Directory");
        Console.WriteLine("✓ Update 4: Alice removed from Engineers");

        // ============================================================
        // VERIFICATION: Validate Change History Created
        // ============================================================

        Console.WriteLine("\n=== VERIFICATION: Change History Summary ===");

        // Reload MVOs with change history
        var aliceMvo = await _harness.Jim.Metaverse.GetMetaverseObjectWithChangeHistoryAsync(alice.MetaverseObject!.Id);
        var bobMvo = await _harness.Jim.Metaverse.GetMetaverseObjectWithChangeHistoryAsync(bob.MetaverseObject!.Id);
        var engineersMvo = await _harness.Jim.Metaverse.GetMetaverseObjectWithChangeHistoryAsync(engineersGroup.MetaverseObject!.Id);
        var platformMvo = await _harness.Jim.Metaverse.GetMetaverseObjectWithChangeHistoryAsync(platformGroup.MetaverseObject!.Id);

        Console.WriteLine($"\nAlice (Person):");
        Console.WriteLine($"  - Total Changes: {aliceMvo!.Changes.Count}");
        Console.WriteLine($"  - Total Attribute Changes: {aliceMvo.Changes.Sum(c => c.AttributeChanges.Count)}");
        Console.WriteLine($"  - Total Value Changes: {aliceMvo.Changes.Sum(c => c.AttributeChanges.Sum(ac => ac.ValueChanges.Count))}");

        Console.WriteLine($"\nBob (Person):");
        Console.WriteLine($"  - Total Changes: {bobMvo!.Changes.Count}");
        Console.WriteLine($"  - Total Attribute Changes: {bobMvo.Changes.Sum(c => c.AttributeChanges.Count)}");
        Console.WriteLine($"  - Total Value Changes: {bobMvo.Changes.Sum(c => c.AttributeChanges.Sum(ac => ac.ValueChanges.Count))}");

        Console.WriteLine($"\nEngineers Group:");
        Console.WriteLine($"  - Total Changes: {engineersMvo!.Changes.Count}");
        Console.WriteLine($"  - Total Attribute Changes: {engineersMvo.Changes.Sum(c => c.AttributeChanges.Count)}");

        Console.WriteLine($"\nPlatform Team Group:");
        Console.WriteLine($"  - Total Changes: {platformMvo!.Changes.Count}");
        Console.WriteLine($"  - Total Attribute Changes: {platformMvo.Changes.Sum(c => c.AttributeChanges.Count)}");

        // Assertions
        Assert.That(aliceMvo.Changes.Count, Is.GreaterThan(0), "Alice should have change history");
        Assert.That(bobMvo.Changes.Count, Is.GreaterThan(0), "Bob should have change history");

        Console.WriteLine($"\n✓ All change history validations passed!");
        Console.WriteLine($"\nUI Testing URLs (copy MVIDs from above):");
        Console.WriteLine($"  Alice: /t/people/v/{aliceMvo.Id}");
        Console.WriteLine($"  Bob: /t/people/v/{bobMvo.Id}");
        Console.WriteLine($"  Engineers: /t/groups/v/{engineersMvo.Id}");
        Console.WriteLine($"  Platform Team: /t/groups/v/{platformMvo.Id}");
    }

    #region Helper Methods

    private Dictionary<string, object?> CreateMockUser(
        string employeeId,
        string firstName,
        string lastName,
        string email,
        string department,
        string jobTitle,
        int employeeNumber,
        DateTime hireDate,
        long salary,
        bool isActive,
        string? managerEmployeeId = null)
    {
        var user = new Dictionary<string, object?>
        {
            ["EmployeeID"] = employeeId,
            ["FirstName"] = firstName,
            ["LastName"] = lastName,
            ["Email"] = email,
            ["Department"] = department,
            ["JobTitle"] = jobTitle,
            ["EmployeeNumber"] = employeeNumber,
            ["HireDate"] = hireDate,
            ["Salary"] = salary,
            ["IsActive"] = isActive
        };

        if (managerEmployeeId != null)
        {
            user["ManagerEmployeeID"] = managerEmployeeId;
        }

        return user;
    }

    private Dictionary<string, object?> CreateMockGroup(
        string dn,
        string name,
        string description,
        string[] memberEmployeeIds)
    {
        return new Dictionary<string, object?>
        {
            ["DistinguishedName"] = dn,
            ["Name"] = name,
            ["Description"] = description,
            ["Members"] = memberEmployeeIds
        };
    }

    #endregion
}
