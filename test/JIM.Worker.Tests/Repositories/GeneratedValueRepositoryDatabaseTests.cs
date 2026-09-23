// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for the Unique Value Generation repository members work package C adds to
/// <see cref="JIM.Data.Repositories.ISyncRepository"/> (#242, Phase 1 step 4): the value gates, assignment CRUD,
/// the cross-assignment conflict, and sequence seeding and block reservation. <see cref="GeneratedValueSchemaDatabaseTests"/>
/// covers the schema itself (cascades, check constraints, unique indexes); this fixture covers the repository
/// members that sit in front of it.
/// <para>
/// Only a real provider proves the case-insensitive expression indexes, the <c>ANY(...)</c> gate queries, the
/// <c>ON CONFLICT ... DO NOTHING</c> / <c>UPDATE ... RETURNING</c> block reservation and the <c>23505</c> to
/// <see cref="GeneratedValueConflictException"/> translation are correct; the in-memory suite
/// (<c>SyncRepositoryGeneratedValueTests</c>) covers the same contract without PostgreSQL.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class GeneratedValueRepositoryDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL generated value repository tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    private static SyncRepository NewSyncRepository(JimDbContext ctx) => new(new PostgresDataRepository(ctx));

    /// <summary>
    /// One Connected System with a joined-up import mapping (targeting a fresh Text Metaverse attribute) and
    /// export mapping (targeting a fresh Text Connected System attribute), each with a Generation row, plus a
    /// Number Metaverse attribute and Number Connected System attribute for the numeric gate and seeding tests,
    /// and one Metaverse Object / Connected System Object to hang values off.
    /// </summary>
    private async Task<Estate> SeedEstateAsync(string suffix)
    {
        await using var ctx = NewContext();

        var mvoType = new MetaverseObjectType { Name = $"Person-{suffix}", PluralName = $"People-{suffix}" };
        var mvTextAttribute = new MetaverseAttribute { Name = $"Account Name-{suffix}", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var mvNumberAttribute = new MetaverseAttribute { Name = $"Employee Number-{suffix}", Type = AttributeDataType.Number, AttributePlurality = AttributePlurality.SingleValued };
        ctx.MetaverseObjectTypes.Add(mvoType);
        ctx.MetaverseAttributes.AddRange(mvTextAttribute, mvNumberAttribute);

        var connectorDefinition = new ConnectorDefinition { Name = $"def-{suffix}" };
        var system = new ConnectedSystem { Name = $"system-{suffix}", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var csTextAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "accountName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var csNumberAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "employeeNumber", ConnectedSystemObjectType = csType, Type = AttributeDataType.Number,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(csTextAttribute);
        csType.Attributes.Add(csNumberAttribute);
        ctx.AddRange(connectorDefinition, system, csType);
        await ctx.SaveChangesAsync();

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow };
        var cso = new ConnectedSystemObject
        {
            Type = csType,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            DateJoined = DateTime.UtcNow,
            ExternalIdAttributeId = csTextAttribute.Id
        };
        ctx.MetaverseObjects.Add(mvo);
        ctx.ConnectedSystemObjects.Add(cso);
        await ctx.SaveChangesAsync();

        var importRule = new SyncRule
        {
            Name = $"import-{suffix}", Direction = SyncRuleDirection.Import, ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = csType.Id, MetaverseObjectTypeId = mvoType.Id
        };
        var exportRule = new SyncRule
        {
            Name = $"export-{suffix}", Direction = SyncRuleDirection.Export, ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = csType.Id, MetaverseObjectTypeId = mvoType.Id
        };
        ctx.SyncRules.AddRange(importRule, exportRule);
        await ctx.SaveChangesAsync();

        var importMapping = new SyncRuleMapping { SyncRuleId = importRule.Id, TargetMetaverseAttributeId = mvTextAttribute.Id };
        var exportMapping = new SyncRuleMapping { SyncRuleId = exportRule.Id, TargetConnectedSystemAttributeId = csTextAttribute.Id };
        ctx.SyncRuleMappings.AddRange(importMapping, exportMapping);
        await ctx.SaveChangesAsync();

        var importGeneration = new SyncRuleMappingGeneration { SyncRuleMappingId = importMapping.Id, TokenKind = GeneratedValueTokenKind.Sequence };
        ctx.SyncRuleMappingGenerations.Add(importGeneration);
        await ctx.SaveChangesAsync();

        return new Estate(
            MvoId: mvo.Id,
            MvoTypeId: mvoType.Id,
            MvTextAttributeId: mvTextAttribute.Id,
            MvNumberAttributeId: mvNumberAttribute.Id,
            CsoId: cso.Id,
            CsTextAttributeId: csTextAttribute.Id,
            CsNumberAttributeId: csNumberAttribute.Id,
            ImportRuleId: importRule.Id,
            ImportMappingId: importMapping.Id,
            ImportGenerationId: importGeneration.Id);
    }

    private sealed record Estate(
        Guid MvoId,
        int MvoTypeId,
        int MvTextAttributeId,
        int MvNumberAttributeId,
        Guid CsoId,
        int CsTextAttributeId,
        int CsNumberAttributeId,
        int ImportRuleId,
        int ImportMappingId,
        int ImportGenerationId);

    /// <summary>
    /// A second Metaverse Object of the same type, so a gate's "own value is free" exclusion can be proven
    /// against a genuinely different object. Assigned via the shadow FK to avoid re-inserting the estate's type.
    /// </summary>
    private async Task<Guid> CreateSecondMvoAsync(Estate estate)
    {
        await using var ctx = NewContext();
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Created = DateTime.UtcNow };
        ctx.MetaverseObjects.Add(mvo);
        ctx.Entry(mvo).Property("TypeId").CurrentValue = estate.MvoTypeId;
        await ctx.SaveChangesAsync();
        return mvo.Id;
    }

    private async Task AddMvoStringValueAsync(Guid mvoId, int attributeId, string value)
    {
        await using var ctx = NewContext();
        ctx.MetaverseObjectAttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, StringValue = value });
        ctx.Entry(ctx.ChangeTracker.Entries<MetaverseObjectAttributeValue>().Single().Entity).Property("MetaverseObjectId").CurrentValue = mvoId;
        await ctx.SaveChangesAsync();
    }

    private async Task AddMvoNumberValueAsync(Guid mvoId, int attributeId, int? intValue = null, long? longValue = null)
    {
        await using var ctx = NewContext();
        var value = new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, IntValue = intValue, LongValue = longValue };
        ctx.MetaverseObjectAttributeValues.Add(value);
        ctx.Entry(value).Property("MetaverseObjectId").CurrentValue = mvoId;
        await ctx.SaveChangesAsync();
    }

    private static GeneratedValueAssignment ImportAssignment(Estate estate, string value) => new()
    {
        Id = Guid.NewGuid(),
        MetaverseObjectId = estate.MvoId,
        MetaverseAttributeId = estate.MvTextAttributeId,
        Value = value,
        NormalisedValue = value.ToLowerInvariant(),
        State = GeneratedValueAssignmentState.Committed,
        SyncRuleMappingGenerationId = estate.ImportGenerationId,
        CommittedAt = DateTime.UtcNow
    };

    // ---- String value gates ----

    [Test]
    public async Task GetMetaverseAttributeValuesInUseAsync_ReturnsTakenSubsetCaseInsensitivelyAndExcludesTheRequestingObjectAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);
        var secondMvoId = await CreateSecondMvoAsync(estate);

        await AddMvoStringValueAsync(estate.MvoId, estate.MvTextAttributeId, "Alice.Smith");
        await AddMvoStringValueAsync(secondMvoId, estate.MvTextAttributeId, "bob.jones");

        await using var ctx = NewContext();
        var repo = NewSyncRepository(ctx);

        // Neither excluded: both values are taken.
        var both = await repo.GetMetaverseAttributeValuesInUseAsync(estate.MvTextAttributeId, ["alice.smith", "bob.jones", "carol.evans"], null);
        Assert.That(both, Is.EquivalentTo(new[] { "alice.smith", "bob.jones" }));

        // Excluding the estate's own MVO: its value is free for it, the other object's value is still taken.
        var excludingSelf = await repo.GetMetaverseAttributeValuesInUseAsync(estate.MvTextAttributeId, ["alice.smith", "bob.jones"], estate.MvoId);
        Assert.That(excludingSelf, Is.EquivalentTo(new[] { "bob.jones" }));
    }

    [Test]
    public async Task GetConnectedSystemAttributeValuesInUseAsync_ExcludesTheRequestingObjectAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);

        await using (var ctx = NewContext())
        {
            var value = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = estate.CsTextAttributeId, StringValue = "Joe.Bloggs" };
            ctx.ConnectedSystemObjectAttributeValues.Add(value);
            ctx.Entry(value).Property("ConnectedSystemObjectId").CurrentValue = estate.CsoId;
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = NewContext();
        var repo = NewSyncRepository(readCtx);

        var taken = await repo.GetConnectedSystemAttributeValuesInUseAsync(estate.CsTextAttributeId, ["joe.bloggs"], null);
        Assert.That(taken, Is.EquivalentTo(new[] { "joe.bloggs" }));

        var excludingSelf = await repo.GetConnectedSystemAttributeValuesInUseAsync(estate.CsTextAttributeId, ["joe.bloggs"], estate.CsoId);
        Assert.That(excludingSelf, Is.Empty, "the requesting object's own value must always be free for it");
    }

    // ---- Numeric value gates ----

    [Test]
    public async Task GetMetaverseAttributeNumbersInUseAsync_MatchesEitherIntOrLongValueAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);
        var secondMvoId = await CreateSecondMvoAsync(estate);

        await AddMvoNumberValueAsync(estate.MvoId, estate.MvNumberAttributeId, intValue: 1001);
        await AddMvoNumberValueAsync(secondMvoId, estate.MvNumberAttributeId, longValue: 9999999999L);

        await using var ctx = NewContext();
        var repo = NewSyncRepository(ctx);

        var taken = await repo.GetMetaverseAttributeNumbersInUseAsync(estate.MvNumberAttributeId, [1001L, 9999999999L, 42L], null);
        Assert.That(taken, Is.EquivalentTo(new[] { 1001L, 9999999999L }));
    }

    [Test]
    public async Task GetConnectedSystemAttributeNumbersInUseAsync_EmptyInput_ReturnsEmptyAsync()
    {
        await using var ctx = NewContext();
        var repo = NewSyncRepository(ctx);

        var result = await repo.GetConnectedSystemAttributeNumbersInUseAsync(1, [], null);
        Assert.That(result, Is.Empty);
    }

    // ---- Seeding query ----

    [Test]
    public async Task GetHighestNumericValueForAttributeAsync_AcrossIntLongAndDigitOnlyTextValues_ReturnsTheMaximumAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);
        var secondMvoId = await CreateSecondMvoAsync(estate);
        var thirdMvoId = await CreateSecondMvoAsync(estate);

        await AddMvoNumberValueAsync(estate.MvoId, estate.MvNumberAttributeId, intValue: 100);
        await AddMvoNumberValueAsync(secondMvoId, estate.MvNumberAttributeId, longValue: 250);
        await AddMvoStringValueAsync(thirdMvoId, estate.MvNumberAttributeId, "300");

        await using var ctx = NewContext();
        var repo = NewSyncRepository(ctx);

        var highest = await repo.GetHighestNumericValueForAttributeAsync(estate.MvNumberAttributeId, null);
        Assert.That(highest, Is.EqualTo(300L));
    }

    [Test]
    public async Task GetHighestNumericValueForAttributeAsync_PrefixedTextValue_IsNotConsideredAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);
        await AddMvoStringValueAsync(estate.MvoId, estate.MvNumberAttributeId, "EMP0042");

        await using var ctx = NewContext();
        var repo = NewSyncRepository(ctx);

        var highest = await repo.GetHighestNumericValueForAttributeAsync(estate.MvNumberAttributeId, null);
        Assert.That(highest, Is.Null, "a prefixed value is not purely numeric and must not seed the counter");
    }

    [Test]
    public async Task GetHighestNumericValueForAttributeAsync_NoValues_ReturnsNullAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);

        await using var ctx = NewContext();
        var repo = NewSyncRepository(ctx);

        var highest = await repo.GetHighestNumericValueForAttributeAsync(estate.MvNumberAttributeId, null);
        Assert.That(highest, Is.Null);
    }

    // ---- Block reservation ----

    [Test]
    public async Task ReserveGeneratedValueSequenceBlockAsync_FirstReservation_SeedsAtFloorAndReturnsItAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);

        await using var ctx = NewContext();
        var repo = NewSyncRepository(ctx);

        var first = await repo.ReserveGeneratedValueSequenceBlockAsync(estate.MvNumberAttributeId, null, floor: 500, count: 10, increment: 1);
        Assert.That(first, Is.EqualTo(500));

        var sequence = await repo.GetGeneratedValueSequenceAsync(estate.MvNumberAttributeId, null);
        Assert.That(sequence?.NextValue, Is.EqualTo(510));
    }

    [Test]
    public async Task ReserveGeneratedValueSequenceBlockAsync_LowerFloorThanCounter_MovesOnlyForwardAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);

        await using (var ctx = NewContext())
            await NewSyncRepository(ctx).ReserveGeneratedValueSequenceBlockAsync(estate.MvNumberAttributeId, null, floor: 500, count: 20, increment: 1);

        // The counter is now at 520; a reservation with a far lower floor must still continue from 520.
        await using var ctx2 = NewContext();
        var next = await NewSyncRepository(ctx2).ReserveGeneratedValueSequenceBlockAsync(estate.MvNumberAttributeId, null, floor: 1, count: 1, increment: 1);
        Assert.That(next, Is.EqualTo(520), "the counter only ever moves forward (plan decision 3)");
    }

    [Test]
    public async Task ReserveGeneratedValueSequenceBlockAsync_ConcurrentReservations_NeverOverlapAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);

        // Two independent contexts (and so independent connections), reserving concurrently against the same
        // attribute, prove the atomic UPDATE ... RETURNING serialises on the row rather than racing.
        await using var ctxA = NewContext();
        await using var ctxB = NewContext();
        var repoA = NewSyncRepository(ctxA);
        var repoB = NewSyncRepository(ctxB);

        var taskA = repoA.ReserveGeneratedValueSequenceBlockAsync(estate.MvNumberAttributeId, null, floor: 1, count: 25, increment: 1);
        var taskB = repoB.ReserveGeneratedValueSequenceBlockAsync(estate.MvNumberAttributeId, null, floor: 1, count: 25, increment: 1);
        await Task.WhenAll(taskA, taskB);

        var firstA = taskA.Result;
        var firstB = taskB.Result;

        var rangeA = Enumerable.Range(0, 25).Select(i => firstA + i).ToHashSet();
        var rangeB = Enumerable.Range(0, 25).Select(i => firstB + i).ToHashSet();

        Assert.That(rangeA.Overlaps(rangeB), Is.False, "two concurrent reservations against the same attribute must never overlap");
    }

    [Test]
    public async Task IncrementGeneratedValueSequenceAssignedCountAsync_AdvancesTheDisplayCountAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);

        await using var ctx = NewContext();
        var repo = NewSyncRepository(ctx);
        await repo.ReserveGeneratedValueSequenceBlockAsync(estate.MvNumberAttributeId, null, floor: 1, count: 5, increment: 1);
        var sequence = await repo.GetGeneratedValueSequenceAsync(estate.MvNumberAttributeId, null);

        await repo.IncrementGeneratedValueSequenceAssignedCountAsync(sequence!.Id, 4);

        var updated = await repo.GetGeneratedValueSequenceAsync(estate.MvNumberAttributeId, null);
        Assert.That(updated?.AssignedCount, Is.EqualTo(4));
    }

    [Test]
    public void GetGeneratedValueSequenceAsync_NeitherIdGiven_ThrowsArgumentExceptionAsync()
    {
        using var ctx = NewContext();
        var repo = NewSyncRepository(ctx);

        Assert.That(async () => await repo.GetGeneratedValueSequenceAsync(null, null), Throws.ArgumentException);
    }

    // ---- Assignment CRUD and the cross-assignment conflict ----

    [Test]
    public async Task CreateGeneratedValueAssignmentsAsync_PersistsAndIsReadableByEveryLookupAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);
        var assignment = ImportAssignment(estate, "joe.bloggs");

        await using (var ctx = NewContext())
            await NewSyncRepository(ctx).CreateGeneratedValueAssignmentsAsync([assignment]);

        await using var ctx2 = NewContext();
        var repo = NewSyncRepository(ctx2);

        using (Assert.EnterMultipleScope())
        {
            var byObject = await repo.GetGeneratedValueAssignmentAsync(estate.MvoId, estate.MvTextAttributeId);
            Assert.That(byObject?.Id, Is.EqualTo(assignment.Id));

            var byObjects = await repo.GetGeneratedValueAssignmentsForMetaverseObjectsAsync([estate.MvoId]);
            Assert.That(byObjects.Select(a => a.Id), Is.EquivalentTo(new[] { assignment.Id }));

            var byGeneration = await repo.GetGeneratedValueAssignmentsForGenerationAsync(estate.ImportGenerationId);
            Assert.That(byGeneration.Select(a => a.Id), Is.EquivalentTo(new[] { assignment.Id }));
        }
    }

    [Test]
    public async Task CreateGeneratedValueAssignmentsAsync_DuplicateNormalisedValueForSameAttribute_ThrowsGeneratedValueConflictExceptionAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);
        var secondMvoId = await CreateSecondMvoAsync(estate);

        await using (var ctx = NewContext())
            await NewSyncRepository(ctx).CreateGeneratedValueAssignmentsAsync([ImportAssignment(estate, "Joe.Bloggs")]);

        var colliding = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = secondMvoId,
            MetaverseAttributeId = estate.MvTextAttributeId,
            Value = "JOE.BLOGGS",
            NormalisedValue = "joe.bloggs",
            State = GeneratedValueAssignmentState.Proposed,
            SyncRuleMappingGenerationId = estate.ImportGenerationId
        };

        await using var ctx2 = NewContext();
        Assert.That(async () => await NewSyncRepository(ctx2).CreateGeneratedValueAssignmentsAsync([colliding]),
            Throws.InstanceOf<GeneratedValueConflictException>(),
            "the losing side of a concurrent create must see GeneratedValueConflictException, not a raw DbUpdateException");
    }

    [Test]
    public async Task DeleteGeneratedValueAssignmentsAsync_RemovesTheRowAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);
        var assignment = ImportAssignment(estate, "joe.bloggs");

        await using (var ctx = NewContext())
            await NewSyncRepository(ctx).CreateGeneratedValueAssignmentsAsync([assignment]);

        await using (var ctx = NewContext())
            await NewSyncRepository(ctx).DeleteGeneratedValueAssignmentsAsync([assignment.Id]);

        await using var assertCtx = NewContext();
        Assert.That(await assertCtx.GeneratedValueAssignments.AnyAsync(a => a.Id == assignment.Id), Is.False);
    }

    [Test]
    public async Task UpdateGeneratedValueAssignmentAsync_StampsLastUpdatedAndPersistsAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);
        var assignment = ImportAssignment(estate, "joe.bloggs");

        await using (var ctx = NewContext())
            await NewSyncRepository(ctx).CreateGeneratedValueAssignmentsAsync([assignment]);

        Guid assignmentId;
        await using (var ctx = NewContext())
        {
            var repo = NewSyncRepository(ctx);
            var loaded = await repo.GetGeneratedValueAssignmentAsync(estate.MvoId, estate.MvTextAttributeId);
            loaded!.State = GeneratedValueAssignmentState.Remediated;
            loaded.PreviousValue = "joe.bloggs";
            await repo.UpdateGeneratedValueAssignmentAsync(loaded);
            assignmentId = loaded.Id;
        }

        await using var assertCtx = NewContext();
        var persisted = await assertCtx.GeneratedValueAssignments.SingleAsync(a => a.Id == assignmentId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.State, Is.EqualTo(GeneratedValueAssignmentState.Remediated));
            Assert.That(persisted.PreviousValue, Is.EqualTo("joe.bloggs"));
        }
    }

    // ---- Mapping with Generation round-trips through the Include chain ----

    [Test]
    public async Task SyncRuleMappingWithGenerationAndExclusion_RoundTripsThroughCreateAndGetAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        int excludedSystemId, mappingId;

        await using (var ctx = NewContext())
        {
            var mvoType = new MetaverseObjectType { Name = $"Person-{suffix}", PluralName = $"People-{suffix}" };
            var mvAttribute = new MetaverseAttribute { Name = $"Account Name-{suffix}", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
            ctx.MetaverseObjectTypes.Add(mvoType);
            ctx.MetaverseAttributes.Add(mvAttribute);

            var connectorDefinition = new ConnectorDefinition { Name = $"def-{suffix}" };
            var system = new ConnectedSystem { Name = $"system-{suffix}", ConnectorDefinition = connectorDefinition };
            var excludedConnectorDefinition = new ConnectorDefinition { Name = $"def-excl-{suffix}" };
            var excludedSystem = new ConnectedSystem { Name = $"system-excl-{suffix}", ConnectorDefinition = excludedConnectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            ctx.AddRange(connectorDefinition, excludedConnectorDefinition, system, excludedSystem, csType);
            await ctx.SaveChangesAsync();
            excludedSystemId = excludedSystem.Id;

            var importRule = new SyncRule
            {
                Name = $"import-{suffix}", Direction = SyncRuleDirection.Import, ConnectedSystemId = system.Id,
                ConnectedSystemObjectTypeId = csType.Id, MetaverseObjectTypeId = mvoType.Id
            };
            ctx.SyncRules.Add(importRule);
            await ctx.SaveChangesAsync();

            // Created through the repository's own CreateSyncRuleMappingAsync, per Phase 1 step 3's brief: EF
            // persists the Generation/Exclusion graph attached to the mapping with no extra repository code.
            var mapping = new SyncRuleMapping
            {
                SyncRuleId = importRule.Id,
                TargetMetaverseAttributeId = mvAttribute.Id,
                Generation = new SyncRuleMappingGeneration
                {
                    TokenKind = GeneratedValueTokenKind.Sequence,
                    Exclusions = { new SyncRuleMappingGenerationExclusion { ConnectedSystemId = excludedSystem.Id } }
                }
            };

            var repository = new PostgresDataRepository(ctx);
            await repository.ConnectedSystems.CreateSyncRuleMappingAsync(mapping);
            mappingId = mapping.Id;
        }

        await using var readCtx = NewContext();
        var readRepository = new PostgresDataRepository(readCtx);
        var loaded = await readRepository.ConnectedSystems.GetSyncRuleMappingAsync(mappingId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Generation, Is.Not.Null, "GetSyncRuleMappingAsync must load the Generation navigation alongside Sources");
            Assert.That(loaded.Generation!.TokenKind, Is.EqualTo(GeneratedValueTokenKind.Sequence));
            Assert.That(loaded.Generation.Exclusions.Select(e => e.ConnectedSystemId), Is.EquivalentTo(new[] { excludedSystemId }),
                "GetSyncRuleMappingAsync must load the Generation's Exclusions too");
        }
    }
}
