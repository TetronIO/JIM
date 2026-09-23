// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// Real-PostgreSQL, end-to-end cover for <see cref="UniqueValueGenerationServer"/> (#242, Phase 2 step 6):
/// resolving a page of import-mode requests against seeded Metaverse values in mixed case, a sequence's first
/// use seeding from an existing value, and a commit conflict returning its loser. The unit suite
/// (<c>UniqueValueGenerationServerResolveTests</c>, <c>UniqueValueGenerationServerCommitTests</c>) covers the
/// same contract against the in-memory repository; only a real provider proves the case-insensitive expression
/// indexes and the <c>23505</c> to <see cref="GeneratedValueConflictException"/> translation the service relies
/// on actually hold.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class UniqueValueGenerationDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Unique Value Generation service tests.");

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
        .Options);

    private static SyncRepository NewSyncRepository(JimDbContext ctx) => new(new PostgresDataRepository(ctx));

    /// <summary>
    /// One Metaverse Object Type, one Text attribute, one Number attribute, and one import Synchronisation Rule
    /// mapping with a Generation row per attribute, each id unique to this call so parallel test runs never
    /// collide.
    /// </summary>
    private async Task<Estate> SeedEstateAsync(string suffix)
    {
        await using var ctx = NewContext();

        var mvoType = new MetaverseObjectType { Name = $"Person-{suffix}", PluralName = $"People-{suffix}" };
        var textAttribute = new MetaverseAttribute { Name = $"Account Name-{suffix}", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var numberAttribute = new MetaverseAttribute { Name = $"Employee Number-{suffix}", Type = AttributeDataType.Number, AttributePlurality = AttributePlurality.SingleValued };
        ctx.MetaverseObjectTypes.Add(mvoType);
        ctx.MetaverseAttributes.AddRange(textAttribute, numberAttribute);

        var connectorDefinition = new ConnectorDefinition { Name = $"def-{suffix}" };
        var system = new ConnectedSystem { Name = $"system-{suffix}", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        ctx.AddRange(connectorDefinition, system, csType);
        await ctx.SaveChangesAsync();

        var importRule = new SyncRule
        {
            Name = $"import-{suffix}", Direction = SyncRuleDirection.Import, ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = csType.Id, MetaverseObjectTypeId = mvoType.Id
        };
        ctx.SyncRules.Add(importRule);
        await ctx.SaveChangesAsync();

        var textMapping = new SyncRuleMapping { SyncRuleId = importRule.Id, TargetMetaverseAttributeId = textAttribute.Id };
        var numberMapping = new SyncRuleMapping { SyncRuleId = importRule.Id, TargetMetaverseAttributeId = numberAttribute.Id };
        ctx.SyncRuleMappings.AddRange(textMapping, numberMapping);
        await ctx.SaveChangesAsync();

        var textGeneration = new SyncRuleMappingGeneration { SyncRuleMappingId = textMapping.Id, TokenKind = GeneratedValueTokenKind.OnlyIfTaken };
        var numberGeneration = new SyncRuleMappingGeneration { SyncRuleMappingId = numberMapping.Id, TokenKind = GeneratedValueTokenKind.Sequence, SequenceStart = 100456 };
        ctx.SyncRuleMappingGenerations.AddRange(textGeneration, numberGeneration);
        await ctx.SaveChangesAsync();

        return new Estate(mvoType.Id, textAttribute.Id, numberAttribute.Id, textGeneration, numberGeneration);
    }

    private async Task<Guid> SeedMvoWithTextValueAsync(Estate estate, string value)
    {
        await using var ctx = NewContext();
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Created = DateTime.UtcNow };
        ctx.MetaverseObjects.Add(mvo);
        ctx.Entry(mvo).Property("TypeId").CurrentValue = estate.MvoTypeId;
        await ctx.SaveChangesAsync();

        var attributeValue = new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = estate.TextAttributeId, StringValue = value };
        ctx.MetaverseObjectAttributeValues.Add(attributeValue);
        ctx.Entry(attributeValue).Property("MetaverseObjectId").CurrentValue = mvo.Id;
        await ctx.SaveChangesAsync();

        return mvo.Id;
    }

    private async Task<Guid> SeedMvoWithNumberValueAsync(Estate estate, long value)
    {
        await using var ctx = NewContext();
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Created = DateTime.UtcNow };
        ctx.MetaverseObjects.Add(mvo);
        ctx.Entry(mvo).Property("TypeId").CurrentValue = estate.MvoTypeId;
        await ctx.SaveChangesAsync();

        var attributeValue = new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = estate.NumberAttributeId, LongValue = value };
        ctx.MetaverseObjectAttributeValues.Add(attributeValue);
        ctx.Entry(attributeValue).Property("MetaverseObjectId").CurrentValue = mvo.Id;
        await ctx.SaveChangesAsync();

        return mvo.Id;
    }

    /// <summary>
    /// A bare, otherwise-empty Metaverse Object: what the worker would have persisted, in the same page flush,
    /// before calling <see cref="UniqueValueGenerationServer.CommitAssignmentsAsync"/> with its real id. The
    /// assignment's foreign key requires the row to already exist.
    /// </summary>
    private async Task<Guid> CreateBareMvoAsync(Estate estate)
    {
        await using var ctx = NewContext();
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Created = DateTime.UtcNow };
        ctx.MetaverseObjects.Add(mvo);
        ctx.Entry(mvo).Property("TypeId").CurrentValue = estate.MvoTypeId;
        await ctx.SaveChangesAsync();
        return mvo.Id;
    }

    private sealed record Estate(int MvoTypeId, int TextAttributeId, int NumberAttributeId, SyncRuleMappingGeneration TextGeneration, SyncRuleMappingGeneration NumberGeneration);

    [Test]
    public async Task ResolveAndCommit_PageOfImportRequestsAgainstMixedCaseSeededValues_ProducesDistinctValuesAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);
        await SeedMvoWithTextValueAsync(estate, "Joe.Bloggs");

        await using var ctx = NewContext();
        var repository = NewSyncRepository(ctx);
        var server = new UniqueValueGenerationServer(repository);
        var options = new UniqueValueResolveOptions { Reservations = new UniqueValueReservationSet(), ReservationOwnerId = Guid.NewGuid() };

        var requestA = new GenerationRequest
        {
            Mode = GeneratedValueMode.Import, MetaverseAttributeId = estate.TextAttributeId, Generation = estate.TextGeneration,
            TargetType = AttributeDataType.Text, AttributeName = "Account Name", BaseValue = "joe.bloggs"
        };
        var requestB = new GenerationRequest
        {
            Mode = GeneratedValueMode.Import, MetaverseAttributeId = estate.TextAttributeId, Generation = estate.TextGeneration,
            TargetType = AttributeDataType.Text, AttributeName = "Account Name", BaseValue = "jane.doe"
        };

        var outcomes = await server.ResolveAsync([requestA, requestB], options);
        var mvoA = await CreateBareMvoAsync(estate);
        var mvoB = await CreateBareMvoAsync(estate);
        var resolver = new Dictionary<GenerationRequest, Guid> { [requestA] = mvoA, [requestB] = mvoB };

        var losers = await server.CommitAssignmentsAsync(outcomes, r => resolver[r]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(losers, Is.Empty);
            Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs1"), "the seeded value is mixed case; the case-insensitive index must still catch the lower-case candidate");
            Assert.That(outcomes[1].Value, Is.EqualTo("jane.doe"));

            var persistedA = await repository.GetGeneratedValueAssignmentAsync(mvoA, estate.TextAttributeId);
            Assert.That(persistedA?.Value, Is.EqualTo("joe.bloggs1"));
        }
    }

    /// <summary>
    /// The companion direction the mixed-case test above does not cover: a MIXED-CASE candidate (base value not
    /// already lower) checked against a LOWER-CASE stored value. PostgreSQL's gate queries only lower the stored
    /// side (matching the expression index); an un-normalised candidate sent as the query parameter would not
    /// match, silently issuing a case-insensitive duplicate of an existing value.
    /// </summary>
    [Test]
    public async Task ResolveAsync_MixedCaseCandidate_StillCollidesWithALowerCaseSeededValueAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);
        await SeedMvoWithTextValueAsync(estate, "joe.bloggs");

        await using var ctx = NewContext();
        var repository = NewSyncRepository(ctx);
        var server = new UniqueValueGenerationServer(repository);
        var options = new UniqueValueResolveOptions { Reservations = new UniqueValueReservationSet(), ReservationOwnerId = Guid.NewGuid() };

        var request = new GenerationRequest
        {
            Mode = GeneratedValueMode.Import, MetaverseAttributeId = estate.TextAttributeId, Generation = estate.TextGeneration,
            TargetType = AttributeDataType.Text, AttributeName = "Account Name", BaseValue = "Joe.Bloggs"
        };

        var outcomes = await server.ResolveAsync([request], options);

        Assert.That(outcomes[0].Value, Is.EqualTo("Joe.Bloggs1"),
            "the mixed-case candidate must still be recognised as colliding with the lower-case seeded value");
    }

    [Test]
    public async Task ResolveAsync_SequenceFirstUseAgainstSeededValues_SeedsFromTheHighestExistingValueAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);
        await SeedMvoWithNumberValueAsync(estate, 100999);

        await using var ctx = NewContext();
        var repository = NewSyncRepository(ctx);
        var server = new UniqueValueGenerationServer(repository);
        var options = new UniqueValueResolveOptions { Reservations = new UniqueValueReservationSet(), ReservationOwnerId = Guid.NewGuid() };

        var request = new GenerationRequest
        {
            Mode = GeneratedValueMode.Import, MetaverseAttributeId = estate.NumberAttributeId, Generation = estate.NumberGeneration,
            TargetType = AttributeDataType.Number, AttributeName = "Employee Number"
        };

        var outcomes = await server.ResolveAsync([request], options);

        Assert.That(outcomes[0].NumericValue, Is.EqualTo(101000), "the counter must seed from the highest existing value plus the increment, per plan decision 3");
    }

    [Test]
    public async Task CommitAssignmentsAsync_ConflictingBatch_ReturnsTheLoserAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);
        var concurrentMvoId = await CreateBareMvoAsync(estate);
        var losingMvoId = await CreateBareMvoAsync(estate);

        await using var ctx = NewContext();
        var repository = NewSyncRepository(ctx);

        // Simulates a concurrent run that committed "joe.bloggs" for this attribute between this run's gates
        // clearing and this call.
        var concurrentAssignment = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = concurrentMvoId, MetaverseAttributeId = estate.TextAttributeId,
            Value = "joe.bloggs", NormalisedValue = "joe.bloggs", State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = estate.TextGeneration.Id
        };
        await repository.CreateGeneratedValueAssignmentsAsync([concurrentAssignment]);

        var losingRequest = new GenerationRequest
        {
            Mode = GeneratedValueMode.Import, MetaverseAttributeId = estate.TextAttributeId, Generation = estate.TextGeneration,
            TargetType = AttributeDataType.Text, AttributeName = "Account Name", BaseValue = "joe.bloggs"
        };
        var loserOutcome = new GenerationOutcome(losingRequest, GenerationOutcomeKind.Generated, "joe.bloggs", null,
            new GeneratedValueAssignment
            {
                Id = Guid.NewGuid(), MetaverseAttributeId = estate.TextAttributeId, Value = "joe.bloggs",
                NormalisedValue = "joe.bloggs", State = GeneratedValueAssignmentState.Proposed,
                SyncRuleMappingGenerationId = estate.TextGeneration.Id
            }, null);

        var server = new UniqueValueGenerationServer(repository);
        var losers = await server.CommitAssignmentsAsync([loserOutcome], _ => losingMvoId);

        Assert.That(losers, Has.Count.EqualTo(1));
        Assert.That(losers[0], Is.SameAs(loserOutcome));
    }
}
