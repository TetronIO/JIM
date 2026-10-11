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
/// <see cref="UniqueValueGenerationServer.ReturnUnusedSequenceNumbersAsync"/> (#2044): a run hands back the sequence
/// numbers it reserved and never drew, so runs that each issue a few numbers issue consecutive numbers, and it never
/// hands back a number another run, or an administrator's move of the counter, has since claimed.
/// </summary>
[TestFixture]
public class UniqueValueGenerationServerSequenceHandBackTests
{
    [Test]
    public async Task ReturnUnusedSequenceNumbersAsync_RunsThatEachIssueOneNumber_IssueConsecutiveNumbersAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = SequenceFrom(1);
        var server = new UniqueValueGenerationServer(repo);

        var numbers = new List<long?>();
        for (var run = 0; run < 3; run++)
        {
            var options = UniqueValueTestHelpers.Options(sequenceBlockSize: 100);
            numbers.Add((await server.ResolveAsync([NumberRequest(generation, attributeId)], options))[0].NumericValue);
            await server.ReturnUnusedSequenceNumbersAsync(options);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(numbers, Is.EqualTo(new long?[] { 1, 2, 3 }), "each run's block of 100 is handed back past the one number it drew");
            Assert.That((await repo.GetGeneratedValueSequenceAsync(attributeId, null))!.NextValue, Is.EqualTo(4));
        }
    }

    [Test]
    public async Task ReturnUnusedSequenceNumbersAsync_ReportsHowManyNumbersWereHandedBackAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var server = new UniqueValueGenerationServer(repo);
        var options = UniqueValueTestHelpers.Options(sequenceBlockSize: 10);

        await server.ResolveAsync([NumberRequest(SequenceFrom(1), attributeId), NumberRequest(SequenceFrom(1), attributeId)], options);

        Assert.That(await server.ReturnUnusedSequenceNumbersAsync(options), Is.EqualTo(8), "2 of the block of 10 were drawn");
    }

    [Test]
    public async Task ReturnUnusedSequenceNumbersAsync_AnotherRunReservedAfterThisOne_LeavesItsNumbersAloneAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = SequenceFrom(1);
        var server = new UniqueValueGenerationServer(repo);
        var runA = UniqueValueTestHelpers.Options(sequenceBlockSize: 100);
        var runB = UniqueValueTestHelpers.Options(sequenceBlockSize: 100);

        await server.ResolveAsync([NumberRequest(generation, attributeId)], runA); // draws 1, holds 2 to 100
        await server.ResolveAsync([NumberRequest(generation, attributeId)], runB); // draws 101, holds 102 to 200

        var returnedByA = await server.ReturnUnusedSequenceNumbersAsync(runA);
        var counterAfterA = (await repo.GetGeneratedValueSequenceAsync(attributeId, null))!.NextValue;
        var returnedByB = await server.ReturnUnusedSequenceNumbersAsync(runB);
        var counterAfterB = (await repo.GetGeneratedValueSequenceAsync(attributeId, null))!.NextValue;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(returnedByA, Is.Zero, "run B reserved after run A, so A's tail lies below numbers B may still issue; it stays a gap");
            Assert.That(counterAfterA, Is.EqualTo(201));
            Assert.That(returnedByB, Is.EqualTo(99));
            Assert.That(counterAfterB, Is.EqualTo(102), "run B was the last to reserve, so its own tail comes back");
        }
    }

    [Test]
    public async Task ReturnUnusedSequenceNumbersAsync_StartAgainWhileTheRunWasDrawing_IsNotUndoneEvenWhereTheCounterEndsUpTheSameAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = SequenceFrom(1);
        var server = new UniqueValueGenerationServer(repo);
        var run = UniqueValueTestHelpers.Options(sequenceBlockSize: 100);

        await server.ResolveAsync([NumberRequest(generation, attributeId)], run); // block 1 to 100, counter at 101
        await repo.ResetGeneratedValueSequenceAsync(attributeId, null, newValue: 1, syncRuleMappingId: 1); // Start again
        await repo.ReserveGeneratedValueSequenceBlockAsync(attributeId, null, floor: 1, count: 100, increment: 1); // another run: counter at 101 again

        var returned = await server.ReturnUnusedSequenceNumbersAsync(run);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(returned, Is.Zero, "the counter is back where this run left it, but only because Start again moved it and another run reserved");
            Assert.That((await repo.GetGeneratedValueSequenceAsync(attributeId, null))!.NextValue, Is.EqualTo(101));
        }
    }

    [Test]
    public async Task ReturnUnusedSequenceNumbersAsync_AfterHandingBack_DrawsNothingFromTheHandedBackNumbersAsync()
    {
        var repo = new InMemorySyncRepository();
        var (countingRepo, counts) = CountingSyncRepositoryProxy.Create(repo);
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = SequenceFrom(1);
        var server = new UniqueValueGenerationServer(countingRepo);
        var options = UniqueValueTestHelpers.Options(sequenceBlockSize: 100);

        await server.ResolveAsync([NumberRequest(generation, attributeId)], options);
        await server.ReturnUnusedSequenceNumbersAsync(options);
        var afterHandBack = (await server.ResolveAsync([NumberRequest(generation, attributeId)], options))[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterHandBack.NumericValue, Is.EqualTo(2));
            Assert.That(counts[nameof(ISyncRepository.ReserveGeneratedValueSequenceBlockAsync)], Is.EqualTo(2),
                "the handed-back numbers belong to the counter again, so the next draw reserves them afresh rather than reusing the run's queue");
        }
    }

    [Test]
    public async Task ReturnUnusedSequenceNumbersAsync_BlockFullyDrawn_HandsNothingBackAsync()
    {
        var repo = new InMemorySyncRepository();
        var (countingRepo, counts) = CountingSyncRepositoryProxy.Create(repo);
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var server = new UniqueValueGenerationServer(countingRepo);
        var options = UniqueValueTestHelpers.Options(sequenceBlockSize: 1);

        await server.ResolveAsync([NumberRequest(SequenceFrom(1), attributeId)], options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await server.ReturnUnusedSequenceNumbersAsync(options), Is.Zero);
            Assert.That(counts.ContainsKey(nameof(ISyncRepository.ReturnUnusedGeneratedValueSequenceNumbersAsync)), Is.False);
        }
    }

    [Test]
    public async Task ReturnUnusedSequenceNumbersAsync_DryRun_HandsNothingBackAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedGeneratedValueSequence(new GeneratedValueSequence { MetaverseAttributeId = attributeId, NextValue = 50 });
        var server = new UniqueValueGenerationServer(new ReadOnlySyncRepositoryGuard(repo));
        var options = UniqueValueTestHelpers.Options(dryRun: true);

        await server.ResolveAsync([NumberRequest(SequenceFrom(1), attributeId)], options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await server.ReturnUnusedSequenceNumbersAsync(options), Is.Zero, "Sync Preview reserves nothing, so has nothing to hand back (and the guard would refuse the write)");
            Assert.That((await repo.GetGeneratedValueSequenceAsync(attributeId, null))!.NextValue, Is.EqualTo(50));
        }
    }

    private static SyncRuleMappingGeneration SequenceFrom(long start) =>
        UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence, sequenceStart: start);

    private static GenerationRequest NumberRequest(SyncRuleMappingGeneration generation, int attributeId) =>
        UniqueValueTestHelpers.ImportRequest(generation, attributeId, null, baseValue: null, targetType: AttributeDataType.Number);
}
