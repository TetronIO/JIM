// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.InMemoryData.Tests;

[TestFixture]
public class SyncRepositoryMvoTests
{
    private SyncRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    #region FindMatchingMetaverseObjectAsync

    [Test]
    public async Task FindMatchingMetaverseObjectAsync_NoMatch_ReturnsNullAsync()
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 1, StringValue = "john" }
            }
        };

        var rule = CreateMatchingRule(sourceAttrId: 1, targetAttrId: 10);
        var result = await _repo.FindMatchingMetaverseObjectAsync(cso, new List<ObjectMatchingRule> { rule });
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task FindMatchingMetaverseObjectAsync_SingleMatch_ReturnsMvoAsync()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 5 },
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 10, StringValue = "john" }
            }
        };
        _repo.SeedMetaverseObject(mvo);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 1, StringValue = "john" }
            }
        };

        var rule = CreateMatchingRule(sourceAttrId: 1, targetAttrId: 10, mvoTypeId: 5);
        var result = await _repo.FindMatchingMetaverseObjectAsync(cso, new List<ObjectMatchingRule> { rule });
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(mvo.Id));
    }

    [Test]
    public void FindMatchingMetaverseObjectAsync_MultipleMatches_ThrowsMultipleMatchesException()
    {
        var mvo1 = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 5 },
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 10, StringValue = "john" }
            }
        };
        var mvo2 = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 5 },
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 10, StringValue = "john" }
            }
        };
        _repo.SeedMetaverseObject(mvo1);
        _repo.SeedMetaverseObject(mvo2);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 1, StringValue = "john" }
            }
        };

        var rule = CreateMatchingRule(sourceAttrId: 1, targetAttrId: 10, mvoTypeId: 5);

        Assert.ThrowsAsync<MultipleMatchesException>(async () =>
            await _repo.FindMatchingMetaverseObjectAsync(cso, new List<ObjectMatchingRule> { rule }));
    }

    [Test]
    public async Task FindMatchingMetaverseObjectAsync_CaseInsensitive_MatchesAsync()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 5 },
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 10, StringValue = "JOHN" }
            }
        };
        _repo.SeedMetaverseObject(mvo);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 1, StringValue = "john" }
            }
        };

        var rule = CreateMatchingRule(sourceAttrId: 1, targetAttrId: 10, mvoTypeId: 5, caseSensitive: false);
        var result = await _repo.FindMatchingMetaverseObjectAsync(cso, new List<ObjectMatchingRule> { rule });
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task FindMatchingMetaverseObjectAsync_CaseSensitive_NoMatchAsync()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 5 },
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 10, StringValue = "JOHN" }
            }
        };
        _repo.SeedMetaverseObject(mvo);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 1, StringValue = "john" }
            }
        };

        var rule = CreateMatchingRule(sourceAttrId: 1, targetAttrId: 10, mvoTypeId: 5, caseSensitive: true);
        var result = await _repo.FindMatchingMetaverseObjectAsync(cso, new List<ObjectMatchingRule> { rule });
        Assert.That(result, Is.Null);
    }

    private static ObjectMatchingRule CreateMatchingRule(
        int sourceAttrId, int targetAttrId, int? mvoTypeId = null, bool caseSensitive = false)
    {
        return new ObjectMatchingRule
        {
            Id = 1,
            Order = 0,
            TargetMetaverseAttributeId = targetAttrId,
            MetaverseObjectTypeId = mvoTypeId,
            CaseSensitive = caseSensitive,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Id = 1,
                    Order = 0,
                    ConnectedSystemAttributeId = sourceAttrId
                }
            }
        };
    }

    #endregion

    #region MVO Writes

    [Test]
    public async Task CreateMetaverseObjectsAsync_AddsMvosAsync()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), AttributeValues = new() };
        await _repo.CreateMetaverseObjectsAsync(new[] { mvo });

        var match = await _repo.FindMatchingMetaverseObjectAsync(
            new ConnectedSystemObject { AttributeValues = new() },
            new List<ObjectMatchingRule>());
        // Can't easily read back without a read method, but verify no exception
    }

    [Test]
    public async Task CreateMetaverseObjectsAsync_GeneratesIdWhenEmptyAsync()
    {
        var mvo = new MetaverseObject { Id = Guid.Empty, AttributeValues = new() };
        await _repo.CreateMetaverseObjectsAsync(new[] { mvo });
        Assert.That(mvo.Id, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public async Task DeleteMetaverseObjectAsync_RemovesMvoAsync()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 5 },
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 10, StringValue = "test" }
            }
        };
        _repo.SeedMetaverseObject(mvo);

        await _repo.DeleteMetaverseObjectAsync(mvo);

        // Verify deleted by trying to match — should not find it
        var cso = new ConnectedSystemObject
        {
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { AttributeId = 1, StringValue = "test" }
            }
        };
        var rule = CreateMatchingRule(1, 10, 5);
        var result = await _repo.FindMatchingMetaverseObjectAsync(cso, new List<ObjectMatchingRule> { rule });
        Assert.That(result, Is.Null);
    }

    #endregion

    #region MVO deletion ghost reference rows (#1019)

    private const int MemberAttributeId = 86;

    private (MetaverseObject member, MetaverseObject survivor, MetaverseObject group) SeedGroupReferencingMemberAndSurvivor()
    {
        var member = new MetaverseObject { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 5 } };
        var survivor = new MetaverseObject { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 5 } };
        var group = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 6 },
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = MemberAttributeId, ReferenceValueId = member.Id },
                new() { Id = Guid.NewGuid(), AttributeId = MemberAttributeId, ReferenceValueId = survivor.Id }
            }
        };
        _repo.SeedMetaverseObject(member);
        _repo.SeedMetaverseObject(survivor);
        _repo.SeedMetaverseObject(group);
        return (member, survivor, group);
    }

    [Test]
    public async Task DeleteMetaverseObjectsAsync_SurvivingGroupReferencesDeletedMember_RemovesValuelessReferenceRowAsync()
    {
        var (member, survivor, group) = SeedGroupReferencingMemberAndSurvivor();

        await _repo.DeleteMetaverseObjectsAsync(new[] { member });

        Assert.That(group.AttributeValues, Has.Count.EqualTo(1),
            "The valueless row referencing the deleted member must be removed, not left as an all-null ghost");
        Assert.That(group.AttributeValues[0].ReferenceValueId, Is.EqualTo(survivor.Id),
            "The row referencing the surviving object must be untouched");
    }

    [Test]
    public async Task DeleteMetaverseObjectsAsync_NavigationOnlyReferenceToDeletedMember_RemovesRowAsync()
    {
        var member = new MetaverseObject { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 5 } };
        var group = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 6 },
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = MemberAttributeId, ReferenceValue = member }
            }
        };
        _repo.SeedMetaverseObject(member);
        _repo.SeedMetaverseObject(group);

        await _repo.DeleteMetaverseObjectsAsync(new[] { member });

        Assert.That(group.AttributeValues, Is.Empty,
            "A row referencing the deleted member via navigation only must also be removed");
    }

    [Test]
    public async Task DeleteMetaverseObjectsAsync_ReferenceRowWithPayload_IsNulledNotRemovedAsync()
    {
        var member = new MetaverseObject { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 5 } };
        var group = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 6 },
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = MemberAttributeId, ReferenceValueId = member.Id, StringValue = "payload" }
            }
        };
        _repo.SeedMetaverseObject(member);
        _repo.SeedMetaverseObject(group);

        await _repo.DeleteMetaverseObjectsAsync(new[] { member });

        Assert.That(group.AttributeValues, Has.Count.EqualTo(1),
            "A row carrying payload must survive with its reference nulled, preserving today's behaviour");
        Assert.That(group.AttributeValues[0].ReferenceValueId, Is.Null);
        Assert.That(group.AttributeValues[0].StringValue, Is.EqualTo("payload"));
    }

    [Test]
    public async Task DeleteMetaverseObjectsAsync_AssertedNullMarkerRow_SurvivesAsync()
    {
        var member = new MetaverseObject { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 5 } };
        var group = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 6 },
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = MemberAttributeId, ReferenceValueId = member.Id },
                new() { Id = Guid.NewGuid(), AttributeId = MemberAttributeId, NullValue = true }
            }
        };
        _repo.SeedMetaverseObject(member);
        _repo.SeedMetaverseObject(group);

        await _repo.DeleteMetaverseObjectsAsync(new[] { member });

        Assert.That(group.AttributeValues, Has.Count.EqualTo(1),
            "Only the row referencing the deleted member may be removed");
        Assert.That(group.AttributeValues[0].NullValue, Is.True,
            "The asserted-null marker row must survive deletion clean-up");
    }

    [Test]
    public async Task DeleteMetaverseObjectAsync_SingularForm_RemovesValuelessReferenceRowAsync()
    {
        var (member, survivor, group) = SeedGroupReferencingMemberAndSurvivor();

        await _repo.DeleteMetaverseObjectAsync(member);

        Assert.That(group.AttributeValues, Has.Count.EqualTo(1),
            "The singular deletion form must remove ghost rows identically to the plural form");
        Assert.That(group.AttributeValues[0].ReferenceValueId, Is.EqualTo(survivor.Id));
    }

    #endregion

    #region PersistPendingMvoChangesAsync (combined new + append)

    [Test]
    public async Task PersistPendingMvoChangesAsync_BothListsEmpty_NoOpAsync()
    {
        await _repo.PersistPendingMvoChangesAsync(
            new List<MetaverseObjectChange>(),
            new List<MetaverseObjectChange>());

        Assert.That(_repo.MetaverseObjectChanges, Is.Empty);
    }

    [Test]
    public async Task PersistPendingMvoChangesAsync_OnlyNewChanges_InsertsParentsAndChildrenAsync()
    {
        var change = BuildNewMvoChange(Guid.NewGuid());

        await _repo.PersistPendingMvoChangesAsync(
            new List<MetaverseObjectChange> { change },
            new List<MetaverseObjectChange>());

        Assert.That(change.Id, Is.Not.EqualTo(Guid.Empty));
        Assert.That(_repo.MetaverseObjectChanges, Has.Count.EqualTo(1));
        Assert.That(_repo.MetaverseObjectChanges.ContainsKey(change.Id), Is.True);
    }

    [Test]
    public async Task PersistPendingMvoChangesAsync_OnlyAppends_AddsAttributeChildrenToExistingParentAsync()
    {
        var parent = BuildNewMvoChange(Guid.NewGuid());
        parent.Id = Guid.NewGuid();
        await _repo.CreateMetaverseObjectChangeDirectAsync(parent);
        Assume.That(parent.AttributeChanges, Is.Empty, "Parent starts with no attribute children.");

        var append = new MetaverseObjectChange { Id = parent.Id };
        append.AttributeChanges.Add(new MetaverseObjectChangeAttribute
        {
            AttributeName = "DisplayName",
            AttributeType = AttributeDataType.Text
        });

        await _repo.PersistPendingMvoChangesAsync(
            new List<MetaverseObjectChange>(),
            new List<MetaverseObjectChange> { append });

        Assert.That(_repo.MetaverseObjectChanges[parent.Id].AttributeChanges, Has.Count.EqualTo(1));
        Assert.That(_repo.MetaverseObjectChanges[parent.Id].AttributeChanges[0].AttributeName, Is.EqualTo("DisplayName"));
    }

    [Test]
    public async Task PersistPendingMvoChangesAsync_BothLists_PersistsAtomicallyAsync()
    {
        // Existing parent (from a prior page) receives an attribute append.
        var existingParent = BuildNewMvoChange(Guid.NewGuid());
        existingParent.Id = Guid.NewGuid();
        await _repo.CreateMetaverseObjectChangeDirectAsync(existingParent);

        var append = new MetaverseObjectChange { Id = existingParent.Id };
        append.AttributeChanges.Add(new MetaverseObjectChangeAttribute
        {
            AttributeName = "Manager",
            AttributeType = AttributeDataType.Reference
        });

        // New parent for an RPEI persisted this round.
        var newParent = BuildNewMvoChange(Guid.NewGuid());

        await _repo.PersistPendingMvoChangesAsync(
            new List<MetaverseObjectChange> { newParent },
            new List<MetaverseObjectChange> { append });

        Assert.That(_repo.MetaverseObjectChanges, Has.Count.EqualTo(2));
        Assert.That(_repo.MetaverseObjectChanges[existingParent.Id].AttributeChanges, Has.Count.EqualTo(1));
        Assert.That(_repo.MetaverseObjectChanges[newParent.Id], Is.Not.Null);
    }

    private static MetaverseObjectChange BuildNewMvoChange(Guid rpeiId)
    {
        return new MetaverseObjectChange
        {
            ActivityRunProfileExecutionItemId = rpeiId,
            ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Projected,
            ChangeInitiatorType = MetaverseObjectChangeInitiatorType.SynchronisationRule,
            InitiatedByType = ActivityInitiatorType.System
        };
    }

    #endregion
}
