// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Transactional;
using NUnit.Framework;
using InMemorySyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// The retired values register's part of <see cref="UniqueValueGenerationServer"/> (Unique Value Generation, #242,
/// Phase 6; plan decision 4): the retired gate, which treats a retired value as taken for a flow that never reuses
/// values, its place in the gate order, retiring on supersession, and "Start again" forgetting the register.
/// </summary>
[TestFixture]
public class UniqueValueGenerationServerRetiredRegisterTests
{
    private static RetiredGeneratedValue Retired(int metaverseAttributeId, string value) => new()
    {
        MetaverseAttributeId = metaverseAttributeId,
        Value = value,
        NormalisedValue = value.ToLowerInvariant(),
        RetiredAt = DateTime.UtcNow,
        Reason = RetiredGeneratedValueReason.ObjectDeleted
    };

    // ---- The retired gate ----

    [Test]
    public async Task ResolveAsync_BaseValueRetiredAndNeverReuseOn_GeneratesTheNextCandidateAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedRetiredGeneratedValue(Retired(attributeId, "joe.bloggs"));

        var server = new UniqueValueGenerationServer(repo);
        var generation = UniqueValueTestHelpers.Generation(neverReuse: true);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, Guid.NewGuid(), baseValue: "joe.bloggs");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Generated));
            Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs1"), "a leaver's retired value is never issued to a new joiner");
        }
    }

    [Test]
    public async Task ResolveAsync_RetiredValueDiffersOnlyInCase_IsStillTakenAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedRetiredGeneratedValue(Retired(attributeId, "Joe.Bloggs"));

        var server = new UniqueValueGenerationServer(repo);
        var request = UniqueValueTestHelpers.ImportRequest(UniqueValueTestHelpers.Generation(), attributeId, Guid.NewGuid(), baseValue: "joe.bloggs");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs1"));
    }

    [Test]
    public async Task ResolveAsync_BaseValueRetiredButNeverReuseOff_IssuesTheRetiredValueAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedRetiredGeneratedValue(Retired(attributeId, "joe.bloggs"));
        var (countingRepo, counts) = CountingSyncRepositoryProxy.Create(repo);

        var server = new UniqueValueGenerationServer(countingRepo);
        var generation = UniqueValueTestHelpers.Generation(neverReuse: false);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, Guid.NewGuid(), baseValue: "joe.bloggs");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs"), "with Never reuse off, a rehire may receive the previous value");
            Assert.That(counts.ContainsKey(nameof(ISyncRepository.GetRetiredGeneratedValuesInUseAsync)), Is.False,
                "a flow that reuses values never consults the register");
        }
    }

    [Test]
    public async Task ResolveAsync_SequenceNumberRetired_IsSkippedEvenWithNeverReuseOffAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedRetiredGeneratedValue(Retired(attributeId, "E1"));

        var server = new UniqueValueGenerationServer(repo);
        // A Sequence always never reuses, whatever the stored switch says.
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.Sequence, sequenceStart: 1, neverReuse: false);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, Guid.NewGuid(), baseValue: "E");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].Value, Is.EqualTo("E2"));
    }

    [Test]
    public async Task ResolveAsync_RetiredOnAnotherAttribute_DoesNotBlockAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedRetiredGeneratedValue(Retired(UniqueValueTestHelpers.NextAttributeId(), "joe.bloggs"));

        var server = new UniqueValueGenerationServer(repo);
        var request = UniqueValueTestHelpers.ImportRequest(UniqueValueTestHelpers.Generation(), attributeId, Guid.NewGuid(), baseValue: "joe.bloggs");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs"), "the register is kept per attribute");
    }

    [Test]
    public async Task ResolveAsync_ExportModeValueRetired_GeneratesTheNextCandidateAsync()
    {
        var repo = new InMemorySyncRepository();
        var csAttributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedRetiredGeneratedValue(new RetiredGeneratedValue
        {
            ConnectedSystemObjectTypeAttributeId = csAttributeId,
            Value = "joe.bloggs",
            NormalisedValue = "joe.bloggs",
            RetiredAt = DateTime.UtcNow,
            Reason = RetiredGeneratedValueReason.ObjectDeleted
        });

        var server = new UniqueValueGenerationServer(repo);
        var request = UniqueValueTestHelpers.ExportRequest(UniqueValueTestHelpers.Generation(), csAttributeId, Guid.NewGuid(), baseValue: "joe.bloggs");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs1"));
    }

    // ---- Gate order ----

    [Test]
    public async Task ResolveAsync_CandidateRetired_NeverQueriesTheMetaverseGateForItAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedRetiredGeneratedValue(Retired(attributeId, "joe.bloggs"));
        var (countingRepo, counts) = CountingSyncRepositoryProxy.Create(repo);

        var server = new UniqueValueGenerationServer(countingRepo);
        var generation = UniqueValueTestHelpers.Generation(attemptLimit: 1);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, Guid.NewGuid(), baseValue: "joe.bloggs");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Exhausted));
            Assert.That(outcomes[0].FailureMessage, Does.Contain("retired"));
            Assert.That(counts.GetValueOrDefault(nameof(ISyncRepository.GetRetiredGeneratedValuesInUseAsync)), Is.EqualTo(1));
            Assert.That(counts.ContainsKey(nameof(ISyncRepository.GetMetaverseAttributeValuesInUseAsync)), Is.False,
                "the retired gate runs before the Metaverse gate, and a rejected candidate goes no further");
        }
    }

    [Test]
    public async Task ResolveAsync_CandidateTakenAtReservationGate_NeverQueriesTheRetiredGateAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var (countingRepo, counts) = CountingSyncRepositoryProxy.Create(repo);
        var reservations = new UniqueValueReservationSet();
        reservations.TryReserve(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, attributeId, "joe.bloggs");

        var server = new UniqueValueGenerationServer(countingRepo);
        var request = UniqueValueTestHelpers.ImportRequest(UniqueValueTestHelpers.Generation(attemptLimit: 1), attributeId, null, baseValue: "joe.bloggs");

        await server.ResolveAsync([request], UniqueValueTestHelpers.Options(reservations));

        Assert.That(counts.ContainsKey(nameof(ISyncRepository.GetRetiredGeneratedValuesInUseAsync)), Is.False,
            "the reservation gate runs first");
    }

    [Test]
    public async Task ResolveAsync_ManyRequestsForOneAttribute_QueryTheRegisterOncePerRoundAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var (countingRepo, counts) = CountingSyncRepositoryProxy.Create(repo);

        var server = new UniqueValueGenerationServer(countingRepo);
        var generation = UniqueValueTestHelpers.Generation();
        var requests = new[] { "ann.ash", "bob.birch", "cat.cole" }
            .Select(v => UniqueValueTestHelpers.ImportRequest(generation, attributeId, Guid.NewGuid(), baseValue: v))
            .ToList();

        var outcomes = await server.ResolveAsync(requests, UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes.All(o => o.Kind == GenerationOutcomeKind.Generated), Is.True);
            Assert.That(counts.GetValueOrDefault(nameof(ISyncRepository.GetRetiredGeneratedValuesInUseAsync)), Is.EqualTo(1),
                "batched per attribute, never per object");
        }
    }

    // ---- Retiring on supersession ----

    [Test]
    public async Task RetireAndDeleteAssignmentsAsync_NeverReuseOn_RetiresDeletesAndEvictsAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = UniqueValueTestHelpers.Generation(neverReuse: true);
        repo.SeedGeneration(generation);
        var mvoId = Guid.NewGuid();
        var assignment = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = mvoId, MetaverseAttributeId = attributeId, Value = "Joe.Bloggs",
            NormalisedValue = "joe.bloggs", State = GeneratedValueAssignmentState.Committed, SyncRuleMappingGenerationId = generation.Id
        };
        repo.SeedGeneratedValueAssignment(assignment);

        var server = new UniqueValueGenerationServer(repo);
        var options = UniqueValueTestHelpers.Options();
        await server.PrefetchAssignmentsAsync([mvoId], [], options);
        var activityId = Guid.NewGuid();

        var retired = await server.RetireAndDeleteAssignmentsAsync([assignment.Id], RetiredGeneratedValueReason.Superseded, activityId, options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retired.Select(r => r.Value), Is.EqualTo(new[] { "Joe.Bloggs" }));
            Assert.That(repo.GeneratedValueAssignments.ContainsKey(assignment.Id), Is.False);
            Assert.That(options.GetKnownMetaverseAssignments(mvoId), Is.Empty, "the run cache must not offer a deleted assignment as sticky");
            var entry = repo.RetiredGeneratedValues.Single();
            Assert.That(entry.Reason, Is.EqualTo(RetiredGeneratedValueReason.Superseded));
            Assert.That(entry.ActivityId, Is.EqualTo(activityId));
            Assert.That(entry.FromObjectId, Is.EqualTo(mvoId));
            Assert.That(entry.NormalisedValue, Is.EqualTo("joe.bloggs"));
        }
    }

    [Test]
    public async Task RetireAndDeleteAssignmentsAsync_NeverReuseOff_DeletesWithoutRetiringAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = UniqueValueTestHelpers.Generation(neverReuse: false);
        repo.SeedGeneration(generation);
        var assignment = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = Guid.NewGuid(), MetaverseAttributeId = attributeId, Value = "joe.bloggs",
            NormalisedValue = "joe.bloggs", State = GeneratedValueAssignmentState.Committed, SyncRuleMappingGenerationId = generation.Id
        };
        repo.SeedGeneratedValueAssignment(assignment);

        var server = new UniqueValueGenerationServer(repo);

        var retired = await server.RetireAndDeleteAssignmentsAsync([assignment.Id], RetiredGeneratedValueReason.Superseded, null, UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retired, Is.Empty);
            Assert.That(repo.RetiredGeneratedValues, Is.Empty, "nothing is written to the register for a flow that reuses values");
            Assert.That(repo.GeneratedValueAssignments.ContainsKey(assignment.Id), Is.False);
        }
    }

    [Test]
    public async Task RetireAndDeleteAssignmentsAsync_ValueAlreadyRetired_LeavesTheEntryAndReportsNothingAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = UniqueValueTestHelpers.Generation();
        repo.SeedGeneration(generation);
        repo.SeedRetiredGeneratedValue(Retired(attributeId, "joe.bloggs"));
        var assignment = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = Guid.NewGuid(), MetaverseAttributeId = attributeId, Value = "joe.bloggs",
            NormalisedValue = "joe.bloggs", State = GeneratedValueAssignmentState.Committed, SyncRuleMappingGenerationId = generation.Id
        };
        repo.SeedGeneratedValueAssignment(assignment);

        var server = new UniqueValueGenerationServer(repo);

        var retired = await server.RetireAndDeleteAssignmentsAsync([assignment.Id], RetiredGeneratedValueReason.Superseded, null, UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retired, Is.Empty);
            Assert.That(repo.RetiredGeneratedValues, Has.Count.EqualTo(1));
            Assert.That(repo.RetiredGeneratedValues[0].Reason, Is.EqualTo(RetiredGeneratedValueReason.ObjectDeleted), "the first retirement stands");
        }
    }

    // ---- Start again ----

    [Test]
    public async Task RestartAsync_Sequence_ForgetsTheAttributesRetiredValuesAndReportsHowManyAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var otherAttributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedRetiredGeneratedValue(Retired(attributeId, "100456"));
        repo.SeedRetiredGeneratedValue(Retired(attributeId, "100457"));
        repo.SeedRetiredGeneratedValue(Retired(otherAttributeId, "100456"));

        var server = new UniqueValueGenerationServer(repo);
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.Sequence, sequenceStart: 100456);
        var mapping = new SyncRuleMapping
        {
            Id = 7,
            TargetMetaverseAttributeId = attributeId,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = attributeId, Name = "Employee Number", Type = AttributeDataType.Text },
            Generation = generation
        };

        var result = await server.RestartAsync(mapping);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RetiredValuesForgotten, Is.EqualTo(2));
            Assert.That(repo.RetiredGeneratedValues.Select(r => r.MetaverseAttributeId), Is.EqualTo(new int?[] { otherAttributeId }),
                "only this attribute's register is forgotten");
        }
    }

    [Test]
    public async Task RestartAsync_NotASequence_LeavesTheRegisterAloneAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedRetiredGeneratedValue(Retired(attributeId, "joe.bloggs"));

        var server = new UniqueValueGenerationServer(repo);
        var mapping = new SyncRuleMapping
        {
            Id = 8,
            TargetMetaverseAttributeId = attributeId,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = attributeId, Name = "Account Name", Type = AttributeDataType.Text },
            Generation = UniqueValueTestHelpers.Generation()
        };

        var result = await server.RestartAsync(mapping);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RetiredValuesForgotten, Is.Zero);
            Assert.That(repo.RetiredGeneratedValues, Has.Count.EqualTo(1), "Start again is offered only for Sequence flows, and only it purges");
        }
    }

    [Test]
    public async Task GetRetiredValueCountAsync_ReturnsTheTargetAttributesCountAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedRetiredGeneratedValue(Retired(attributeId, "a"));
        repo.SeedRetiredGeneratedValue(Retired(attributeId, "b"));
        repo.SeedRetiredGeneratedValue(Retired(UniqueValueTestHelpers.NextAttributeId(), "c"));

        var server = new UniqueValueGenerationServer(repo);
        var mapping = new SyncRuleMapping { Id = 9, TargetMetaverseAttributeId = attributeId, Generation = UniqueValueTestHelpers.Generation() };

        Assert.That(await server.GetRetiredValueCountAsync(mapping), Is.EqualTo(2));
    }
}
