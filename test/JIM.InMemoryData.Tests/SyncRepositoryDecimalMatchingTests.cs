// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.InMemoryData.Tests;

/// <summary>
/// Proves the in-memory repository's join/match value coalescing treats Decimal values numerically
/// (via canonical strings), matching the PostgreSQL implementation's numeric comparison where
/// 5.0 equals 5.00 despite differing stored scale.
/// </summary>
[TestFixture]
public class SyncRepositoryDecimalMatchingTests
{
    private SyncRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    private static ObjectMatchingRule CreateMatchingRule(
        int sourceAttrId, int targetAttrId, int? mvoTypeId = null)
    {
        return new ObjectMatchingRule
        {
            Id = 1,
            Order = 0,
            TargetMetaverseAttributeId = targetAttrId,
            MetaverseObjectTypeId = mvoTypeId,
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

    [Test]
    public async Task FindMatchingMetaverseObjectAsync_DecimalValuesDifferOnlyInScale_MatchesAsync()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 5 },
            AttributeValues = new()
            {
                new() { Id = Guid.NewGuid(), AttributeId = 10, DecimalValue = 5.00m }
            }
        };
        _repo.SeedMetaverseObject(mvo);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = new()
            {
                new() { Id = Guid.NewGuid(), AttributeId = 1, DecimalValue = 5.0m }
            }
        };

        var rule = CreateMatchingRule(sourceAttrId: 1, targetAttrId: 10, mvoTypeId: 5);
        var result = await _repo.FindMatchingMetaverseObjectAsync(cso, new List<ObjectMatchingRule> { rule });

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(mvo.Id));
    }

    [Test]
    public async Task FindMatchingMetaverseObjectAsync_DecimalValuesNumericallyDifferent_ReturnsNullAsync()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 5 },
            AttributeValues = new()
            {
                new() { Id = Guid.NewGuid(), AttributeId = 10, DecimalValue = 5.01m }
            }
        };
        _repo.SeedMetaverseObject(mvo);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = new()
            {
                new() { Id = Guid.NewGuid(), AttributeId = 1, DecimalValue = 5.0m }
            }
        };

        var rule = CreateMatchingRule(sourceAttrId: 1, targetAttrId: 10, mvoTypeId: 5);
        var result = await _repo.FindMatchingMetaverseObjectAsync(cso, new List<ObjectMatchingRule> { rule });

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task FindMetaverseObjectIdsByMatchValuesAsync_DecimalValuesDifferOnlyInScale_MatchesAsync()
    {
        var mvoType = new MetaverseObjectType { Id = 5 };
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = mvoType,
            AttributeValues = new()
            {
                new() { Id = Guid.NewGuid(), AttributeId = 10, DecimalValue = 10.50m }
            }
        };
        _repo.SeedMetaverseObject(mvo);

        var result = await _repo.FindMetaverseObjectIdsByMatchValuesAsync(
            mvoType.Id, metaverseAttributeId: 10, AttributeDataType.Decimal, [10.5m], caseSensitive: false, take: 2);

        Assert.That(result, Is.EqualTo(new[] { mvo.Id }));
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_DecimalValuesDifferOnlyInScale_MatchesAsync()
    {
        var cso = SeedDecimalCso(10.5m);

        var result = await _repo.GetExportMatchCandidateIdsAsync(1, 2, "salary", AttributeDataType.Decimal, caseSensitive: true, [10.50m]);

        Assert.That(result.Select(r => r.ConnectedSystemObjectId), Is.EqualTo(new[] { cso.Id }));
    }

    [Test]
    public async Task GetExportMatchCandidateIdsAsync_DecimalValuesNumericallyDifferent_ReturnsNothingAsync()
    {
        SeedDecimalCso(10.51m);

        var result = await _repo.GetExportMatchCandidateIdsAsync(1, 2, "salary", AttributeDataType.Decimal, caseSensitive: true, [10.50m]);

        Assert.That(result, Is.Empty);
    }

    /// <summary>An unjoined, Normal object of type 2 in Connected System 1 holding the given salary.</summary>
    private ConnectedSystemObject SeedDecimalCso(decimal salary)
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "salary", Type = AttributeDataType.Decimal };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            TypeId = 2,
            Status = ConnectedSystemObjectStatus.Normal,
            AttributeValues = new()
            {
                new() { Id = Guid.NewGuid(), AttributeId = attribute.Id, Attribute = attribute, DecimalValue = salary }
            }
        };
        _repo.SeedConnectedSystemObject(cso);
        return cso;
    }
}
