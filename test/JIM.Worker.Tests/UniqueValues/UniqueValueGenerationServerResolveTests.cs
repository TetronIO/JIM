// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;
using InMemorySyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// <see cref="UniqueValueGenerationServer.ResolveAsync"/>: sticky, adopt before generate, candidate generation
/// through the ordered gates, exhaustion, width overflow, sequence seeding and block reservation, run-scoped
/// caching, and dry run (Unique Value Generation, #242, Phase 2).
/// </summary>
[TestFixture]
public class UniqueValueGenerationServerResolveTests
{
    // ---- Sticky (FR 10) ----

    [Test]
    public async Task ResolveAsync_LiveAssignmentExists_ReturnsStickyWithNoGateQueriesAsync()
    {
        var repo = new InMemorySyncRepository();
        var (countingRepo, counts) = CountingSyncRepositoryProxy.Create(repo);
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var mvoId = Guid.NewGuid();

        var existing = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = mvoId,
            MetaverseAttributeId = attributeId,
            Value = "joe.bloggs",
            NormalisedValue = "joe.bloggs",
            State = GeneratedValueAssignmentState.Committed
        };
        repo.SeedGeneratedValueAssignment(existing);

        var server = new UniqueValueGenerationServer(countingRepo);
        var generation = UniqueValueTestHelpers.Generation();
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, mvoId, baseValue: "joe.bloggs2");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Sticky));
            Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs"));
            Assert.That(outcomes[0].Assignment, Is.SameAs(existing));
            Assert.That(counts.ContainsKey(nameof(ISyncRepository.GetMetaverseAttributeValuesInUseAsync)), Is.False);
            Assert.That(counts.ContainsKey(nameof(ISyncRepository.ReserveGeneratedValueSequenceBlockAsync)), Is.False);
        }
    }

    [Test]
    public async Task ResolveAsync_RequestingObjectHasNoAssignment_DoesNotReturnStickyAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var server = new UniqueValueGenerationServer(repo);
        var generation = UniqueValueTestHelpers.Generation();
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, Guid.NewGuid(), baseValue: "joe.bloggs");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Generated));
    }

    // ---- StickyOnly / Waiting ----

    [Test]
    public async Task ResolveAsync_StickyOnlyWithLiveAssignment_ReturnsStickyAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var mvoId = Guid.NewGuid();
        var existing = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = mvoId, MetaverseAttributeId = attributeId,
            Value = "joe.bloggs", NormalisedValue = "joe.bloggs", State = GeneratedValueAssignmentState.Committed
        };
        repo.SeedGeneratedValueAssignment(existing);

        var server = new UniqueValueGenerationServer(repo);
        var generation = UniqueValueTestHelpers.Generation();
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, mvoId, baseValue: null, stickyOnly: true);

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Sticky));
    }

    [Test]
    public async Task ResolveAsync_StickyOnlyWithNoLiveAssignment_ReturnsWaitingWithNoGateOrSequenceCallsAsync()
    {
        var repo = new InMemorySyncRepository();
        var (countingRepo, counts) = CountingSyncRepositoryProxy.Create(repo);
        var attributeId = UniqueValueTestHelpers.NextAttributeId();

        var server = new UniqueValueGenerationServer(countingRepo);
        var generation = UniqueValueTestHelpers.Generation();
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, Guid.NewGuid(), baseValue: null, stickyOnly: true);

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Waiting));
            Assert.That(outcomes[0].Value, Is.Null);
            Assert.That(outcomes[0].Assignment, Is.Null);
            Assert.That(counts.ContainsKey(nameof(ISyncRepository.GetMetaverseAttributeValuesInUseAsync)), Is.False);
            Assert.That(counts.ContainsKey(nameof(ISyncRepository.ReserveGeneratedValueSequenceBlockAsync)), Is.False);
            Assert.That(counts.ContainsKey(nameof(ISyncRepository.GetGeneratedValueAssignmentsForGenerationAsync)), Is.False);
        }
    }

    // ---- Adopt before generate (FR 30) ----

    [Test]
    public async Task ResolveAsync_AdoptableValueFree_ReturnsAdoptedCommittedAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var server = new UniqueValueGenerationServer(repo);
        var generation = UniqueValueTestHelpers.Generation();
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, Guid.NewGuid(), baseValue: null, adoptableValue: "jsmith");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Adopted));
            Assert.That(outcomes[0].Value, Is.EqualTo("jsmith"));
            Assert.That(outcomes[0].Assignment!.State, Is.EqualTo(GeneratedValueAssignmentState.Committed));
            Assert.That(outcomes[0].Assignment!.Adopted, Is.True);
            Assert.That(outcomes[0].Assignment!.CommittedAt, Is.Not.Null);
        }
    }

    [Test]
    public async Task ResolveAsync_AdoptableValueHeldByAnotherLiveAssignment_ReturnsAdoptionConflictAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = UniqueValueTestHelpers.Generation();

        repo.SeedGeneratedValueAssignment(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = Guid.NewGuid(), MetaverseAttributeId = attributeId,
            Value = "jsmith", NormalisedValue = "jsmith", State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = generation.Id
        });

        var server = new UniqueValueGenerationServer(repo);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, Guid.NewGuid(), baseValue: null, adoptableValue: "jsmith");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.AdoptionConflict));
    }

    [Test]
    public async Task ResolveAsync_AdoptableValueHeldByAReservation_ReturnsAdoptionConflictAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = UniqueValueTestHelpers.Generation();

        var reservations = new UniqueValueReservationSet();
        reservations.TryReserve(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, attributeId, "jsmith");

        var server = new UniqueValueGenerationServer(repo);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, Guid.NewGuid(), baseValue: null, adoptableValue: "jsmith");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options(reservations));

        Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.AdoptionConflict));
    }

    [Test]
    public async Task ResolveAsync_AdoptableValueHeldByAnotherGenerationOnTheSameAttribute_ReturnsAdoptionConflictAsync()
    {
        // Two different Synchronisation Rule mappings can target the same Metaverse attribute (plan decision 3):
        // the adoption conflict check must be scoped by attribute, not by which generation row asked.
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generationA = UniqueValueTestHelpers.Generation();
        var generationB = UniqueValueTestHelpers.Generation();

        repo.SeedGeneratedValueAssignment(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = Guid.NewGuid(), MetaverseAttributeId = attributeId,
            Value = "jsmith", NormalisedValue = "jsmith", State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = generationA.Id
        });

        var server = new UniqueValueGenerationServer(repo);
        var request = UniqueValueTestHelpers.ImportRequest(generationB, attributeId, Guid.NewGuid(), baseValue: null, adoptableValue: "jsmith");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.AdoptionConflict));
    }

    // ---- Gate order and short-circuit ----

    [Test]
    public async Task ResolveAsync_CandidateTakenAtReservationGate_NeverQueriesLaterGatesAsync()
    {
        var repo = new InMemorySyncRepository();
        var (countingRepo, counts) = CountingSyncRepositoryProxy.Create(repo);
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = UniqueValueTestHelpers.Generation(attemptLimit: 1);

        var reservations = new UniqueValueReservationSet();
        reservations.TryReserve(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, attributeId, "joe.bloggs");

        var server = new UniqueValueGenerationServer(countingRepo);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: "joe.bloggs");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options(reservations));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Exhausted));
            Assert.That(counts.ContainsKey(nameof(ISyncRepository.GetMetaverseAttributeValuesInUseAsync)), Is.False,
                "a candidate rejected by the reservation gate must never reach the Metaverse gate");
            Assert.That(counts.ContainsKey(nameof(ISyncRepository.GetGeneratedValueAssignmentsForGenerationAsync)), Is.False,
                "a candidate rejected by the reservation gate must never reach the other-live-assignments gate");
        }
    }

    // ---- Case-insensitivity and self-exclusion ----

    [Test]
    public async Task ResolveAsync_MetaverseGate_IsCaseInsensitiveAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var takenMvo = SeedMvoWithValue(repo, attributeId, "Joe.Bloggs");

        var server = new UniqueValueGenerationServer(repo);
        var generation = UniqueValueTestHelpers.Generation();
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: "joe.bloggs");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Generated));
            Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs1"), "the lower-case candidate must still collide with the mixed-case existing value");
        }

        GC.KeepAlive(takenMvo);
    }

    [Test]
    public async Task ResolveAsync_CandidateMatchesRequestingObjectsOwnValue_IsFreeForItAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var mvoId = SeedMvoWithValue(repo, attributeId, "joe.bloggs");

        var server = new UniqueValueGenerationServer(repo);
        var generation = UniqueValueTestHelpers.Generation();
        // The requesting object itself already holds "joe.bloggs" for this attribute (outside the assignment
        // model, e.g. a non-generated legacy value); its own candidate must not collide with itself.
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, mvoId, baseValue: "joe.bloggs");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs"));
    }

    // ---- Intra-call duplicates (Scenario 3) ----

    [Test]
    public async Task ResolveAsync_TwoRequestsSameAttributeSameBase_ResolveToDistinctValuesAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = UniqueValueTestHelpers.Generation();
        var server = new UniqueValueGenerationServer(repo);

        var requestA = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: "john.smith");
        var requestB = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: "john.smith");

        var outcomes = await server.ResolveAsync([requestA, requestB], UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Generated));
            Assert.That(outcomes[1].Kind, Is.EqualTo(GenerationOutcomeKind.Generated));
            Assert.That(outcomes[0].Value, Is.Not.EqualTo(outcomes[1].Value));
            Assert.That(new[] { outcomes[0].Value, outcomes[1].Value }, Is.EquivalentTo(new[] { "john.smith", "john.smith1" }));
        }
    }

    // ---- Gate (e): scoped by attribute, not by generation ----

    [Test]
    public async Task ResolveAsync_TwoGenerationRowsOnTheSameAttribute_CannotIssueTheSameValueAsync()
    {
        // Decision 3: two different Synchronisation Rule mappings (so two different SyncRuleMappingGeneration
        // rows) can target the same attribute. A live assignment from one must still be seen as taken by the
        // other, or gate (e) scoped by generation instead of attribute would let them collide.
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generationA = UniqueValueTestHelpers.Generation();
        var generationB = UniqueValueTestHelpers.Generation();

        repo.SeedGeneratedValueAssignment(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = Guid.NewGuid(), MetaverseAttributeId = attributeId,
            Value = "joe.bloggs", NormalisedValue = "joe.bloggs", State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = generationA.Id
        });

        var server = new UniqueValueGenerationServer(repo);
        var request = UniqueValueTestHelpers.ImportRequest(generationB, attributeId, null, baseValue: "joe.bloggs");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs1"),
            "a live assignment from a DIFFERENT generation row on the SAME attribute must still be treated as taken");
    }

    [Test]
    public async Task ResolveAsync_NeverCallsGetGeneratedValueAssignmentsForGenerationAsync()
    {
        // A 100k-object run calls ResolveAsync once per object; scanning every assignment a generation has ever
        // produced on every call does not scale. Gate (e) and the adoption conflict check must use the targeted,
        // indexed lookup instead.
        var repo = new InMemorySyncRepository();
        var (countingRepo, counts) = CountingSyncRepositoryProxy.Create(repo);
        var server = new UniqueValueGenerationServer(countingRepo);
        var generation = UniqueValueTestHelpers.Generation();

        var generateRequest = UniqueValueTestHelpers.ImportRequest(generation, UniqueValueTestHelpers.NextAttributeId(), null, baseValue: "joe.bloggs");
        var adoptRequest = UniqueValueTestHelpers.ImportRequest(generation, UniqueValueTestHelpers.NextAttributeId(), null, baseValue: null, adoptableValue: "jsmith");

        await server.ResolveAsync([generateRequest, adoptRequest], UniqueValueTestHelpers.Options());

        Assert.That(counts.ContainsKey(nameof(ISyncRepository.GetGeneratedValueAssignmentsForGenerationAsync)), Is.False);
    }

    // ---- Exhaustion (FR 11) ----

    [Test]
    public async Task ResolveAsync_NoFreeCandidateWithinAttemptLimit_ReturnsExhaustedWithNamedMessageAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        SeedMvoWithValue(repo, attributeId, "joe.bloggs");
        SeedMvoWithValue(repo, attributeId, "joe.bloggs1");

        var generation = UniqueValueTestHelpers.Generation(attemptLimit: 2);
        var server = new UniqueValueGenerationServer(repo);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: "joe.bloggs");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Exhausted));
            Assert.That(outcomes[0].Value, Is.Null);
            Assert.That(outcomes[0].FailureMessage, Is.EqualTo(
                "No free value was found for Account Name after 2 attempts; the last candidate, \"joe.bloggs1\", is already held by another Metaverse Object."));
        }
    }

    // ---- Width exceeded (FR 25) ----

    [Test]
    public async Task ResolveAsync_SequenceOutgrowsFixedWidthWithStopAndReport_ReturnsWidthExceededAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedGeneratedValueSequence(new GeneratedValueSequence { MetaverseAttributeId = attributeId, NextValue = 1000000 });

        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, fixedWidth: 6);
        var server = new UniqueValueGenerationServer(repo);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Text);

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.WidthExceeded));
            Assert.That(outcomes[0].Value, Is.Null);
            Assert.That(outcomes[0].FailureMessage, Does.Contain("1000000"));
        }
    }

    // ---- Number targets ----

    [Test]
    public async Task ResolveAsync_SequenceOnNumberTarget_YieldsNumericValueOnlyAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 500);
        var server = new UniqueValueGenerationServer(repo);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Number, attributeName: "Employee Number");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Generated));
            Assert.That(outcomes[0].NumericValue, Is.EqualTo(500));
            Assert.That(outcomes[0].Value, Is.EqualTo("500"));
        }
    }

    // ---- No base value ----

    [Test]
    public async Task ResolveAsync_OnlyIfTakenWithNoBaseValue_ReturnsNoBaseValueAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = UniqueValueTestHelpers.Generation();
        var server = new UniqueValueGenerationServer(repo);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: "   ");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.NoBaseValue));
    }

    // ---- Sequence seeding and block reservation ----

    [Test]
    public async Task ResolveAsync_SequenceFirstUse_SeedsFromHighestExistingValueAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        SeedMvoWithNumber(repo, attributeId, 100999);

        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 100456, sequenceIncrement: 1);
        var server = new UniqueValueGenerationServer(repo);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].NumericValue, Is.EqualTo(101000), "the counter must seed from the highest existing value plus the increment, not the configured start");
    }

    [Test]
    public async Task ResolveAsync_SequenceAfterFirstUse_DrawsFromTheCounterNotTheHighestValueAgainAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedGeneratedValueSequence(new GeneratedValueSequence { MetaverseAttributeId = attributeId, NextValue = 50 });
        // A higher value exists on an object, but must be ignored once a counter row already exists.
        SeedMvoWithNumber(repo, attributeId, 99999);

        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1);
        var server = new UniqueValueGenerationServer(repo);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].NumericValue, Is.EqualTo(50));
    }

    [Test]
    public async Task ResolveAsync_SequenceNumberHeldByAGate_IsSkippedAndTheNextIsDrawnAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedGeneratedValueSequence(new GeneratedValueSequence { MetaverseAttributeId = attributeId, NextValue = 1 });
        SeedMvoWithNumber(repo, attributeId, 1); // takes the first number the counter would issue

        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1);
        var server = new UniqueValueGenerationServer(repo);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].NumericValue, Is.EqualTo(2), "1 was already taken, so the counter's gap is expected and the next number is issued");
    }

    [Test]
    public async Task ResolveAsync_SequenceBlockRunsOut_ReservesASecondBlockAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1);
        var server = new UniqueValueGenerationServer(repo);

        // A block size of 1 forces a fresh reservation for every single number drawn.
        var options = UniqueValueTestHelpers.Options(sequenceBlockSize: 1);

        var requestA = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);
        var requestB = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);

        var outcomeA = (await server.ResolveAsync([requestA], options))[0];
        var outcomeB = (await server.ResolveAsync([requestB], options))[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomeA.NumericValue, Is.EqualTo(1));
            Assert.That(outcomeB.NumericValue, Is.EqualTo(2), "the first block (size 1) ran out, so a second block must have been reserved");
        }
    }

    [Test]
    public async Task ResolveAsync_TwoGenerationsTargetingTheSameAttribute_ShareTheCounterAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generationA = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1);
        var generationB = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1);
        var server = new UniqueValueGenerationServer(repo);
        var options = UniqueValueTestHelpers.Options(sequenceBlockSize: 1);

        var requestA = UniqueValueTestHelpers.ImportRequest(generationA, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);
        var requestB = UniqueValueTestHelpers.ImportRequest(generationB, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);

        var outcomeA = (await server.ResolveAsync([requestA], options))[0];
        var outcomeB = (await server.ResolveAsync([requestB], options))[0];

        Assert.That(outcomeB.NumericValue, Is.EqualTo(outcomeA.NumericValue + 1), "two different flows targeting the same attribute draw from one shared counter");
    }

    // ---- Run-scoped caching across multiple ResolveAsync calls ----

    [Test]
    public async Task ResolveAsync_TwoConsecutiveCallsForOneAttribute_ReserveOnceAndYieldConsecutiveNumbersAsync()
    {
        var repo = new InMemorySyncRepository();
        var (countingRepo, counts) = CountingSyncRepositoryProxy.Create(repo);
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1);
        var server = new UniqueValueGenerationServer(countingRepo);
        var options = UniqueValueTestHelpers.Options(sequenceBlockSize: 100);

        var requestA = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);
        var requestB = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);

        var outcomeA = (await server.ResolveAsync([requestA], options))[0];
        var outcomeB = (await server.ResolveAsync([requestB], options))[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomeA.NumericValue, Is.EqualTo(1));
            Assert.That(outcomeB.NumericValue, Is.EqualTo(2));
            Assert.That(counts[nameof(ISyncRepository.ReserveGeneratedValueSequenceBlockAsync)], Is.EqualTo(1),
                "the second call must draw from the block the first call already reserved");
        }
    }

    [Test]
    public async Task ResolveAsync_PrefetchedObjects_CauseNoAssignmentQueryAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var mvoId = Guid.NewGuid();

        var (countingRepo, counts) = CountingSyncRepositoryProxy.Create(repo);
        var server = new UniqueValueGenerationServer(countingRepo);
        var options = UniqueValueTestHelpers.Options();

        await server.PrefetchAssignmentsAsync([mvoId], [], options);
        counts.Clear();

        var generation = UniqueValueTestHelpers.Generation();
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, mvoId, baseValue: "joe.bloggs");

        await server.ResolveAsync([request], options);

        Assert.That(counts.ContainsKey(nameof(ISyncRepository.GetGeneratedValueAssignmentsForMetaverseObjectsAsync)), Is.False,
            "a prefetched object's sticky check must be answered from the run-scoped cache");
    }

    [Test]
    public async Task ResolveAsync_ValueGeneratedAndCommittedInCallOne_IsStickyInCallTwoForTheSameObjectAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var mvoId = Guid.NewGuid();
        var server = new UniqueValueGenerationServer(repo);
        var options = UniqueValueTestHelpers.Options();
        var generation = UniqueValueTestHelpers.Generation();

        var firstRequest = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: "joe.bloggs");
        var firstOutcomes = await server.ResolveAsync([firstRequest], options);
        await server.CommitAssignmentsAsync(firstOutcomes, _ => mvoId, options);

        var secondRequest = UniqueValueTestHelpers.ImportRequest(generation, attributeId, mvoId, baseValue: "joe.bloggs");
        var secondOutcomes = await server.ResolveAsync([secondRequest], options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondOutcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Sticky));
            Assert.That(secondOutcomes[0].Value, Is.EqualTo("joe.bloggs"));
        }
    }

    // ---- Dry run (Sync Preview) ----

    [Test]
    public async Task ResolveAsync_DryRun_MakesNoWritesAndLeavesNoReservationsAsync()
    {
        var repo = new InMemorySyncRepository();
        var guardedRepo = new ReadOnlySyncRepositoryGuard(repo);
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var server = new UniqueValueGenerationServer(guardedRepo);
        var reservations = new UniqueValueReservationSet();
        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);

        // ReadOnlySyncRepositoryGuard throws on any write; reaching the assertions below without an exception is
        // the proof that a dry run never attempts one (in particular, never calls ReserveGeneratedValueSequenceBlockAsync).
        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options(reservations, dryRun: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Generated));
            Assert.That(outcomes[0].NumericValue, Is.EqualTo(1));
            Assert.That(reservations.IsReservedByAnotherOwner(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, attributeId, "1"), Is.False,
                "the dry run's own claim must be released before ResolveAsync returns");
        }
    }

    [Test]
    public async Task ResolveAsync_DryRunSequence_SimulatesTheBlockFromTheRealCounterAsync()
    {
        var repo = new InMemorySyncRepository();
        var guardedRepo = new ReadOnlySyncRepositoryGuard(repo);
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        SeedMvoWithNumber(repo, attributeId, 100999);

        var server = new UniqueValueGenerationServer(guardedRepo);
        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 100456);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options(dryRun: true));

        Assert.That(outcomes[0].NumericValue, Is.EqualTo(101000), "a dry run seeds its simulated counter from the same rule as a real run");
    }

    [Test]
    public async Task ResolveAsync_DryRunWithAnExistingCounter_YieldsTheCountersNextValueNotTheConfiguredStartAsync()
    {
        // A real reservation lets the database's GREATEST("NextValue", @floor) supply the counter, but a dry
        // run never reaches the database: ComputeFloorAsync must read the existing counter itself, or Sync
        // Preview would show the flow's configured start value instead of the real next number.
        var repo = new InMemorySyncRepository();
        var guardedRepo = new ReadOnlySyncRepositoryGuard(repo);
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedGeneratedValueSequence(new GeneratedValueSequence { MetaverseAttributeId = attributeId, NextValue = 5000 });

        var server = new UniqueValueGenerationServer(guardedRepo);
        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options(dryRun: true));

        Assert.That(outcomes[0].NumericValue, Is.EqualTo(5000));
    }

    [Test]
    public async Task ResolveAsync_FlowStartHigherThanAQueuedNumber_DiscardsTheRemainderAndDrawsItsOwnFloorAsync()
    {
        // The block is keyed by attribute and shared across flows (decision 3), but decision 3 also says the
        // next number is the higher of the counter and any flow's start: a flow whose start is above what is
        // left in the queue must not receive a stale, too-low queued number.
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var flowA = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 100);
        var flowB = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 5000);
        var server = new UniqueValueGenerationServer(repo);
        var options = UniqueValueTestHelpers.Options(sequenceBlockSize: 10);

        var requestA = UniqueValueTestHelpers.ImportRequest(flowA, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);
        var requestB = UniqueValueTestHelpers.ImportRequest(flowB, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);

        var outcomeA = (await server.ResolveAsync([requestA], options))[0];
        var outcomeB = (await server.ResolveAsync([requestB], options))[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomeA.NumericValue, Is.EqualTo(100));
            Assert.That(outcomeB.NumericValue, Is.EqualTo(5000),
                "flow B's start is above the queued remainder (101..109), so those numbers must be discarded as gaps, not issued to it");
        }
    }

    // ---- Helpers ----

    private static Guid SeedMvoWithValue(InMemorySyncRepository repo, int attributeId, string value)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Created = DateTime.UtcNow };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, StringValue = value });
        repo.SeedMetaverseObject(mvo);
        return mvo.Id;
    }

    private static Guid SeedMvoWithNumber(InMemorySyncRepository repo, int attributeId, long value)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Created = DateTime.UtcNow };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, LongValue = value });
        repo.SeedMetaverseObject(mvo);
        return mvo.Id;
    }
}
