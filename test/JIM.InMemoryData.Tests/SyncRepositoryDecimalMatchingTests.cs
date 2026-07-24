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
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 10, DecimalValue = 5.00m }
            }
        };
        _repo.SeedMetaverseObject(mvo);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
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
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 10, DecimalValue = 5.01m }
            }
        };
        _repo.SeedMetaverseObject(mvo);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 1, DecimalValue = 5.0m }
            }
        };

        var rule = CreateMatchingRule(sourceAttrId: 1, targetAttrId: 10, mvoTypeId: 5);
        var result = await _repo.FindMatchingMetaverseObjectAsync(cso, new List<ObjectMatchingRule> { rule });

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task FindMetaverseObjectUsingMatchingRuleAsync_DecimalValuesDifferOnlyInScale_MatchesAsync()
    {
        var mvoType = new MetaverseObjectType { Id = 5 };
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = mvoType,
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 10, DecimalValue = 10.50m }
            }
        };
        _repo.SeedMetaverseObject(mvo);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 1, DecimalValue = 10.5m }
            }
        };

        var rule = CreateMatchingRule(sourceAttrId: 1, targetAttrId: 10);
        var result = await _repo.FindMetaverseObjectUsingMatchingRuleAsync(cso, mvoType, rule);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(mvo.Id));
    }

    [Test]
    public async Task FindConnectedSystemObjectUsingMatchingRuleAsync_DecimalValuesDifferOnlyInScale_MatchesAsync()
    {
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Test System" };
        var csoType = new ConnectedSystemObjectType { Id = 2, Name = "user" };

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            TypeId = csoType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 1, DecimalValue = 10.5m }
            }
        };
        _repo.SeedConnectedSystemObject(cso);

        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 10, DecimalValue = 10.50m }
            }
        };

        var rule = CreateMatchingRule(sourceAttrId: 1, targetAttrId: 10);
        var result = await _repo.FindConnectedSystemObjectUsingMatchingRuleAsync(mvo, connectedSystem, csoType, rule);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(cso.Id));
    }

    [Test]
    public async Task FindConnectedSystemObjectUsingMatchingRuleAsync_DecimalValuesNumericallyDifferent_ReturnsNullAsync()
    {
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Test System" };
        var csoType = new ConnectedSystemObjectType { Id = 2, Name = "user" };

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            TypeId = csoType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 1, DecimalValue = 10.51m }
            }
        };
        _repo.SeedConnectedSystemObject(cso);

        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = 10, DecimalValue = 10.50m }
            }
        };

        var rule = CreateMatchingRule(sourceAttrId: 1, targetAttrId: 10);
        var result = await _repo.FindConnectedSystemObjectUsingMatchingRuleAsync(mvo, connectedSystem, csoType, rule);

        Assert.That(result, Is.Null);
    }
}
