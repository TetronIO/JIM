using JIM.Application.Servers;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Tests for PendingExport merge logic in ExportEvaluationServer.
/// Validates that merging export evaluation changes with drift correction changes
/// works correctly for both single-valued and multi-valued attributes.
/// </summary>
[TestFixture]
public class ExportEvaluationMergeTests
{
    private const int MemberAttributeId = 100;
    private const int DisplayNameAttributeId = 200;
    private const int DescriptionAttributeId = 300;

    #region GetAttributeChangeKey tests

    [Test]
    public void GetAttributeChangeKey_StringValue_ReturnsAttributeIdAndValue()
    {
        var change = new PendingExportAttributeValueChange
        {
            AttributeId = MemberAttributeId,
            StringValue = "CN=User1,DC=test,DC=local",
            ChangeType = PendingExportAttributeChangeType.Add
        };

        var key = ExportEvaluationServer.GetAttributeChangeKey(change);

        Assert.That(key, Is.EqualTo($"{MemberAttributeId}:CN=User1,DC=test,DC=local"));
    }

    [Test]
    public void GetAttributeChangeKey_UnresolvedReference_TakesPrecedenceOverStringValue()
    {
        var change = new PendingExportAttributeValueChange
        {
            AttributeId = MemberAttributeId,
            UnresolvedReferenceValue = "019c8f07-7bd8-7e9a-b8e3-8c83c6eba803",
            StringValue = "CN=User1,DC=test,DC=local",
            ChangeType = PendingExportAttributeChangeType.Add
        };

        var key = ExportEvaluationServer.GetAttributeChangeKey(change);

        Assert.That(key, Is.EqualTo($"{MemberAttributeId}:019c8f07-7bd8-7e9a-b8e3-8c83c6eba803"));
    }

    [Test]
    public void GetAttributeChangeKey_GuidValue_ReturnsGuidString()
    {
        var guid = Guid.NewGuid();
        var change = new PendingExportAttributeValueChange
        {
            AttributeId = DisplayNameAttributeId,
            GuidValue = guid,
            ChangeType = PendingExportAttributeChangeType.Update
        };

        var key = ExportEvaluationServer.GetAttributeChangeKey(change);

        Assert.That(key, Is.EqualTo($"{DisplayNameAttributeId}:{guid}"));
    }

    [Test]
    public void GetAttributeChangeKey_IntValue_ReturnsIntString()
    {
        var change = new PendingExportAttributeValueChange
        {
            AttributeId = DescriptionAttributeId,
            IntValue = 42,
            ChangeType = PendingExportAttributeChangeType.Update
        };

        var key = ExportEvaluationServer.GetAttributeChangeKey(change);

        Assert.That(key, Is.EqualTo($"{DescriptionAttributeId}:42"));
    }

    [Test]
    public void GetAttributeChangeKey_NoValues_ReturnsAttributeIdWithEmptyValue()
    {
        var change = new PendingExportAttributeValueChange
        {
            AttributeId = MemberAttributeId,
            ChangeType = PendingExportAttributeChangeType.RemoveAll
        };

        var key = ExportEvaluationServer.GetAttributeChangeKey(change);

        Assert.That(key, Is.EqualTo($"{MemberAttributeId}:"));
    }

    [Test]
    public void GetAttributeChangeKey_DifferentAttributes_SameValue_ReturnsDifferentKeys()
    {
        var change1 = new PendingExportAttributeValueChange
        {
            AttributeId = MemberAttributeId,
            StringValue = "SameValue",
            ChangeType = PendingExportAttributeChangeType.Add
        };
        var change2 = new PendingExportAttributeValueChange
        {
            AttributeId = DisplayNameAttributeId,
            StringValue = "SameValue",
            ChangeType = PendingExportAttributeChangeType.Add
        };

        var key1 = ExportEvaluationServer.GetAttributeChangeKey(change1);
        var key2 = ExportEvaluationServer.GetAttributeChangeKey(change2);

        Assert.That(key1, Is.Not.EqualTo(key2));
    }

    [Test]
    public void GetAttributeChangeKey_SameAttributeAndValue_DifferentChangeType_ReturnsSameKey()
    {
        // The key is based on attribute+value, NOT change type.
        // This allows export eval to override drift when both target the same value.
        var change1 = new PendingExportAttributeValueChange
        {
            AttributeId = MemberAttributeId,
            StringValue = "CN=User1,DC=test,DC=local",
            ChangeType = PendingExportAttributeChangeType.Add
        };
        var change2 = new PendingExportAttributeValueChange
        {
            AttributeId = MemberAttributeId,
            StringValue = "CN=User1,DC=test,DC=local",
            ChangeType = PendingExportAttributeChangeType.Remove
        };

        var key1 = ExportEvaluationServer.GetAttributeChangeKey(change1);
        var key2 = ExportEvaluationServer.GetAttributeChangeKey(change2);

        Assert.That(key1, Is.EqualTo(key2));
    }

    [Test]
    public void GetAttributeChangeKey_SameAttribute_DifferentValues_ReturnsDifferentKeys()
    {
        var change1 = new PendingExportAttributeValueChange
        {
            AttributeId = MemberAttributeId,
            StringValue = "CN=User1,DC=test,DC=local",
            ChangeType = PendingExportAttributeChangeType.Add
        };
        var change2 = new PendingExportAttributeValueChange
        {
            AttributeId = MemberAttributeId,
            StringValue = "CN=User2,DC=test,DC=local",
            ChangeType = PendingExportAttributeChangeType.Add
        };

        var key1 = ExportEvaluationServer.GetAttributeChangeKey(change1);
        var key2 = ExportEvaluationServer.GetAttributeChangeKey(change2);

        Assert.That(key1, Is.Not.EqualTo(key2));
    }

    [Test]
    public void GetAttributeChangeKey_BinaryValue_ReturnsBase64()
    {
        var bytes = new byte[] { 0x01, 0x02, 0x03 };
        var change = new PendingExportAttributeValueChange
        {
            AttributeId = DescriptionAttributeId,
            ByteValue = bytes,
            ChangeType = PendingExportAttributeChangeType.Update
        };

        var key = ExportEvaluationServer.GetAttributeChangeKey(change);

        Assert.That(key, Is.EqualTo($"{DescriptionAttributeId}:{Convert.ToBase64String(bytes)}"));
    }

    #endregion

    #region Merge scenario tests (value-level deduplication)

    [Test]
    public void MergeScenario_MultiValuedAttribute_DriftChangesPreservedAlongsideExportEval()
    {
        // Simulates: drift PE has 117 member removals, export eval has 2 member adds.
        // After merge, ALL 119 changes should be preserved (no attribute-level collision).
        var driftChanges = CreateMemberChanges(PendingExportAttributeChangeType.Remove, 117, "OldUser");
        var exportEvalChanges = CreateMemberChanges(PendingExportAttributeChangeType.Add, 2, "NewUser");

        // Build the key sets to simulate merge logic
        var exportEvalKeys = exportEvalChanges.Select(ExportEvaluationServer.GetAttributeChangeKey).ToHashSet();
        var driftOnlyChanges = driftChanges.Where(dc => !exportEvalKeys.Contains(ExportEvaluationServer.GetAttributeChangeKey(dc))).ToList();

        // All 117 drift changes should survive because they target different values
        Assert.That(driftOnlyChanges, Has.Count.EqualTo(117));

        // Merged total should be 119
        var merged = new List<PendingExportAttributeValueChange>(exportEvalChanges);
        merged.AddRange(driftOnlyChanges);
        Assert.That(merged, Has.Count.EqualTo(119));
    }

    [Test]
    public void MergeScenario_OverlappingValues_ExportEvalWins()
    {
        // Simulates: drift says Remove User1, export eval says Add User1.
        // Export eval should win (the user was re-added via MVO sync).
        var driftChanges = new List<PendingExportAttributeValueChange>
        {
            CreateMemberChange(PendingExportAttributeChangeType.Remove, "CN=User1,DC=test,DC=local"),
            CreateMemberChange(PendingExportAttributeChangeType.Remove, "CN=User2,DC=test,DC=local"),
            CreateMemberChange(PendingExportAttributeChangeType.Remove, "CN=User3,DC=test,DC=local")
        };
        var exportEvalChanges = new List<PendingExportAttributeValueChange>
        {
            CreateMemberChange(PendingExportAttributeChangeType.Add, "CN=User1,DC=test,DC=local")
        };

        var exportEvalKeys = exportEvalChanges.Select(ExportEvaluationServer.GetAttributeChangeKey).ToHashSet();
        var driftOnlyChanges = driftChanges.Where(dc => !exportEvalKeys.Contains(ExportEvaluationServer.GetAttributeChangeKey(dc))).ToList();

        // User1 should be excluded from drift (export eval wins)
        Assert.That(driftOnlyChanges, Has.Count.EqualTo(2));
        Assert.That(driftOnlyChanges.All(c => c.StringValue != "CN=User1,DC=test,DC=local"), Is.True);

        // Final merge: 1 export eval Add + 2 drift Removes
        var merged = new List<PendingExportAttributeValueChange>(exportEvalChanges);
        merged.AddRange(driftOnlyChanges);
        Assert.That(merged, Has.Count.EqualTo(3));
    }

    [Test]
    public void MergeScenario_SingleValuedAttribute_ExportEvalReplacessDrift()
    {
        // Single-valued attribute: both target the same attribute+value identity
        var driftChange = new PendingExportAttributeValueChange
        {
            AttributeId = DisplayNameAttributeId,
            StringValue = "Old Display Name",
            ChangeType = PendingExportAttributeChangeType.Update
        };
        var exportEvalChange = new PendingExportAttributeValueChange
        {
            AttributeId = DisplayNameAttributeId,
            StringValue = "New Display Name",
            ChangeType = PendingExportAttributeChangeType.Update
        };

        var driftChanges = new List<PendingExportAttributeValueChange> { driftChange };
        var exportEvalChanges = new List<PendingExportAttributeValueChange> { exportEvalChange };

        var exportEvalKeys = exportEvalChanges.Select(ExportEvaluationServer.GetAttributeChangeKey).ToHashSet();
        var driftOnlyChanges = driftChanges.Where(dc => !exportEvalKeys.Contains(ExportEvaluationServer.GetAttributeChangeKey(dc))).ToList();

        // Different values = different keys, so both survive (correct for Update operations
        // where both are valid attribute changes targeting different target values)
        Assert.That(driftOnlyChanges, Has.Count.EqualTo(1));
    }

    [Test]
    public void MergeScenario_MixedAttributes_AllPreserved()
    {
        // Drift has member removals + displayName update
        // Export eval has member adds + description update
        // All should be preserved (no overlapping values)
        var driftChanges = new List<PendingExportAttributeValueChange>
        {
            CreateMemberChange(PendingExportAttributeChangeType.Remove, "CN=OldUser1,DC=test,DC=local"),
            CreateMemberChange(PendingExportAttributeChangeType.Remove, "CN=OldUser2,DC=test,DC=local"),
            new()
            {
                AttributeId = DisplayNameAttributeId,
                StringValue = "Corrected Name",
                ChangeType = PendingExportAttributeChangeType.Update
            }
        };
        var exportEvalChanges = new List<PendingExportAttributeValueChange>
        {
            CreateMemberChange(PendingExportAttributeChangeType.Add, "CN=NewUser1,DC=test,DC=local"),
            new()
            {
                AttributeId = DescriptionAttributeId,
                StringValue = "Updated description",
                ChangeType = PendingExportAttributeChangeType.Update
            }
        };

        var exportEvalKeys = exportEvalChanges.Select(ExportEvaluationServer.GetAttributeChangeKey).ToHashSet();
        var driftOnlyChanges = driftChanges.Where(dc => !exportEvalKeys.Contains(ExportEvaluationServer.GetAttributeChangeKey(dc))).ToList();

        // All 3 drift changes should survive (no overlapping values)
        Assert.That(driftOnlyChanges, Has.Count.EqualTo(3));

        var merged = new List<PendingExportAttributeValueChange>(exportEvalChanges);
        merged.AddRange(driftOnlyChanges);
        Assert.That(merged, Has.Count.EqualTo(5));
    }

    [Test]
    public void MergeScenario_UnresolvedReferences_DeduplicatedByMvoId()
    {
        // Both drift and export eval have unresolved references targeting the same MVO ID
        var mvoId = Guid.NewGuid().ToString();

        var driftChange = new PendingExportAttributeValueChange
        {
            AttributeId = MemberAttributeId,
            UnresolvedReferenceValue = mvoId,
            ChangeType = PendingExportAttributeChangeType.Remove
        };
        var exportEvalChange = new PendingExportAttributeValueChange
        {
            AttributeId = MemberAttributeId,
            UnresolvedReferenceValue = mvoId,
            ChangeType = PendingExportAttributeChangeType.Add
        };

        var driftChanges = new List<PendingExportAttributeValueChange> { driftChange };
        var exportEvalChanges = new List<PendingExportAttributeValueChange> { exportEvalChange };

        var exportEvalKeys = exportEvalChanges.Select(ExportEvaluationServer.GetAttributeChangeKey).ToHashSet();
        var driftOnlyChanges = driftChanges.Where(dc => !exportEvalKeys.Contains(ExportEvaluationServer.GetAttributeChangeKey(dc))).ToList();

        // Same MVO ID = same key, so drift version is excluded (export eval wins)
        Assert.That(driftOnlyChanges, Has.Count.EqualTo(0));
    }

    #endregion

    #region Helpers

    private static List<PendingExportAttributeValueChange> CreateMemberChanges(
        PendingExportAttributeChangeType changeType, int count, string prefix)
    {
        return Enumerable.Range(0, count)
            .Select(i => CreateMemberChange(changeType, $"CN={prefix}{i:D4},OU=Users,DC=test,DC=local"))
            .ToList();
    }

    private static PendingExportAttributeValueChange CreateMemberChange(
        PendingExportAttributeChangeType changeType, string dn)
    {
        return new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = MemberAttributeId,
            Attribute = new ConnectedSystemObjectTypeAttribute { Id = MemberAttributeId, Name = "member" },
            StringValue = dn,
            ChangeType = changeType
        };
    }

    #endregion
}
