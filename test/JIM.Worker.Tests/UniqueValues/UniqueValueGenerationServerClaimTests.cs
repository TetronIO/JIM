// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Models.Core;
using JIM.Models.Transactional;
using JIM.Models.Utility;
using NUnit.Framework;
using InMemorySyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// <see cref="UniqueValueGenerationServer.ClaimValuesAsync"/> (#1789): a caller that builds its own values (example
/// data) asks the service whether each one is free, through the same reservation set and database gates a generated
/// value passes, and claims the ones that are.
/// </summary>
[TestFixture]
public class UniqueValueGenerationServerClaimTests
{
    private static UniqueValueClaim Claim(int attributeId, string value) => new()
    {
        MetaverseAttributeId = attributeId,
        TargetType = AttributeDataType.Text,
        AttributeName = "Email",
        Value = value
    };

    private static MetaverseObject HolderOf(int attributeId, string value) => new()
    {
        Id = TimeOrderedIds.New(),
        AttributeValues = [new MetaverseObjectAttributeValue { AttributeId = attributeId, StringValue = value }]
    };

    [Test]
    public async Task ClaimValuesAsync_FreeValue_IsClaimedAndReservedAsync()
    {
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var reservations = new UniqueValueReservationSet();
        var server = new UniqueValueGenerationServer(new InMemorySyncRepository());

        var claimed = await server.ClaimValuesAsync([Claim(attributeId, "ada.lovelace@panoply.local")], UniqueValueTestHelpers.Options(reservations));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(claimed, Is.EqualTo(new[] { true }));
            Assert.That(reservations.IsReservedByAnotherOwner(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, attributeId, "ada.lovelace@panoply.local"), Is.True,
                "a claimed value is held in the shared reservation set until the caller releases it");
        }
    }

    [Test]
    public async Task ClaimValuesAsync_ValueHeldByAMetaverseObjectInAnotherCase_IsNotClaimedAsync()
    {
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var repo = new InMemorySyncRepository();
        repo.SeedMetaverseObject(HolderOf(attributeId, "Ada.Lovelace@panoply.local"));
        var server = new UniqueValueGenerationServer(repo);

        var claimed = await server.ClaimValuesAsync([Claim(attributeId, "ada.lovelace@panoply.local")], UniqueValueTestHelpers.Options());

        Assert.That(claimed, Is.EqualTo(new[] { false }));
    }

    [Test]
    public async Task ClaimValuesAsync_ValueReservedByAnotherOwner_IsNotClaimedAsync()
    {
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var reservations = new UniqueValueReservationSet();
        reservations.TryReserve(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, attributeId, "ada.lovelace@panoply.local");
        var server = new UniqueValueGenerationServer(new InMemorySyncRepository());

        var claimed = await server.ClaimValuesAsync([Claim(attributeId, "ada.lovelace@panoply.local")], UniqueValueTestHelpers.Options(reservations));

        Assert.That(claimed, Is.EqualTo(new[] { false }));
    }

    [Test]
    public async Task ClaimValuesAsync_ValueAlreadyClaimedByTheSameOwner_IsNotClaimedTwiceAsync()
    {
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var options = UniqueValueTestHelpers.Options();
        var server = new UniqueValueGenerationServer(new InMemorySyncRepository());

        await server.ClaimValuesAsync([Claim(attributeId, "ada.lovelace@panoply.local")], options);
        var second = await server.ClaimValuesAsync([Claim(attributeId, "Ada.Lovelace@panoply.local")], options);

        Assert.That(second, Is.EqualTo(new[] { false }), "two objects in one run never receive the same value");
    }

    [Test]
    public async Task ClaimValuesAsync_SameValueTwiceInOneCall_OnlyTheFirstIsClaimedAsync()
    {
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var server = new UniqueValueGenerationServer(new InMemorySyncRepository());

        var claimed = await server.ClaimValuesAsync(
            [Claim(attributeId, "ada.lovelace@panoply.local"), Claim(attributeId, "grace.hopper@panoply.local"), Claim(attributeId, "ada.lovelace@panoply.local")],
            UniqueValueTestHelpers.Options());

        Assert.That(claimed, Is.EqualTo(new[] { true, true, false }));
    }

    [Test]
    public async Task ClaimValuesAsync_ValueInTheRetiredRegister_IsNotClaimedAsync()
    {
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var repo = new InMemorySyncRepository();
        repo.SeedRetiredGeneratedValue(new RetiredGeneratedValue
        {
            MetaverseAttributeId = attributeId,
            Value = "ada.lovelace@panoply.local",
            NormalisedValue = "ada.lovelace@panoply.local",
            RetiredAt = DateTime.UtcNow,
            Reason = RetiredGeneratedValueReason.ObjectDeleted
        });
        var server = new UniqueValueGenerationServer(repo);

        var claimed = await server.ClaimValuesAsync([Claim(attributeId, "ada.lovelace@panoply.local")], UniqueValueTestHelpers.Options());

        Assert.That(claimed, Is.EqualTo(new[] { false }), "a value retired by a Synchronisation Rule is never issued again, by anyone");
    }

    [Test]
    public async Task ClaimValuesAsync_ValueHeldByALiveAssignment_IsNotClaimedAsync()
    {
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var repo = new InMemorySyncRepository();
        repo.SeedGeneratedValueAssignment(new GeneratedValueAssignment
        {
            Id = TimeOrderedIds.New(),
            MetaverseObjectId = TimeOrderedIds.New(),
            MetaverseAttributeId = attributeId,
            Value = "ada.lovelace@panoply.local",
            NormalisedValue = "ada.lovelace@panoply.local",
            State = GeneratedValueAssignmentState.Committed
        });
        var server = new UniqueValueGenerationServer(repo);

        var claimed = await server.ClaimValuesAsync([Claim(attributeId, "ada.lovelace@panoply.local")], UniqueValueTestHelpers.Options());

        Assert.That(claimed, Is.EqualTo(new[] { false }));
    }

    [Test]
    public async Task ClaimValuesAsync_NumberHeldByAMetaverseObject_IsNotClaimedAsync()
    {
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var repo = new InMemorySyncRepository();
        repo.SeedMetaverseObject(new MetaverseObject
        {
            Id = TimeOrderedIds.New(),
            AttributeValues = [new MetaverseObjectAttributeValue { AttributeId = attributeId, IntValue = 42 }]
        });
        var server = new UniqueValueGenerationServer(repo);

        var claimed = await server.ClaimValuesAsync(
            [new UniqueValueClaim { MetaverseAttributeId = attributeId, TargetType = AttributeDataType.Number, AttributeName = "Level", Value = "42", NumericValue = 42 }],
            UniqueValueTestHelpers.Options());

        Assert.That(claimed, Is.EqualTo(new[] { false }));
    }

    [Test]
    public async Task ClaimValuesAsync_NoClaims_ReturnsEmptyAsync()
    {
        var server = new UniqueValueGenerationServer(new InMemorySyncRepository());

        var claimed = await server.ClaimValuesAsync([], UniqueValueTestHelpers.Options());

        Assert.That(claimed, Is.Empty);
    }
}
