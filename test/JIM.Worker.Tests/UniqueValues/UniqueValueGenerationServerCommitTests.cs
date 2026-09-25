// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Transactional;
using NUnit.Framework;
using InMemorySyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// <see cref="UniqueValueGenerationServer.CommitAssignmentsAsync"/>: filling in the now-persisted object id,
/// saving the assignment, advancing a sequence's display-only assigned count, and identifying the losers of a
/// cross-run uniqueness conflict (Unique Value Generation, #242, plan decision 13).
/// </summary>
[TestFixture]
public class UniqueValueGenerationServerCommitTests
{
    [Test]
    public async Task CommitAssignmentsAsync_GeneratedImportOutcome_FillsObjectIdAndPersistsTheAssignmentAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var server = new UniqueValueGenerationServer(repo);
        var generation = UniqueValueTestHelpers.Generation();
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: "joe.bloggs");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());
        var mvoId = Guid.NewGuid();

        var losers = await server.CommitAssignmentsAsync(outcomes, _ => mvoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(losers, Is.Empty);
            Assert.That(outcomes[0].Assignment!.MetaverseObjectId, Is.EqualTo(mvoId));

            var persisted = await repo.GetGeneratedValueAssignmentAsync(mvoId, attributeId);
            Assert.That(persisted, Is.Not.Null);
            Assert.That(persisted!.Value, Is.EqualTo("joe.bloggs"));
        }
    }

    [Test]
    public async Task CommitAssignmentsAsync_AdoptedExportOutcome_FillsConnectedSystemObjectIdAndPersistsAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var server = new UniqueValueGenerationServer(repo);
        var generation = UniqueValueTestHelpers.Generation();
        var request = UniqueValueTestHelpers.ExportRequest(generation, attributeId, null, baseValue: null, adoptableValue: "jsmith");

        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());
        var csoId = Guid.NewGuid();

        var losers = await server.CommitAssignmentsAsync(outcomes, _ => csoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(losers, Is.Empty);
            var persisted = await repo.GetGeneratedValueAssignmentForConnectedSystemObjectAsync(csoId, attributeId);
            Assert.That(persisted, Is.Not.Null);
            Assert.That(persisted!.Value, Is.EqualTo("jsmith"));
            Assert.That(persisted.Adopted, Is.True);
        }
    }

    [Test]
    public async Task CommitAssignmentsAsync_NonGeneratedOrAdoptedOutcomes_AreIgnoredAsync()
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
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, mvoId, baseValue: "joe.bloggs");
        var outcomes = await server.ResolveAsync([request], UniqueValueTestHelpers.Options());

        Assume.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Sticky));

        var losers = await server.CommitAssignmentsAsync(outcomes, _ => throw new InvalidOperationException("must not be called for a Sticky outcome"));

        Assert.That(losers, Is.Empty);
    }

    [Test]
    public async Task CommitAssignmentsAsync_SequenceOutcome_IncrementsTheAssignedCountOnceAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var server = new UniqueValueGenerationServer(repo);
        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1);
        var requestA = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);
        var requestB = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);

        var options = UniqueValueTestHelpers.Options();
        var outcomes = await server.ResolveAsync([requestA, requestB], options);

        await server.CommitAssignmentsAsync(outcomes, _ => Guid.NewGuid());

        var sequence = await repo.GetGeneratedValueSequenceAsync(attributeId, null);
        Assert.That(sequence!.AssignedCount, Is.EqualTo(2));
    }

    [Test]
    public async Task CommitAssignmentsAsync_BatchConflict_ReturnsTheLoserAndSavesTheOtherAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = UniqueValueTestHelpers.Generation();

        // Seeded directly (bypassing conflict detection) to simulate a value another, concurrent run committed
        // between this run's gates clearing and this commit call.
        repo.SeedGeneratedValueAssignment(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = Guid.NewGuid(), MetaverseAttributeId = attributeId,
            Value = "joe.bloggs1", NormalisedValue = "joe.bloggs1", State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = generation.Id
        });

        var winnerOutcome = new GenerationOutcome(
            UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: "winner"),
            GenerationOutcomeKind.Generated, "winner", null,
            BuildUnsavedAssignment(generation, attributeId, "winner"), null);

        var loserOutcome = new GenerationOutcome(
            UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: "joe.bloggs1"),
            GenerationOutcomeKind.Generated, "joe.bloggs1", null,
            BuildUnsavedAssignment(generation, attributeId, "joe.bloggs1"), null);

        var server = new UniqueValueGenerationServer(repo);
        var losers = await server.CommitAssignmentsAsync([winnerOutcome, loserOutcome], _ => Guid.NewGuid());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(losers, Has.Count.EqualTo(1));
            Assert.That(losers[0], Is.SameAs(loserOutcome));

            var savedWinner = await repo.GetGeneratedValueAssignmentAsync(winnerOutcome.Assignment!.MetaverseObjectId!.Value, attributeId);
            Assert.That(savedWinner, Is.Not.Null, "the non-conflicting outcome in the same batch must still be saved");
        }
    }

    /// <summary>
    /// Work package E fix #2 follow-up: a batch of more than two outcomes, with the loser in the middle, must
    /// report exactly that one loser and save every other outcome either side of it.
    /// </summary>
    [Test]
    public async Task CommitAssignmentsAsync_ThreeItemBatchWithOneLoserInTheMiddle_ReportsExactlyOneLoserAndSavesTheOtherTwoAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = UniqueValueTestHelpers.Generation();

        repo.SeedGeneratedValueAssignment(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = Guid.NewGuid(), MetaverseAttributeId = attributeId,
            Value = "joe.bloggs1", NormalisedValue = "joe.bloggs1", State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = generation.Id
        });

        var winnerA = new GenerationOutcome(
            UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: "alpha"),
            GenerationOutcomeKind.Generated, "alpha", null,
            BuildUnsavedAssignment(generation, attributeId, "alpha"), null);
        var loser = new GenerationOutcome(
            UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: "joe.bloggs1"),
            GenerationOutcomeKind.Generated, "joe.bloggs1", null,
            BuildUnsavedAssignment(generation, attributeId, "joe.bloggs1"), null);
        var winnerB = new GenerationOutcome(
            UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: "beta"),
            GenerationOutcomeKind.Generated, "beta", null,
            BuildUnsavedAssignment(generation, attributeId, "beta"), null);

        var server = new UniqueValueGenerationServer(repo);
        var losers = await server.CommitAssignmentsAsync([winnerA, loser, winnerB], _ => Guid.NewGuid());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(losers, Has.Count.EqualTo(1));
            Assert.That(losers[0], Is.SameAs(loser));

            var savedA = await repo.GetGeneratedValueAssignmentAsync(winnerA.Assignment!.MetaverseObjectId!.Value, attributeId);
            var savedB = await repo.GetGeneratedValueAssignmentAsync(winnerB.Assignment!.MetaverseObjectId!.Value, attributeId);
            Assert.That(savedA, Is.Not.Null, "the winner before the loser in the batch must still be saved");
            Assert.That(savedB, Is.Not.Null, "the winner after the loser in the batch must still be saved");
        }
    }

    [Test]
    public async Task CommitAssignmentsAsync_WithOptions_UpdatesTheRunScopedCacheAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var server = new UniqueValueGenerationServer(repo);
        var generation = UniqueValueTestHelpers.Generation();
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: "joe.bloggs");
        var options = UniqueValueTestHelpers.Options();

        var outcomes = await server.ResolveAsync([request], options);
        var mvoId = Guid.NewGuid();
        await server.CommitAssignmentsAsync(outcomes, _ => mvoId, options);

        Assert.That(options.KnownMetaverseAssignments.TryGetValue(mvoId, out var bag), Is.True);
        Assert.That(bag!.Select(a => a.Value), Does.Contain("joe.bloggs"));
    }

    private static GeneratedValueAssignment BuildUnsavedAssignment(SyncRuleMappingGeneration generation, int attributeId, string value) => new()
    {
        Id = Guid.NewGuid(),
        MetaverseAttributeId = attributeId,
        Value = value,
        NormalisedValue = value.ToLowerInvariant(),
        State = GeneratedValueAssignmentState.Proposed,
        SyncRuleMappingGenerationId = generation.Id
    };
}
