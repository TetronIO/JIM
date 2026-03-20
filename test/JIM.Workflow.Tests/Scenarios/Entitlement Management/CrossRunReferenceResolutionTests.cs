using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Workflow.Tests.Harness;
using NUnit.Framework;

namespace JIM.Workflow.Tests.Scenarios.EntitlementManagement;

/// <summary>
/// Workflow tests for cross-run reference resolution.
///
/// These tests verify that group member references are correctly resolved (FK set) when
/// the referenced user objects were imported in a PRIOR import run — not the same batch.
///
/// This exercises the Phase 2 DB fallback path in ResolveReferencesAsync, where the
/// referenced CSOs are not in the in-memory lookup dictionaries and must be found via
/// a batch database query. The FK (ReferenceValueId) must be set on the attribute value
/// after resolution so that it is persisted correctly.
///
/// Regression test for: Phase 2 DB fallback setting navigation property only (ReferenceValue)
/// without setting the foreign key (ReferenceValueId), causing references to remain
/// unresolved in the database despite being matched in memory.
/// </summary>
[TestFixture]
public class CrossRunReferenceResolutionTests
{
    private WorkflowTestHarness _harness = null!;

    private static readonly List<(string SamAccountName, string DisplayName, Guid ObjectGuid)> TestUsers =
    [
        ("alice.johnson", "Alice Johnson", Guid.Parse("11111111-1111-1111-1111-111111111111")),
        ("bob.smith", "Bob Smith", Guid.Parse("22222222-2222-2222-2222-222222222222")),
        ("charlie.brown", "Charlie Brown", Guid.Parse("33333333-3333-3333-3333-333333333333")),
    ];

    private static readonly (string SamAccountName, string Name, Guid ObjectGuid) TestGroup =
        ("project-alpha", "Project-Alpha", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

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
    /// Verifies that group member references are fully resolved (ReferenceValueId persisted)
    /// when users are imported in Run 1 and groups are imported in a separate Run 2.
    ///
    /// This is the structural regression test for the Phase 2 DB fallback FK persistence bug.
    /// In the buggy version: ReferenceValue (navigation) was set in memory but ReferenceValueId
    /// (FK) was not set, so the resolved reference was never written to the database.
    /// After the fix: ReferenceValueId is explicitly set alongside ReferenceValue.
    ///
    /// NOTE: The EF Core in-memory database auto-resolves FKs from navigation properties, so
    /// this test CANNOT catch the specific FK persistence bug itself — the in-memory DB will
    /// always populate ReferenceValueId automatically when ReferenceValue is set. The integration
    /// test (Scenario 8 against real PostgreSQL) is the authoritative regression test for that.
    /// This test verifies the two-run import flow works end-to-end and the assertions are correct.
    /// See test/CLAUDE.md "EF Core In-Memory Database Limitation" for full details.
    /// </summary>
    [Test]
    public async Task ImportGroups_WithMembersImportedInPriorRun_AllReferencesAreResolvedAsync()
    {
        // Arrange: set up source system with user and group object types
        await SetUpSourceSystemAsync();

        // Run 1: Import users only (no groups)
        var sourceConnector = _harness.GetConnector("Source");
        sourceConnector.QueueImportObjects(GenerateUserImportObjects());
        await _harness.ExecuteFullImportAsync("Source");

        await _harness.TakeSnapshotAsync("After User Import (Run 1)");

        // Verify users were imported as CSOs
        var userCsos = _harness.SyncRepo.ConnectedSystemObjects.Values
            .Where(c => c.Type != null && c.Type.Name == "User")
            .ToList();
        Assert.That(userCsos.Count, Is.EqualTo(TestUsers.Count),
            "All users should be imported as CSOs after Run 1");

        // Run 2: Import groups only (users are NOT in this import batch)
        // The group references users by DN. These DNs should be resolved via DB fallback.
        var sourceConnector2 = _harness.GetConnector("Source");
        sourceConnector2.QueueImportObjects(GenerateGroupImportObjects());
        await _harness.ExecuteFullImportAsync("Source");

        await _harness.TakeSnapshotAsync("After Group Import (Run 2)");

        // Assert: The group CSO should have all member references resolved with ReferenceValueId set
        var groupCso = _harness.SyncRepo.ConnectedSystemObjects.Values
            .FirstOrDefault(c => c.Type != null && c.Type.Name == "Group");

        Assert.That(groupCso, Is.Not.Null, "Group CSO should exist after Run 2");

        var memberValues = groupCso!.AttributeValues
            .Where(av => av.Attribute?.Name == "member")
            .ToList();

        Assert.That(memberValues.Count, Is.EqualTo(TestUsers.Count),
            $"Group should have {TestUsers.Count} member attribute values");

        // The key assertion: every member reference must have ReferenceValueId set (not null)
        // This is the FK that is persisted to the database.
        // In the buggy version, all ReferenceValueId values were null despite the in-memory
        // ReferenceValue navigation property being set.
        var unresolvedMembers = memberValues.Where(av => av.ReferenceValueId == null).ToList();

        if (unresolvedMembers.Count > 0)
        {
            Console.WriteLine($"Unresolved member references ({unresolvedMembers.Count}):");
            foreach (var uv in unresolvedMembers)
            {
                Console.WriteLine($"  UnresolvedReferenceValue={uv.UnresolvedReferenceValue}, ReferenceValueId={uv.ReferenceValueId}");
            }
        }

        Assert.That(unresolvedMembers.Count, Is.EqualTo(0),
            "All group member references should have ReferenceValueId set after cross-run DB fallback resolution. " +
            "If this fails, the Phase 2 DB fallback is not persisting the FK (ReferenceValueId) alongside the navigation property (ReferenceValue).");

        // Also verify the UnresolvedReferenceValue field is cleared (or that ReferenceValueId is set — both indicate resolution)
        var resolvedMembers = memberValues.Where(av => av.ReferenceValueId != null).ToList();
        Assert.That(resolvedMembers.Count, Is.EqualTo(TestUsers.Count),
            "All members should be resolved");

        Console.WriteLine($"✓ All {resolvedMembers.Count} group member references were resolved via DB fallback (cross-run resolution).");
    }

    /// <summary>
    /// Verifies that references remain unresolved (with UnresolvedReferenceValue set) when
    /// the referenced objects do not exist at all — i.e., the reference truly cannot be resolved.
    /// This ensures we distinguish "resolved via DB fallback" from "genuinely unresolvable".
    /// </summary>
    [Test]
    public async Task ImportGroups_WithNonExistentMembers_ReferencesRemainUnresolvedAsync()
    {
        // Arrange: set up source system but do NOT import any users
        await SetUpSourceSystemAsync();

        // Import group only — members reference users that don't exist in JIM at all
        var sourceConnector = _harness.GetConnector("Source");
        sourceConnector.QueueImportObjects(GenerateGroupImportObjects());
        await _harness.ExecuteFullImportAsync("Source");

        // Assert: Group exists but member references are unresolved
        var groupCso = _harness.SyncRepo.ConnectedSystemObjects.Values
            .FirstOrDefault(c => c.Type != null && c.Type.Name == "Group");

        Assert.That(groupCso, Is.Not.Null, "Group CSO should exist");

        var memberValues = groupCso!.AttributeValues
            .Where(av => av.Attribute?.Name == "member")
            .ToList();

        Assert.That(memberValues.Count, Is.EqualTo(TestUsers.Count),
            "All member attribute values should be present");

        var unresolvedCount = memberValues.Count(av => av.ReferenceValueId == null && av.UnresolvedReferenceValue != null);
        Assert.That(unresolvedCount, Is.EqualTo(TestUsers.Count),
            "All members should remain unresolved when the referenced users do not exist");

        Console.WriteLine($"✓ All {unresolvedCount} group member references correctly remain unresolved when users do not exist.");
    }

    #region Setup Helpers

    private async Task SetUpSourceSystemAsync()
    {
        await _harness.CreateConnectedSystemAsync("Source");
        await _harness.CreateObjectTypeAsync("Source", "User", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn")
            .WithStringAttribute("sAMAccountName"));

        await _harness.CreateObjectTypeAsync("Source", "Group", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn")
            .WithReferenceAttribute("member", isMultiValued: true));

        // Create minimal metaverse types (needed for sync processor infrastructure)
        await _harness.CreateMetaverseObjectTypeAsync("Person", t => t
            .WithStringAttribute("cn"));

        await _harness.CreateMetaverseObjectTypeAsync("Group", t => t
            .WithStringAttribute("cn"));

        // Sync rules are not needed to test import-only reference resolution
    }

    private List<ConnectedSystemImportObject> GenerateUserImportObjects()
    {
        return TestUsers.Select(u => new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "User",
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute
                {
                    Name = "objectGUID",
                    GuidValues = [u.ObjectGuid]
                },
                new ConnectedSystemImportObjectAttribute
                {
                    Name = "distinguishedName",
                    StringValues = [$"CN={u.DisplayName},OU=Users,DC=test,DC=local"]
                },
                new ConnectedSystemImportObjectAttribute
                {
                    Name = "cn",
                    StringValues = [u.DisplayName]
                },
                new ConnectedSystemImportObjectAttribute
                {
                    Name = "sAMAccountName",
                    StringValues = [u.SamAccountName]
                }
            ]
        }).ToList();
    }

    private List<ConnectedSystemImportObject> GenerateGroupImportObjects()
    {
        // Generate member DNs matching the user DNs from GenerateUserImportObjects
        var memberDns = TestUsers
            .Select(u => $"CN={u.DisplayName},OU=Users,DC=test,DC=local")
            .ToList();

        return
        [
            new ConnectedSystemImportObject
            {
                ChangeType = ObjectChangeType.NotSet,
                ObjectType = "Group",
                Attributes =
                [
                    new ConnectedSystemImportObjectAttribute
                    {
                        Name = "objectGUID",
                        GuidValues = [TestGroup.ObjectGuid]
                    },
                    new ConnectedSystemImportObjectAttribute
                    {
                        Name = "distinguishedName",
                        StringValues = [$"CN={TestGroup.Name},OU=Groups,DC=test,DC=local"]
                    },
                    new ConnectedSystemImportObjectAttribute
                    {
                        Name = "cn",
                        StringValues = [TestGroup.Name]
                    },
                    new ConnectedSystemImportObjectAttribute
                    {
                        Name = "member",
                        ReferenceValues = memberDns
                    }
                ]
            }
        ];
    }

    #endregion
}
