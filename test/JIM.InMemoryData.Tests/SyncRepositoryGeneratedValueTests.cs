// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.InMemoryData.Tests;

/// <summary>
/// In-memory counterpart of <c>GeneratedValueRepositoryDatabaseTests</c> (Unique Value Generation, #242, Phase 1
/// step 4): the same semantics, exercised without PostgreSQL.
/// </summary>
[TestFixture]
public class SyncRepositoryGeneratedValueTests
{
    private SyncRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    #region Value gates

    [Test]
    public async Task GetMetaverseAttributeValuesInUseAsync_EmptyInput_ReturnsEmptyWithoutQueryingAsync()
    {
        var result = await _repo.GetMetaverseAttributeValuesInUseAsync(1, [], null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetMetaverseAttributeValuesInUseAsync_TakenValue_ReturnsItCaseInsensitivelyAsync()
    {
        SeedMvoWithStringValue(attributeId: 1, value: "Alice.Smith");

        var result = await _repo.GetMetaverseAttributeValuesInUseAsync(1, ["alice.smith", "bob.jones"], null);

        Assert.That(result, Is.EquivalentTo(new[] { "alice.smith" }).Using<string>((a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase)));
    }

    [Test]
    public async Task GetMetaverseAttributeValuesInUseAsync_ExcludedObjectsOwnValue_IsNotReportedTakenAsync()
    {
        var mvoId = SeedMvoWithStringValue(attributeId: 1, value: "alice.smith");

        var result = await _repo.GetMetaverseAttributeValuesInUseAsync(1, ["alice.smith"], mvoId);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetConnectedSystemAttributeValuesInUseAsync_ExcludedObjectsOwnValue_IsNotReportedTakenAsync()
    {
        var csoId = SeedCsoWithStringValue(attributeId: 1, value: "alice.smith");

        var result = await _repo.GetConnectedSystemAttributeValuesInUseAsync(1, ["alice.smith"], csoId);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetMetaverseAttributeNumbersInUseAsync_MatchesEitherIntOrLongValueAsync()
    {
        SeedMvoWithIntValue(attributeId: 2, value: 42);
        SeedMvoWithLongValue(attributeId: 2, value: 4200000000L);

        var result = await _repo.GetMetaverseAttributeNumbersInUseAsync(2, [42L, 4200000000L, 99L], null);

        Assert.That(result, Is.EquivalentTo(new[] { 42L, 4200000000L }));
    }

    [Test]
    public async Task GetConnectedSystemAttributeNumbersInUseAsync_ExcludedObjectsOwnValue_IsNotReportedTakenAsync()
    {
        var csoId = SeedCsoWithIntValue(attributeId: 2, value: 42);

        var result = await _repo.GetConnectedSystemAttributeNumbersInUseAsync(2, [42L], csoId);

        Assert.That(result, Is.Empty);
    }

    #endregion

    #region GetGeneratedValueAssignmentValuesInUseAsync

    [Test]
    public async Task GetGeneratedValueAssignmentValuesInUseAsync_EmptyInput_ReturnsEmptyAsync()
    {
        var result = await _repo.GetGeneratedValueAssignmentValuesInUseAsync(1, null, [], null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void GetGeneratedValueAssignmentValuesInUseAsync_NeitherIdGiven_ThrowsArgumentException()
    {
        Assert.That(async () => await _repo.GetGeneratedValueAssignmentValuesInUseAsync(null, null, ["a"], null), Throws.ArgumentException);
    }

    [Test]
    public void GetGeneratedValueAssignmentValuesInUseAsync_BothIdsGiven_ThrowsArgumentException()
    {
        Assert.That(async () => await _repo.GetGeneratedValueAssignmentValuesInUseAsync(1, 2, ["a"], null), Throws.ArgumentException);
    }

    [Test]
    public async Task GetGeneratedValueAssignmentValuesInUseAsync_ScopedByAttributeNotByGeneration_FindsAssignmentsFromAnyGenerationAsync()
    {
        // Decision 3: two different generation rows can target the same attribute; the gate must see both.
        var a = NewMetaverseAssignment(metaverseAttributeId: 1, mvoId: Guid.NewGuid(), value: "joe.bloggs");
        a.SyncRuleMappingGenerationId = 10;
        var b = NewMetaverseAssignment(metaverseAttributeId: 1, mvoId: Guid.NewGuid(), value: "jane.doe");
        b.SyncRuleMappingGenerationId = 20;
        _repo.SeedGeneratedValueAssignment(a);
        _repo.SeedGeneratedValueAssignment(b);

        var result = await _repo.GetGeneratedValueAssignmentValuesInUseAsync(1, null, ["joe.bloggs", "jane.doe", "unused"], null);

        Assert.That(result, Is.EquivalentTo(new[] { "joe.bloggs", "jane.doe" }));
    }

    [Test]
    public async Task GetGeneratedValueAssignmentValuesInUseAsync_CaseInsensitiveAsync()
    {
        var assignment = NewMetaverseAssignment(metaverseAttributeId: 1, mvoId: Guid.NewGuid(), value: "Joe.Bloggs");
        _repo.SeedGeneratedValueAssignment(assignment);

        var result = await _repo.GetGeneratedValueAssignmentValuesInUseAsync(1, null, ["joe.bloggs"], null);

        Assert.That(result, Is.EquivalentTo(new[] { "joe.bloggs" }).Using<string>((x, y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase)));
    }

    [Test]
    public async Task GetGeneratedValueAssignmentValuesInUseAsync_ExcludedObjectsOwnValue_IsNotReportedTakenAsync()
    {
        var mvoId = Guid.NewGuid();
        var assignment = NewMetaverseAssignment(metaverseAttributeId: 1, mvoId: mvoId, value: "joe.bloggs");
        _repo.SeedGeneratedValueAssignment(assignment);

        var result = await _repo.GetGeneratedValueAssignmentValuesInUseAsync(1, null, ["joe.bloggs"], mvoId);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetGeneratedValueAssignmentValuesInUseAsync_ConnectedSystemMode_ExcludesTheRequestingCsoAsync()
    {
        var csoId = Guid.NewGuid();
        var assignment = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObjectId = csoId,
            ConnectedSystemObjectTypeAttributeId = 3,
            Value = "abc123",
            NormalisedValue = "abc123",
            SyncRuleMappingGenerationId = 1
        };
        _repo.SeedGeneratedValueAssignment(assignment);

        var excluded = await _repo.GetGeneratedValueAssignmentValuesInUseAsync(null, 3, ["abc123"], csoId);
        var notExcluded = await _repo.GetGeneratedValueAssignmentValuesInUseAsync(null, 3, ["abc123"], Guid.NewGuid());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(excluded, Is.Empty);
            Assert.That(notExcluded, Is.EquivalentTo(new[] { "abc123" }));
        }
    }

    #endregion

    #region Assignments

    [Test]
    public async Task CreateGeneratedValueAssignmentsAsync_NoConflict_PersistsAsync()
    {
        var assignment = NewMetaverseAssignment(metaverseAttributeId: 1, mvoId: Guid.NewGuid(), value: "alice.smith");

        await _repo.CreateGeneratedValueAssignmentsAsync([assignment]);

        Assert.That(_repo.GeneratedValueAssignments.ContainsKey(assignment.Id), Is.True);
    }

    [Test]
    public void CreateGeneratedValueAssignmentsAsync_DuplicateNormalisedValueForSameAttribute_ThrowsConflict()
    {
        var existing = NewMetaverseAssignment(metaverseAttributeId: 1, mvoId: Guid.NewGuid(), value: "alice.smith");
        _repo.SeedGeneratedValueAssignment(existing);

        var colliding = NewMetaverseAssignment(metaverseAttributeId: 1, mvoId: Guid.NewGuid(), value: "ALICE.SMITH");

        Assert.That(async () => await _repo.CreateGeneratedValueAssignmentsAsync([colliding]),
            Throws.InstanceOf<GeneratedValueConflictException>());
    }

    [Test]
    public async Task GetGeneratedValueAssignmentAsync_Match_ReturnsItAsync()
    {
        var mvoId = Guid.NewGuid();
        var assignment = NewMetaverseAssignment(metaverseAttributeId: 1, mvoId: mvoId, value: "alice.smith");
        _repo.SeedGeneratedValueAssignment(assignment);

        var result = await _repo.GetGeneratedValueAssignmentAsync(mvoId, 1);

        Assert.That(result?.Id, Is.EqualTo(assignment.Id));
    }

    [Test]
    public async Task GetGeneratedValueAssignmentForConnectedSystemObjectAsync_Match_ReturnsItAsync()
    {
        var csoId = Guid.NewGuid();
        var assignment = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObjectId = csoId,
            ConnectedSystemObjectTypeAttributeId = 3,
            Value = "ABC123",
            NormalisedValue = "abc123",
            SyncRuleMappingGenerationId = 1
        };
        _repo.SeedGeneratedValueAssignment(assignment);

        var result = await _repo.GetGeneratedValueAssignmentForConnectedSystemObjectAsync(csoId, 3);

        Assert.That(result?.Id, Is.EqualTo(assignment.Id));
    }

    [Test]
    public async Task GetGeneratedValueAssignmentsForMetaverseObjectsAsync_ReturnsOnlyRequestedObjectsAsync()
    {
        var mvoId1 = Guid.NewGuid();
        var mvoId2 = Guid.NewGuid();
        var mvoId3 = Guid.NewGuid();
        _repo.SeedGeneratedValueAssignment(NewMetaverseAssignment(1, mvoId1, "a"));
        _repo.SeedGeneratedValueAssignment(NewMetaverseAssignment(1, mvoId2, "b"));
        _repo.SeedGeneratedValueAssignment(NewMetaverseAssignment(1, mvoId3, "c"));

        var result = await _repo.GetGeneratedValueAssignmentsForMetaverseObjectsAsync([mvoId1, mvoId2]);

        Assert.That(result.Select(a => a.MetaverseObjectId), Is.EquivalentTo(new Guid?[] { mvoId1, mvoId2 }));
    }

    [Test]
    public async Task GetGeneratedValueAssignmentsForGenerationAsync_ReturnsOnlyThatGenerationsRowsAsync()
    {
        var a = NewMetaverseAssignment(1, Guid.NewGuid(), "a");
        a.SyncRuleMappingGenerationId = 10;
        var b = NewMetaverseAssignment(1, Guid.NewGuid(), "b");
        b.SyncRuleMappingGenerationId = 20;
        _repo.SeedGeneratedValueAssignment(a);
        _repo.SeedGeneratedValueAssignment(b);

        var result = await _repo.GetGeneratedValueAssignmentsForGenerationAsync(10);

        Assert.That(result.Select(x => x.Id), Is.EquivalentTo(new[] { a.Id }));
    }

    [Test]
    public async Task DeleteGeneratedValueAssignmentsAsync_RemovesGivenIdsAsync()
    {
        var a = NewMetaverseAssignment(1, Guid.NewGuid(), "a");
        _repo.SeedGeneratedValueAssignment(a);

        await _repo.DeleteGeneratedValueAssignmentsAsync([a.Id]);

        Assert.That(_repo.GeneratedValueAssignments.ContainsKey(a.Id), Is.False);
    }

    [Test]
    public async Task UpdateGeneratedValueAssignmentAsync_StampsLastUpdatedAsync()
    {
        var a = NewMetaverseAssignment(1, Guid.NewGuid(), "a");
        a.LastUpdated = DateTime.UtcNow.AddDays(-1);
        _repo.SeedGeneratedValueAssignment(a);
        var before = a.LastUpdated;

        await _repo.UpdateGeneratedValueAssignmentAsync(a);

        Assert.That(a.LastUpdated, Is.GreaterThan(before));
    }

    #endregion

    #region Sequences

    [Test]
    public void GetGeneratedValueSequenceAsync_NeitherIdGiven_ThrowsArgumentException()
    {
        Assert.That(async () => await _repo.GetGeneratedValueSequenceAsync(null, null), Throws.ArgumentException);
    }

    [Test]
    public void GetGeneratedValueSequenceAsync_BothIdsGiven_ThrowsArgumentException()
    {
        Assert.That(async () => await _repo.GetGeneratedValueSequenceAsync(1, 2), Throws.ArgumentException);
    }

    [Test]
    public async Task GetHighestNumericValueForAttributeAsync_NoValues_ReturnsNullAsync()
    {
        var result = await _repo.GetHighestNumericValueForAttributeAsync(1, null);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetHighestNumericValueForAttributeAsync_AcrossIntLongAndDigitOnlyText_ReturnsTheMaximumAsync()
    {
        SeedMvoWithIntValue(attributeId: 5, value: 100);
        SeedMvoWithLongValue(attributeId: 5, value: 250);
        SeedMvoWithStringValue(attributeId: 5, value: "300");

        var result = await _repo.GetHighestNumericValueForAttributeAsync(5, null);

        Assert.That(result, Is.EqualTo(300L));
    }

    [Test]
    public async Task GetHighestNumericValueForAttributeAsync_PrefixedTextValue_IsNotConsideredAsync()
    {
        SeedMvoWithStringValue(attributeId: 6, value: "EMP0042");

        var result = await _repo.GetHighestNumericValueForAttributeAsync(6, null);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ReserveGeneratedValueSequenceBlockAsync_FirstReservation_SeedsAtFloorAndReturnsItAsync()
    {
        var first = await _repo.ReserveGeneratedValueSequenceBlockAsync(1, null, floor: 100, count: 5, increment: 1);

        Assert.That(first, Is.EqualTo(100));

        var sequence = await _repo.GetGeneratedValueSequenceAsync(1, null);
        Assert.That(sequence?.NextValue, Is.EqualTo(105));
    }

    [Test]
    public async Task ReserveGeneratedValueSequenceBlockAsync_SecondReservation_ContinuesFromTheCounterAsync()
    {
        await _repo.ReserveGeneratedValueSequenceBlockAsync(1, null, floor: 100, count: 5, increment: 1);
        var second = await _repo.ReserveGeneratedValueSequenceBlockAsync(1, null, floor: 100, count: 3, increment: 1);

        Assert.That(second, Is.EqualTo(105));
    }

    [Test]
    public async Task ReserveGeneratedValueSequenceBlockAsync_LowerFloorThanCounter_HasNoEffectBeyondItsOwnAdvanceAsync()
    {
        await _repo.ReserveGeneratedValueSequenceBlockAsync(1, null, floor: 100, count: 10, increment: 1);

        // The counter is now at 110; a reservation with a lower floor must still continue from 110, not drop
        // back to the floor (plan decision 3: the counter only ever moves forward).
        var next = await _repo.ReserveGeneratedValueSequenceBlockAsync(1, null, floor: 1, count: 1, increment: 1);

        Assert.That(next, Is.EqualTo(110));
    }

    [Test]
    public async Task ReserveGeneratedValueSequenceBlockAsync_HonoursIncrement_ReturnedBlockStepsByIncrementAsync()
    {
        var first = await _repo.ReserveGeneratedValueSequenceBlockAsync(1, null, floor: 0, count: 4, increment: 10);
        Assert.That(first, Is.EqualTo(0));

        var second = await _repo.ReserveGeneratedValueSequenceBlockAsync(1, null, floor: 0, count: 1, increment: 10);
        Assert.That(second, Is.EqualTo(40));
    }

    [Test]
    public async Task ReserveGeneratedValueSequenceBlockAsync_ConcurrentReservations_NeverOverlapAsync()
    {
        var results = new long[20];
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            results[i] = await _repo.ReserveGeneratedValueSequenceBlockAsync(1, null, floor: 1, count: 5, increment: 1);
        });

        await Task.WhenAll(tasks);

        // Every reserved block is 5 wide; no two of the 20 blocks may share a single number.
        var occupied = new HashSet<long>();
        foreach (var first in results)
        {
            for (var offset = 0; offset < 5; offset++)
                Assert.That(occupied.Add(first + offset), Is.True, $"value {first + offset} was reserved twice");
        }
    }

    [Test]
    public async Task IncrementGeneratedValueSequenceAssignedCountAsync_AdvancesTheDisplayCountAsync()
    {
        await _repo.ReserveGeneratedValueSequenceBlockAsync(1, null, floor: 1, count: 5, increment: 1);
        var sequence = await _repo.GetGeneratedValueSequenceAsync(1, null);

        await _repo.IncrementGeneratedValueSequenceAssignedCountAsync(sequence!.Id, 3);

        var updated = await _repo.GetGeneratedValueSequenceAsync(1, null);
        Assert.That(updated?.AssignedCount, Is.EqualTo(3));
    }

    #endregion

    #region Helpers

    private static GeneratedValueAssignment NewMetaverseAssignment(int metaverseAttributeId, Guid mvoId, string value) => new()
    {
        Id = Guid.NewGuid(),
        MetaverseObjectId = mvoId,
        MetaverseAttributeId = metaverseAttributeId,
        Value = value,
        NormalisedValue = value.ToLowerInvariant(),
        SyncRuleMappingGenerationId = 1
    };

    private Guid SeedMvoWithStringValue(int attributeId, string value)
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = [new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, StringValue = value }]
        };
        _repo.SeedMetaverseObject(mvo);
        return mvo.Id;
    }

    private void SeedMvoWithIntValue(int attributeId, int value)
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = [new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, IntValue = value }]
        };
        _repo.SeedMetaverseObject(mvo);
    }

    private void SeedMvoWithLongValue(int attributeId, long value)
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = [new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, LongValue = value }]
        };
        _repo.SeedMetaverseObject(mvo);
    }

    private Guid SeedCsoWithStringValue(int attributeId, string value)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = [new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, StringValue = value }]
        };
        _repo.SeedConnectedSystemObject(cso);
        return cso.Id;
    }

    private Guid SeedCsoWithIntValue(int attributeId, int value)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            AttributeValues = [new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, IntValue = value }]
        };
        _repo.SeedConnectedSystemObject(cso);
        return cso.Id;
    }

    #endregion
}
