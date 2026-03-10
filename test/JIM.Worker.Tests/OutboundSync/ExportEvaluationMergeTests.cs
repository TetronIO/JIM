using JIM.Application.Servers;
using JIM.Models.Core;
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
    private const int TitleAttributeId = 400;

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
        var exportEvalKeys = exportEvalChanges.Select(ExportEvaluationServer.GetAttributeChangeMergeKey).ToHashSet();
        var driftOnlyChanges = driftChanges.Where(dc => !exportEvalKeys.Contains(ExportEvaluationServer.GetAttributeChangeMergeKey(dc))).ToList();

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

        var exportEvalKeys = exportEvalChanges.Select(ExportEvaluationServer.GetAttributeChangeMergeKey).ToHashSet();
        var driftOnlyChanges = driftChanges.Where(dc => !exportEvalKeys.Contains(ExportEvaluationServer.GetAttributeChangeMergeKey(dc))).ToList();

        // User1 should be excluded from drift (export eval wins)
        Assert.That(driftOnlyChanges, Has.Count.EqualTo(2));
        Assert.That(driftOnlyChanges.All(c => c.StringValue != "CN=User1,DC=test,DC=local"), Is.True);

        // Final merge: 1 export eval Add + 2 drift Removes
        var merged = new List<PendingExportAttributeValueChange>(exportEvalChanges);
        merged.AddRange(driftOnlyChanges);
        Assert.That(merged, Has.Count.EqualTo(3));
    }

    [Test]
    public void MergeScenario_SingleValuedAttribute_DifferentValues_ExportEvalWins()
    {
        // Single-valued attribute with different old/new values: export eval should replace drift.
        // This is the scenario that caused the LDAP "SINGLE-VALUE attribute specified more than once" error.
        // A stale PE had title="Developer", new export eval has title="Senior Developer".
        // The merge must keep only the new value.
        var driftChange = CreateSingleValuedChange(TitleAttributeId, "title", "Developer");
        var exportEvalChange = CreateSingleValuedChange(TitleAttributeId, "title", "Senior Developer");

        var driftChanges = new List<PendingExportAttributeValueChange> { driftChange };
        var exportEvalChanges = new List<PendingExportAttributeValueChange> { exportEvalChange };

        var exportEvalKeys = exportEvalChanges.Select(ExportEvaluationServer.GetAttributeChangeMergeKey).ToHashSet();
        var driftOnlyChanges = driftChanges.Where(dc => !exportEvalKeys.Contains(ExportEvaluationServer.GetAttributeChangeMergeKey(dc))).ToList();

        // Drift change should be excluded — same attribute ID key for single-valued
        Assert.That(driftOnlyChanges, Has.Count.EqualTo(0));

        // Final merge: only the new value survives
        var merged = new List<PendingExportAttributeValueChange>(exportEvalChanges);
        merged.AddRange(driftOnlyChanges);
        Assert.That(merged, Has.Count.EqualTo(1));
        Assert.That(merged[0].StringValue, Is.EqualTo("Senior Developer"));
    }

    [Test]
    public void MergeScenario_SingleValuedAttribute_SameValues_ExportEvalWins()
    {
        // Single-valued attribute with identical values: export eval should replace drift.
        var driftChange = CreateSingleValuedChange(DisplayNameAttributeId, "displayName", "Same Name");
        var exportEvalChange = CreateSingleValuedChange(DisplayNameAttributeId, "displayName", "Same Name");

        var driftChanges = new List<PendingExportAttributeValueChange> { driftChange };
        var exportEvalChanges = new List<PendingExportAttributeValueChange> { exportEvalChange };

        var exportEvalKeys = exportEvalChanges.Select(ExportEvaluationServer.GetAttributeChangeMergeKey).ToHashSet();
        var driftOnlyChanges = driftChanges.Where(dc => !exportEvalKeys.Contains(ExportEvaluationServer.GetAttributeChangeMergeKey(dc))).ToList();

        Assert.That(driftOnlyChanges, Has.Count.EqualTo(0));
    }

    [Test]
    public void MergeScenario_MixedAttributes_SingleValuedDeduped_MultiValuedPreserved()
    {
        // Drift has member removals + title update (single-valued, old value)
        // Export eval has member adds + title update (single-valued, new value)
        // Member changes should all survive (multi-valued, different values).
        // Title drift should be replaced by export eval (single-valued, same attribute).
        var driftChanges = new List<PendingExportAttributeValueChange>
        {
            CreateMemberChange(PendingExportAttributeChangeType.Remove, "CN=OldUser1,DC=test,DC=local"),
            CreateMemberChange(PendingExportAttributeChangeType.Remove, "CN=OldUser2,DC=test,DC=local"),
            CreateSingleValuedChange(TitleAttributeId, "title", "Developer")
        };
        var exportEvalChanges = new List<PendingExportAttributeValueChange>
        {
            CreateMemberChange(PendingExportAttributeChangeType.Add, "CN=NewUser1,DC=test,DC=local"),
            CreateSingleValuedChange(TitleAttributeId, "title", "Senior Developer")
        };

        var exportEvalKeys = exportEvalChanges.Select(ExportEvaluationServer.GetAttributeChangeMergeKey).ToHashSet();
        var driftOnlyChanges = driftChanges.Where(dc => !exportEvalKeys.Contains(ExportEvaluationServer.GetAttributeChangeMergeKey(dc))).ToList();

        // Only the 2 member removals should survive — title drift is replaced by export eval
        Assert.That(driftOnlyChanges, Has.Count.EqualTo(2));
        Assert.That(driftOnlyChanges.All(c => c.AttributeId == MemberAttributeId), Is.True);

        var merged = new List<PendingExportAttributeValueChange>(exportEvalChanges);
        merged.AddRange(driftOnlyChanges);
        Assert.That(merged, Has.Count.EqualTo(4));
    }

    [Test]
    public void MergeScenario_UnresolvedReferences_DeduplicatedByMvoId()
    {
        // Both drift and export eval have unresolved references targeting the same MVO ID
        var mvoId = Guid.NewGuid().ToString();

        var driftChange = new PendingExportAttributeValueChange
        {
            AttributeId = MemberAttributeId,
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = MemberAttributeId, Name = "member",
                AttributePlurality = AttributePlurality.MultiValued
            },
            UnresolvedReferenceValue = mvoId,
            ChangeType = PendingExportAttributeChangeType.Remove
        };
        var exportEvalChange = new PendingExportAttributeValueChange
        {
            AttributeId = MemberAttributeId,
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = MemberAttributeId, Name = "member",
                AttributePlurality = AttributePlurality.MultiValued
            },
            UnresolvedReferenceValue = mvoId,
            ChangeType = PendingExportAttributeChangeType.Add
        };

        var driftChanges = new List<PendingExportAttributeValueChange> { driftChange };
        var exportEvalChanges = new List<PendingExportAttributeValueChange> { exportEvalChange };

        var exportEvalKeys = exportEvalChanges.Select(ExportEvaluationServer.GetAttributeChangeMergeKey).ToHashSet();
        var driftOnlyChanges = driftChanges.Where(dc => !exportEvalKeys.Contains(ExportEvaluationServer.GetAttributeChangeMergeKey(dc))).ToList();

        // Same MVO ID = same key, so drift version is excluded (export eval wins)
        Assert.That(driftOnlyChanges, Has.Count.EqualTo(0));
    }

    #endregion

    #region GetAttributeChangeMergeKey tests

    [Test]
    public void GetAttributeChangeMergeKey_SingleValued_KeysByAttributeIdOnly()
    {
        // Single-valued attributes should key by attribute ID alone, regardless of value.
        var change1 = CreateSingleValuedChange(TitleAttributeId, "title", "Developer");
        var change2 = CreateSingleValuedChange(TitleAttributeId, "title", "Senior Developer");

        var key1 = ExportEvaluationServer.GetAttributeChangeMergeKey(change1);
        var key2 = ExportEvaluationServer.GetAttributeChangeMergeKey(change2);

        Assert.That(key1, Is.EqualTo(key2), "Single-valued attributes with different values should have the same merge key");
        Assert.That(key1, Is.EqualTo(TitleAttributeId.ToString()));
    }

    [Test]
    public void GetAttributeChangeMergeKey_MultiValued_KeysByAttributeIdAndValue()
    {
        // Multi-valued attributes should key by attribute ID + value (each distinct value preserved).
        var change1 = CreateMemberChange(PendingExportAttributeChangeType.Add, "CN=User1,DC=test,DC=local");
        var change2 = CreateMemberChange(PendingExportAttributeChangeType.Add, "CN=User2,DC=test,DC=local");

        var key1 = ExportEvaluationServer.GetAttributeChangeMergeKey(change1);
        var key2 = ExportEvaluationServer.GetAttributeChangeMergeKey(change2);

        Assert.That(key1, Is.Not.EqualTo(key2), "Multi-valued attributes with different values should have different merge keys");
    }

    [Test]
    public void GetAttributeChangeMergeKey_MultiValued_SameValue_SameKey()
    {
        var change1 = CreateMemberChange(PendingExportAttributeChangeType.Add, "CN=User1,DC=test,DC=local");
        var change2 = CreateMemberChange(PendingExportAttributeChangeType.Remove, "CN=User1,DC=test,DC=local");

        var key1 = ExportEvaluationServer.GetAttributeChangeMergeKey(change1);
        var key2 = ExportEvaluationServer.GetAttributeChangeMergeKey(change2);

        Assert.That(key1, Is.EqualTo(key2), "Multi-valued attributes with same value should have same merge key");
    }

    [Test]
    public void GetAttributeChangeMergeKey_NullAttribute_TreatedAsSingleValued()
    {
        // When Attribute navigation property is null (defensive case), treat as single-valued
        // (key by attribute ID only) — safer to dedup than to allow duplicates.
        var change = new PendingExportAttributeValueChange
        {
            AttributeId = DisplayNameAttributeId,
            Attribute = null!,
            StringValue = "Some Value",
            ChangeType = PendingExportAttributeChangeType.Update
        };

        var key = ExportEvaluationServer.GetAttributeChangeMergeKey(change);

        Assert.That(key, Is.EqualTo(DisplayNameAttributeId.ToString()));
    }

    [Test]
    public void GetAttributeChangeMergeKey_DifferentSingleValuedAttributes_DifferentKeys()
    {
        var change1 = CreateSingleValuedChange(TitleAttributeId, "title", "Developer");
        var change2 = CreateSingleValuedChange(DisplayNameAttributeId, "displayName", "Developer");

        var key1 = ExportEvaluationServer.GetAttributeChangeMergeKey(change1);
        var key2 = ExportEvaluationServer.GetAttributeChangeMergeKey(change2);

        Assert.That(key1, Is.Not.EqualTo(key2), "Different attributes should always have different merge keys");
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
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = MemberAttributeId,
                Name = "member",
                AttributePlurality = AttributePlurality.MultiValued
            },
            StringValue = dn,
            ChangeType = changeType
        };
    }

    private static PendingExportAttributeValueChange CreateSingleValuedChange(
        int attributeId, string attributeName, string value)
    {
        return new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = attributeId,
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = attributeId,
                Name = attributeName,
                AttributePlurality = AttributePlurality.SingleValued
            },
            StringValue = value,
            ChangeType = PendingExportAttributeChangeType.Update
        };
    }

    #endregion
}
