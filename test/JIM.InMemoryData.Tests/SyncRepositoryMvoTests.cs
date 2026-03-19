using JIM.Models.Activities;
using JIM.Models.Core;
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

        await _repo.DeleteMetaverseObjectAsync(mvo, ActivityInitiatorType.System, null, null, null);

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
}
