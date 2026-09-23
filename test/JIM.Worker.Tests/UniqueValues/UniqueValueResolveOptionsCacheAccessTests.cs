// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Data.Repositories;
using JIM.Models.Transactional;
using NUnit.Framework;
using InMemorySyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// <see cref="UniqueValueResolveOptions.HasKnownMetaverseAssignment"/> and
/// <see cref="UniqueValueResolveOptions.GetKnownMetaverseAssignments"/>: the public read accessors over the
/// otherwise-internal run-scoped assignment cache, added so a caller outside <c>JIM.Application</c> (the
/// worker's adopt-before-generate step and page-flush lifecycle reconciliation, Unique Value Generation, #242,
/// Phase 2 work package G) can read this run's known assignments without gaining write access to the cache
/// itself. Also covers <see cref="UniqueValueGenerationServer.DeleteAssignmentsAsync"/>, which is what keeps
/// the cache these accessors read in step with the database.
/// </summary>
[TestFixture]
public class UniqueValueResolveOptionsCacheAccessTests
{
    [Test]
    public void HasKnownMetaverseAssignment_UnknownObject_ReturnsFalse()
    {
        var options = UniqueValueTestHelpers.Options();

        Assert.That(options.HasKnownMetaverseAssignment(Guid.NewGuid(), UniqueValueTestHelpers.NextAttributeId()), Is.False);
    }

    [Test]
    public async Task HasKnownMetaverseAssignment_AfterPrefetch_ReturnsTrueForTheKnownAttributeAndFalseForAnotherAsync()
    {
        var repo = new InMemorySyncRepository();
        var server = new UniqueValueGenerationServer(repo);
        var options = UniqueValueTestHelpers.Options();
        var mvoId = Guid.NewGuid();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var otherAttributeId = UniqueValueTestHelpers.NextAttributeId();

        repo.SeedGeneratedValueAssignment(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = mvoId,
            MetaverseAttributeId = attributeId,
            Value = "joe.bloggs",
            NormalisedValue = "joe.bloggs",
            State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = 1,
            Created = DateTime.UtcNow,
            LastUpdated = DateTime.UtcNow
        });

        await server.PrefetchAssignmentsAsync([mvoId], [], options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.HasKnownMetaverseAssignment(mvoId, attributeId), Is.True);
            Assert.That(options.HasKnownMetaverseAssignment(mvoId, otherAttributeId), Is.False,
                "a known object with nothing for a DIFFERENT attribute must not report true for that attribute");
        }
    }

    [Test]
    public void GetKnownMetaverseAssignments_UnknownObject_ReturnsEmpty()
    {
        var options = UniqueValueTestHelpers.Options();
        Assert.That(options.GetKnownMetaverseAssignments(Guid.NewGuid()), Is.Empty);
    }

    [Test]
    public async Task GetKnownMetaverseAssignments_AfterPrefetch_ReturnsEveryAssignmentForTheObjectAsync()
    {
        var repo = new InMemorySyncRepository();
        var server = new UniqueValueGenerationServer(repo);
        var options = UniqueValueTestHelpers.Options();
        var mvoId = Guid.NewGuid();
        var attributeId1 = UniqueValueTestHelpers.NextAttributeId();
        var attributeId2 = UniqueValueTestHelpers.NextAttributeId();

        repo.SeedGeneratedValueAssignment(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = mvoId, MetaverseAttributeId = attributeId1,
            Value = "a", NormalisedValue = "a", State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = 1, Created = DateTime.UtcNow, LastUpdated = DateTime.UtcNow
        });
        repo.SeedGeneratedValueAssignment(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = mvoId, MetaverseAttributeId = attributeId2,
            Value = "b", NormalisedValue = "b", State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = 2, Created = DateTime.UtcNow, LastUpdated = DateTime.UtcNow
        });

        await server.PrefetchAssignmentsAsync([mvoId], [], options);

        var assignments = options.GetKnownMetaverseAssignments(mvoId);
        Assert.That(assignments.Select(a => a.MetaverseAttributeId), Is.EquivalentTo(new[] { attributeId1, attributeId2 }));
    }

    [Test]
    public async Task DeleteAssignmentsAsync_DeletesFromTheRepositoryAndDropsFromTheCacheAsync()
    {
        var repo = new InMemorySyncRepository();
        var server = new UniqueValueGenerationServer(repo);
        var options = UniqueValueTestHelpers.Options();
        var mvoId = Guid.NewGuid();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var assignmentId = Guid.NewGuid();

        repo.SeedGeneratedValueAssignment(new GeneratedValueAssignment
        {
            Id = assignmentId, MetaverseObjectId = mvoId, MetaverseAttributeId = attributeId,
            Value = "joe.bloggs", NormalisedValue = "joe.bloggs", State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = 1, Created = DateTime.UtcNow, LastUpdated = DateTime.UtcNow
        });
        await server.PrefetchAssignmentsAsync([mvoId], [], options);
        Assert.That(options.HasKnownMetaverseAssignment(mvoId, attributeId), Is.True, "precondition");

        await server.DeleteAssignmentsAsync([assignmentId], options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await repo.GetGeneratedValueAssignmentAsync(mvoId, attributeId), Is.Null, "deleted from the repository");
            Assert.That(options.HasKnownMetaverseAssignment(mvoId, attributeId), Is.False, "dropped from the run cache in the same call");
        }
    }

    [Test]
    public async Task DeleteAssignmentsAsync_EmptyCollection_MakesNoRepositoryCallAsync()
    {
        var (repo, counts) = CountingSyncRepositoryProxy.Create(new InMemorySyncRepository());
        var server = new UniqueValueGenerationServer(repo);
        var options = UniqueValueTestHelpers.Options();

        await server.DeleteAssignmentsAsync([], options);

        Assert.That(counts.GetValueOrDefault(nameof(ISyncRepository.DeleteGeneratedValueAssignmentsAsync)), Is.EqualTo(0));
    }
}
