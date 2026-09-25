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
/// The Phase 3 administration surface on <see cref="UniqueValueGenerationServer"/> (#242, Work Package A):
/// the read-only sequence state preview, the Metaverse Object generated-values list, "Start again", the
/// save-time counter raise, and the pure candidate preview.
/// </summary>
[TestFixture]
public class UniqueValueGenerationServerAdminSurfaceTests
{
    private static SyncRuleMapping ImportMapping(int attributeId, SyncRuleMappingGeneration generation, string attributeName = "Employee Number") => new()
    {
        Id = 42,
        TargetMetaverseAttributeId = attributeId,
        TargetMetaverseAttribute = new MetaverseAttribute { Id = attributeId, Name = attributeName, Type = AttributeDataType.LongNumber },
        Generation = generation
    };

    // ---- GetSequenceStateAsync ----

    [Test]
    public async Task GetSequenceStateAsync_NotAGeneratedMapping_ReturnsNullAsync()
    {
        var server = new UniqueValueGenerationServer(new InMemorySyncRepository());
        var mapping = new SyncRuleMapping { Id = 1, TargetMetaverseAttributeId = 5 };

        var result = await server.GetSequenceStateAsync(mapping);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetSequenceStateAsync_NotASequenceToken_ReturnsNullAsync()
    {
        var server = new UniqueValueGenerationServer(new InMemorySyncRepository());
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.OnlyIfTaken);
        var mapping = ImportMapping(UniqueValueTestHelpers.NextAttributeId(), generation);

        var result = await server.GetSequenceStateAsync(mapping);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetSequenceStateAsync_NeverSeeded_ReportsTheConfiguredStartAndIsNotSeededAsync()
    {
        var server = new UniqueValueGenerationServer(new InMemorySyncRepository());
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.Sequence, sequenceStart: 1000);
        var mapping = ImportMapping(attributeId, generation);

        var result = await server.GetSequenceStateAsync(mapping);

        Assert.That(result, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result!.NextNumber, Is.EqualTo(1000));
            Assert.That(result.NextNumberFormatted, Is.EqualTo("1000"));
            Assert.That(result.AssignedCount, Is.Zero);
            Assert.That(result.IsSeeded, Is.False);
            Assert.That(result.AttributeName, Is.EqualTo("Employee Number"));
        }
    }

    [Test]
    public async Task GetSequenceStateAsync_AlreadySeeded_ReportsTheCounterAndAssignedCountAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        await repo.ReserveGeneratedValueSequenceBlockAsync(attributeId, null, floor: 1, count: 5, increment: 1);
        var sequence = await repo.GetGeneratedValueSequenceAsync(attributeId, null);
        await repo.IncrementGeneratedValueSequenceAssignedCountAsync(sequence!.Id, 3);

        var server = new UniqueValueGenerationServer(repo);
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.Sequence, sequenceStart: 1);
        var mapping = ImportMapping(attributeId, generation);

        var result = await server.GetSequenceStateAsync(mapping);

        Assert.That(result, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result!.NextNumber, Is.EqualTo(6));
            Assert.That(result.AssignedCount, Is.EqualTo(3));
            Assert.That(result.IsSeeded, Is.True);
        }
    }

    [Test]
    public async Task GetSequenceStateAsync_SeededButBelowARaisedStart_ReportsTheRaisedStartAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        await repo.ReserveGeneratedValueSequenceBlockAsync(attributeId, null, floor: 1, count: 5, increment: 1);
        // The counter now stands at 6.

        var server = new UniqueValueGenerationServer(repo);
        // A flow whose configured start (1000) has not yet been applied to the counter.
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.Sequence, sequenceStart: 1000);
        var mapping = ImportMapping(attributeId, generation);

        var result = await server.GetSequenceStateAsync(mapping);

        Assert.That(result!.NextNumber, Is.EqualTo(1000));
    }

    [Test]
    public async Task GetSequenceStateAsync_FixedWidthExceeded_FlagsItAsync()
    {
        var server = new UniqueValueGenerationServer(new InMemorySyncRepository());
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.Sequence, sequenceStart: 100000, fixedWidth: 3);
        var mapping = ImportMapping(attributeId, generation);

        var result = await server.GetSequenceStateAsync(mapping);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result!.NextNumberWidthExceeded, Is.True);
            Assert.That(result.NextNumberFormatted, Is.EqualTo("100000"));
        }
    }

    // ---- RaiseSequenceStartIfHigherAsync ----

    [Test]
    public async Task RaiseSequenceStartIfHigherAsync_NotASequenceToken_ReturnsNullAndDoesNothingAsync()
    {
        var server = new UniqueValueGenerationServer(new InMemorySyncRepository());
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.OnlyIfTaken);
        var mapping = ImportMapping(UniqueValueTestHelpers.NextAttributeId(), generation);

        var result = await server.RaiseSequenceStartIfHigherAsync(mapping);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task RaiseSequenceStartIfHigherAsync_NoCounterYet_ReturnsNullAndSeedsNothingAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var server = new UniqueValueGenerationServer(repo);
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.Sequence, sequenceStart: 500);
        var mapping = ImportMapping(attributeId, generation);

        var result = await server.RaiseSequenceStartIfHigherAsync(mapping);

        Assert.That(result, Is.Null);
        Assert.That(await repo.GetGeneratedValueSequenceAsync(attributeId, null), Is.Null);
    }

    [Test]
    public async Task RaiseSequenceStartIfHigherAsync_HigherThanCurrent_MovesTheCounterAndReportsItAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        await repo.ReserveGeneratedValueSequenceBlockAsync(attributeId, null, floor: 1, count: 5, increment: 1);
        // The counter now stands at 6.

        var server = new UniqueValueGenerationServer(repo);
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.Sequence, sequenceStart: 500);
        var mapping = ImportMapping(attributeId, generation);

        var result = await server.RaiseSequenceStartIfHigherAsync(mapping);

        Assert.That(result, Is.EqualTo(new SequenceSkippedAhead(6, 500)));
        var sequence = await repo.GetGeneratedValueSequenceAsync(attributeId, null);
        Assert.That(sequence!.NextValue, Is.EqualTo(500));
    }

    [Test]
    public async Task RaiseSequenceStartIfHigherAsync_LowerThanCurrent_HasNoEffectAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        await repo.ReserveGeneratedValueSequenceBlockAsync(attributeId, null, floor: 100, count: 1, increment: 1);
        // The counter now stands at 101.

        var server = new UniqueValueGenerationServer(repo);
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.Sequence, sequenceStart: 1);
        var mapping = ImportMapping(attributeId, generation);

        var result = await server.RaiseSequenceStartIfHigherAsync(mapping);

        Assert.That(result, Is.Null);
        var sequence = await repo.GetGeneratedValueSequenceAsync(attributeId, null);
        Assert.That(sequence!.NextValue, Is.EqualTo(101));
    }

    // ---- RestartAsync ----

    [Test]
    public async Task RestartAsync_NotASequenceToken_IsANoOpReturningZerosAsync()
    {
        var server = new UniqueValueGenerationServer(new InMemorySyncRepository());
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.Random);
        var mapping = ImportMapping(UniqueValueTestHelpers.NextAttributeId(), generation);

        var result = await server.RestartAsync(mapping);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RetiredValuesForgotten, Is.Zero);
            Assert.That(result.CounterFrom, Is.Null);
            Assert.That(result.CounterTo, Is.Null);
        }
    }

    [Test]
    public async Task RestartAsync_SequenceNeverSeeded_IsANoOpAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var server = new UniqueValueGenerationServer(repo);
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.Sequence, sequenceStart: 1);
        var mapping = ImportMapping(attributeId, generation);

        var result = await server.RestartAsync(mapping);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CounterFrom, Is.Null);
            Assert.That(result.CounterTo, Is.Null);
        }
    }

    [Test]
    public async Task RestartAsync_SequenceAlreadySeeded_MovesTheCounterBackToTheFlowsStartAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        await repo.ReserveGeneratedValueSequenceBlockAsync(attributeId, null, floor: 100, count: 50, increment: 1);
        // The counter now stands at 150.

        var server = new UniqueValueGenerationServer(repo);
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.Sequence, sequenceStart: 1);
        var mapping = ImportMapping(attributeId, generation);

        var result = await server.RestartAsync(mapping);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RetiredValuesForgotten, Is.Zero, "the retired values register does not exist until release 2");
            Assert.That(result.CounterFrom, Is.EqualTo(150));
            Assert.That(result.CounterTo, Is.EqualTo(1));
        }

        var sequence = await repo.GetGeneratedValueSequenceAsync(attributeId, null);
        Assert.That(sequence!.NextValue, Is.EqualTo(1));
    }

    // ---- GetAssignmentsForMetaverseObjectAsync / CountObjectsAwaitingValueAsync (thin passthroughs) ----

    [Test]
    public async Task GetAssignmentsForMetaverseObjectAsync_DelegatesToTheRepositoryAsync()
    {
        var repo = new InMemorySyncRepository();
        var mvoId = Guid.NewGuid();
        var generation = UniqueValueTestHelpers.Generation();
        repo.SeedGeneratedValueAssignment(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = mvoId,
            MetaverseAttributeId = generation.Id,
            Value = "alice.smith",
            NormalisedValue = "alice.smith",
            SyncRuleMappingGenerationId = generation.Id
        });
        var server = new UniqueValueGenerationServer(repo);

        var result = await server.GetAssignmentsForMetaverseObjectAsync(mvoId);

        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task CountObjectsAwaitingValueAsync_DelegatesToTheRepositoryAsync()
    {
        var server = new UniqueValueGenerationServer(new InMemorySyncRepository());

        var result = await server.CountObjectsAwaitingValueAsync(1, 2, 3);

        Assert.That(result, Is.Zero);
    }

    // ---- DescribeGeneratedCandidates (pure) ----

    [Test]
    public void DescribeGeneratedCandidates_OnlyIfTaken_ReturnsTheBaseValueThenSuffixedCandidates()
    {
        var server = new UniqueValueGenerationServer(new InMemorySyncRepository());
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.OnlyIfTaken);

        var preview = server.DescribeGeneratedCandidates(generation, AttributeDataType.Text, "joe.bloggs", 3);

        using (Assert.EnterMultipleScope())
        {
            // FirstValue is the bare base value (attempt 0); Candidates are what follows once it is taken.
            Assert.That(preview.FirstValue, Is.EqualTo("joe.bloggs"));
            Assert.That(preview.Candidates, Is.EqualTo(new[] { "joe.bloggs1", "joe.bloggs2", "joe.bloggs3" }));
            Assert.That(preview.Example, Is.Null);
        }
    }

    [Test]
    public void DescribeGeneratedCandidates_Sequence_ReturnsCandidatesFromTheConfiguredStart()
    {
        var server = new UniqueValueGenerationServer(new InMemorySyncRepository());
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.Sequence, sequenceStart: 1000, sequenceIncrement: 1);

        var preview = server.DescribeGeneratedCandidates(generation, AttributeDataType.Text, "EMP", 3);

        Assert.That(preview.Candidates, Is.EqualTo(new[] { "EMP1000", "EMP1001", "EMP1002" }));
    }

    [Test]
    public void DescribeGeneratedCandidates_Random_ReturnsASingleCryptographicExample()
    {
        var server = new UniqueValueGenerationServer(new InMemorySyncRepository());
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.Random, randomFormat: GeneratedValueRandomFormat.Hex, randomLength: 8);

        var preview = server.DescribeGeneratedCandidates(generation, AttributeDataType.Text, null, 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Example, Is.Not.Null.And.Length.EqualTo(8));
            Assert.That(preview.Candidates, Is.Empty);
            Assert.That(preview.FirstValue, Is.Null);
        }
    }

    [Test]
    public void DescribeGeneratedCandidates_PlacementText_NamesTheAtSignRuleWhenPresent()
    {
        var server = new UniqueValueGenerationServer(new InMemorySyncRepository());
        var generation = UniqueValueTestHelpers.Generation(GeneratedValueTokenKind.OnlyIfTaken);

        var preview = server.DescribeGeneratedCandidates(generation, AttributeDataType.Text, "joe@example.com", 1);

        Assert.That(preview.PlacementText, Does.Contain("@"));
    }

    [Test]
    public void DescribeGeneratedCandidates_InvalidCount_ThrowsArgumentOutOfRangeException()
    {
        var server = new UniqueValueGenerationServer(new InMemorySyncRepository());
        var generation = UniqueValueTestHelpers.Generation();

        Assert.That(() => server.DescribeGeneratedCandidates(generation, AttributeDataType.Text, "x", 0),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
