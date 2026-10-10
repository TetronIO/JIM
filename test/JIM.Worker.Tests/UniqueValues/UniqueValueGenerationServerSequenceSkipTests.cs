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
/// A Sequence whose counter sits below a run of numbers already held (#2031): the run is skipped in one step, so
/// its length never costs attempts, and the attempt limit bounds only genuine collisions. The run typically follows
/// Start again over a population that still holds its numbers, which is the action's own documented use.
/// </summary>
[TestFixture]
public class UniqueValueGenerationServerSequenceSkipTests
{
    [Test]
    public async Task ResolveAsync_CounterBelowAHeldRunLongerThanTheAttemptLimit_IssuesTheFirstFreeNumberAboveItAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedGeneratedValueSequence(new GeneratedValueSequence { MetaverseAttributeId = attributeId, NextValue = 1000 });
        for (var n = 1000; n < 1050; n++)
            SeedMvoWithValue(repo, attributeId, $"EMP-{n:000000}");

        var generation = UniqueValueTestHelpers.Generation(
            tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1, fixedWidth: 6, separator: "-", attemptLimit: 10);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: "EMP", attributeName: "Staff Number");

        var outcomes = await new UniqueValueGenerationServer(repo).ResolveAsync([request], UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Generated), outcomes[0].FailureMessage);
            Assert.That(outcomes[0].Value, Is.EqualTo("EMP-001050"), "the first number above the 50 held ones, although the attempt limit is 10");
        }
    }

    [Test]
    public async Task ResolveAsync_HeldRun_IsSkippedWithOneLookupAsync()
    {
        var repo = new InMemorySyncRepository();
        var (countingRepo, counts) = CountingSyncRepositoryProxy.Create(repo);
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedGeneratedValueSequence(new GeneratedValueSequence { MetaverseAttributeId = attributeId, NextValue = 1 });
        for (var n = 1; n <= 500; n++)
            SeedMvoWithNumber(repo, attributeId, n);

        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);

        var outcomes = await new UniqueValueGenerationServer(countingRepo).ResolveAsync([request], UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].NumericValue, Is.EqualTo(501));
            Assert.That(counts[nameof(ISyncRepository.GetSequenceHeldRunAsync)], Is.EqualTo(1),
                "one lookup finds the end of the run, however long it is");
            Assert.That(counts[nameof(ISyncRepository.GetMetaverseAttributeNumbersInUseAsync)], Is.EqualTo(2),
                "the value gate checks the rejected first candidate and the one above the run; nothing in between");
        }
    }

    [Test]
    public async Task ResolveAsync_HeldRunInsideTheReservedBlock_DrawsTheRestOfThatBlockAsync()
    {
        var repo = new InMemorySyncRepository();
        var (countingRepo, counts) = CountingSyncRepositoryProxy.Create(repo);
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedGeneratedValueSequence(new GeneratedValueSequence { MetaverseAttributeId = attributeId, NextValue = 1 });
        for (var n = 1; n <= 10; n++)
            SeedMvoWithNumber(repo, attributeId, n);

        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1, attemptLimit: 3);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);

        var outcomes = await new UniqueValueGenerationServer(countingRepo).ResolveAsync([request], UniqueValueTestHelpers.Options(sequenceBlockSize: 100));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].NumericValue, Is.EqualTo(11));
            Assert.That(counts[nameof(ISyncRepository.ReserveGeneratedValueSequenceBlockAsync)], Is.EqualTo(1),
                "11 is in the block already reserved (1 to 100), so skipping to it must not throw that block away");
        }
    }

    [Test]
    public async Task ResolveAsync_HeldRunStraddlingABlockBoundary_ContinuesAboveItAcrossCallsAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedGeneratedValueSequence(new GeneratedValueSequence { MetaverseAttributeId = attributeId, NextValue = 1 });
        for (var n = 1; n <= 34; n++)
            SeedMvoWithNumber(repo, attributeId, n);

        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1, attemptLimit: 5);
        var server = new UniqueValueGenerationServer(repo);
        var options = UniqueValueTestHelpers.Options(sequenceBlockSize: 10);

        var first = (await server.ResolveAsync([NumberRequest(generation, attributeId)], options))[0];
        var second = (await server.ResolveAsync([NumberRequest(generation, attributeId)], options))[0];
        var counter = await repo.GetGeneratedValueSequenceAsync(attributeId, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.NumericValue, Is.EqualTo(35), "the run (1 to 34) crosses three blocks of 10; the number above it is issued");
            Assert.That(second.NumericValue, Is.EqualTo(36), "a later page carries on above the run, never back inside it");
            Assert.That(counter!.NextValue, Is.GreaterThan(36), "the counter moved forward past the run, never backwards");
        }
    }

    [Test]
    public async Task ResolveAsync_SeveralObjectsInOneCallReachingAHeldRun_EachGetADistinctNumberAboveItAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedGeneratedValueSequence(new GeneratedValueSequence { MetaverseAttributeId = attributeId, NextValue = 1 });
        for (var n = 1; n <= 20; n++)
            SeedMvoWithNumber(repo, attributeId, n);

        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1, attemptLimit: 5);
        var requests = Enumerable.Range(0, 3).Select(_ => NumberRequest(generation, attributeId)).ToList();

        var outcomes = await new UniqueValueGenerationServer(repo).ResolveAsync(requests, UniqueValueTestHelpers.Options(sequenceBlockSize: 10));

        Assert.That(outcomes.Select(o => o.NumericValue), Is.EqualTo(new long?[] { 21, 22, 23 }));
    }

    [Test]
    public async Task ResolveAsync_HeldRunHoldingTheObjectsOwnAccountsNumber_StopsThereRatherThanSkippingItAsync()
    {
        // The connector-space gate lets an object have the number its own account already holds, so skipping the
        // run must not jump over that number: doing so would rename the account on the next export.
        var repo = new InMemorySyncRepository();
        var mvAttributeId = UniqueValueTestHelpers.NextAttributeId();
        var csAttributeId = UniqueValueTestHelpers.NextAttributeId();
        var mvoId = Guid.NewGuid();
        repo.SeedGeneratedValueSequence(new GeneratedValueSequence { MetaverseAttributeId = mvAttributeId, NextValue = 10 });
        for (var n = 10; n <= 19; n++)
            repo.SeedConnectedSystemObject(ConnectedSystemObjectHoldingNumber(csAttributeId, n, n == 15 ? mvoId : Guid.NewGuid()));

        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1, attemptLimit: 3);
        var request = UniqueValueTestHelpers.ImportRequest(generation, mvAttributeId, mvoId, baseValue: null,
            targetType: AttributeDataType.Number, connectorSpaceAttributeIds: [csAttributeId]);

        var outcomes = await new UniqueValueGenerationServer(repo).ResolveAsync([request], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].NumericValue, Is.EqualTo(15), outcomes[0].FailureMessage);
    }

    [Test]
    public async Task ResolveAsync_HeldRunMadeOfOtherAssignmentsAndRetiredValues_IsSkippedAsAWholeAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedGeneratedValueSequence(new GeneratedValueSequence { MetaverseAttributeId = attributeId, NextValue = 1 });
        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1, attemptLimit: 3);
        for (var n = 1; n <= 5; n++)
        {
            repo.SeedGeneratedValueAssignment(new GeneratedValueAssignment
            {
                Id = Guid.NewGuid(), MetaverseObjectId = Guid.NewGuid(), MetaverseAttributeId = attributeId,
                SyncRuleMappingGenerationId = generation.Id, Value = $"{n}", NormalisedValue = $"{n}", State = GeneratedValueAssignmentState.Committed
            });
        }
        for (var n = 6; n <= 10; n++)
        {
            repo.SeedRetiredGeneratedValue(new RetiredGeneratedValue
            {
                MetaverseAttributeId = attributeId, Value = $"{n}", NormalisedValue = $"{n}",
                RetiredAt = DateTime.UtcNow, Reason = RetiredGeneratedValueReason.ObjectDeleted
            });
        }

        var outcomes = await new UniqueValueGenerationServer(repo).ResolveAsync([NumberRequest(generation, attributeId)], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].NumericValue, Is.EqualTo(11), outcomes[0].FailureMessage);
    }

    [Test]
    public async Task ResolveAsync_ExportModeCounterBelowAHeldRun_IssuesTheFirstFreeNumberAboveItAsync()
    {
        var repo = new InMemorySyncRepository();
        var csAttributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedGeneratedValueSequence(new GeneratedValueSequence { ConnectedSystemObjectTypeAttributeId = csAttributeId, NextValue = 100 });
        for (var n = 100; n <= 120; n++)
            repo.SeedConnectedSystemObject(ConnectedSystemObjectHoldingText(csAttributeId, $"u{n}", null));

        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1, attemptLimit: 5);
        var request = UniqueValueTestHelpers.ExportRequest(generation, csAttributeId, Guid.NewGuid(), baseValue: "u");

        var outcomes = await new UniqueValueGenerationServer(repo).ResolveAsync([request], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].Value, Is.EqualTo("u121"), outcomes[0].FailureMessage);
    }

    [Test]
    public async Task ResolveAsync_IncrementAboveOne_SkipsOnlyTheNumbersInItsOwnStepAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedGeneratedValueSequence(new GeneratedValueSequence { MetaverseAttributeId = attributeId, NextValue = 10 });
        foreach (var n in new long[] { 10, 20, 30, 35, 41 })
            SeedMvoWithNumber(repo, attributeId, n);

        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 10, sequenceIncrement: 10, attemptLimit: 2);

        var outcomes = await new UniqueValueGenerationServer(repo).ResolveAsync([NumberRequest(generation, attributeId)], UniqueValueTestHelpers.Options());

        Assert.That(outcomes[0].NumericValue, Is.EqualTo(40), "35 and 41 are not numbers this sequence would issue, so they do not extend the run");
    }

    [Test]
    public async Task ResolveAsync_DryRunCounterBelowAHeldRun_PreviewsTheFirstFreeNumberAboveItAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedGeneratedValueSequence(new GeneratedValueSequence { MetaverseAttributeId = attributeId, NextValue = 1 });
        for (var n = 1; n <= 30; n++)
            SeedMvoWithNumber(repo, attributeId, n);

        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: 1, attemptLimit: 5);

        var outcomes = await new UniqueValueGenerationServer(new ReadOnlySyncRepositoryGuard(repo))
            .ResolveAsync([NumberRequest(generation, attributeId)], UniqueValueTestHelpers.Options(dryRun: true));

        Assert.That(outcomes[0].NumericValue, Is.EqualTo(31), "Sync Preview shows what synchronisation would issue");
    }

    // ---- Helpers ----

    private static GenerationRequest NumberRequest(SyncRuleMappingGeneration generation, int attributeId) =>
        UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);

    private static void SeedMvoWithValue(InMemorySyncRepository repo, int attributeId, string value)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Created = DateTime.UtcNow };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, StringValue = value });
        repo.SeedMetaverseObject(mvo);
    }

    private static void SeedMvoWithNumber(InMemorySyncRepository repo, int attributeId, long value)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Created = DateTime.UtcNow };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, LongValue = value });
        repo.SeedMetaverseObject(mvo);
    }

    private static ConnectedSystemObject ConnectedSystemObjectHoldingNumber(int attributeId, long value, Guid? metaverseObjectId)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), MetaverseObjectId = metaverseObjectId };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, LongValue = value, ConnectedSystemObject = cso });
        return cso;
    }

    private static ConnectedSystemObject ConnectedSystemObjectHoldingText(int attributeId, string value, Guid? metaverseObjectId)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), MetaverseObjectId = metaverseObjectId };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, StringValue = value, ConnectedSystemObject = cso });
        return cso;
    }
}
