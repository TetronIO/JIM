// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for the recall selectors and impact counts (#3059): values of an Accumulate attribute are never
/// recalled, so they are neither stated as recall impact nor selected as recall work. Each test seeds one Synchronisation
/// Rule contributing to a Priority attribute on one Metaverse Object and to an Accumulate attribute on another; only the
/// first object and its value may be counted or selected. The predicates join the value to its attribute, which the
/// in-memory provider cannot be trusted to translate, hence real PostgreSQL.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class AccumulateRecallSelectionDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Accumulate recall selection tests.");

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

    private sealed record Seeded(int ConnectedSystemId, int SyncRuleId, Guid PriorityMvoId, Guid AccumulateOnlyMvoId, int PriorityAttributeId, int AccumulateAttributeId);

    private async Task<Seeded> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var ctx = NewContext();

        var definition = new ConnectorDefinition { Name = $"acc-def-{suffix}" };
        ctx.ConnectorDefinitions.Add(definition);
        await ctx.SaveChangesAsync();
        var system = new ConnectedSystem { Name = $"acc-system-{suffix}", ConnectorDefinitionId = definition.Id, ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem };
        ctx.ConnectedSystems.Add(system);
        await ctx.SaveChangesAsync();
        var objectType = new ConnectedSystemObjectType { ConnectedSystemId = system.Id, Name = "user", Selected = true };
        var mvoType = new MetaverseObjectType { Name = $"acc-person-{suffix}", PluralName = $"acc-people-{suffix}" };
        var priorityAttribute = new MetaverseAttribute { Name = $"acc-dept-{suffix}", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var accumulateAttribute = new MetaverseAttribute
        {
            Name = $"acc-log-{suffix}", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued,
            ContributionMode = AttributeContributionMode.Accumulate
        };
        ctx.ConnectedSystemObjectTypes.Add(objectType);
        ctx.MetaverseObjectTypes.Add(mvoType);
        ctx.MetaverseAttributes.AddRange(priorityAttribute, accumulateAttribute);
        await ctx.SaveChangesAsync();

        var syncRule = new SyncRule
        {
            Name = $"acc-import-{suffix}", Direction = SyncRuleDirection.Import, Enabled = true,
            ConnectedSystemId = system.Id, ConnectedSystemObjectTypeId = objectType.Id, MetaverseObjectTypeId = mvoType.Id
        };
        ctx.SyncRules.Add(syncRule);
        var priorityMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow };
        var accumulateOnlyMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow };
        ctx.MetaverseObjects.AddRange(priorityMvo, accumulateOnlyMvo);
        await ctx.SaveChangesAsync();

        ctx.MetaverseObjectAttributeValues.AddRange(
            Value(priorityMvo, priorityAttribute.Id, "Engineering", syncRule.Id, system.Id),
            Value(priorityMvo, accumulateAttribute.Id, "AD|1|sAMAccountName=alice", syncRule.Id, system.Id),
            Value(accumulateOnlyMvo, accumulateAttribute.Id, "AD|2|sAMAccountName=bob", syncRule.Id, system.Id));
        await ctx.SaveChangesAsync();

        return new Seeded(system.Id, syncRule.Id, priorityMvo.Id, accumulateOnlyMvo.Id, priorityAttribute.Id, accumulateAttribute.Id);
    }

    private static MetaverseObjectAttributeValue Value(MetaverseObject mvo, int attributeId, string value, int syncRuleId, int systemId) => new()
    {
        Id = Guid.NewGuid(), MetaverseObject = mvo, AttributeId = attributeId, StringValue = value,
        ContributedBySyncRuleId = syncRuleId, ContributedBySystemId = systemId
    };

    [Test]
    public async Task GetContributedValuesSummaryAsync_ExcludesAccumulateValuesAsync()
    {
        var seeded = await SeedAsync();
        await using var ctx = NewContext();

        var summary = await new MetaverseRepository(new PostgresDataRepository(ctx)).GetContributedValuesSummaryAsync(seeded.SyncRuleId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.Attributes.Select(a => a.AttributeId), Is.EqualTo(new[] { seeded.PriorityAttributeId }));
            Assert.That(summary.TotalValues, Is.EqualTo(1));
            Assert.That(summary.TotalObjects, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task GetContributedValuesSummaryAsync_ForTheAccumulateAttribute_IsEmptyAsync()
    {
        // The mapping-deletion case: removing a mapping to an Accumulate attribute recalls nothing.
        var seeded = await SeedAsync();
        await using var ctx = NewContext();

        var summary = await new MetaverseRepository(new PostgresDataRepository(ctx)).GetContributedValuesSummaryAsync(seeded.SyncRuleId, seeded.AccumulateAttributeId);

        Assert.That(summary.TotalValues, Is.Zero);
    }

    [Test]
    public async Task GetContributedValueCountsByConnectedSystemAsync_ExcludesAccumulateValuesAsync()
    {
        var seeded = await SeedAsync();
        await using var ctx = NewContext();

        var (valueCount, objectCount) = await new MetaverseRepository(new PostgresDataRepository(ctx)).GetContributedValueCountsByConnectedSystemAsync(seeded.ConnectedSystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(valueCount, Is.EqualTo(1));
            Assert.That(objectCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task GetMetaverseObjectIdsWithValuesContributedBySyncRuleAsync_ExcludesObjectsHoldingOnlyAccumulateValuesAsync()
    {
        var seeded = await SeedAsync();
        await using var ctx = NewContext();

        var ids = await new MetaverseRepository(new PostgresDataRepository(ctx)).GetMetaverseObjectIdsWithValuesContributedBySyncRuleAsync(seeded.SyncRuleId);

        Assert.That(ids, Is.EqualTo(new[] { seeded.PriorityMvoId }));
    }

    [Test]
    public async Task GetMetaverseObjectIdsWithStrandedValuesContributedBySyncRuleAsync_ExcludesObjectsHoldingOnlyAccumulateValuesAsync()
    {
        // Neither object holds a Connected System Object of the system, so both would be stranded but for the mode.
        var seeded = await SeedAsync();
        await using var ctx = NewContext();

        var ids = await new MetaverseRepository(new PostgresDataRepository(ctx))
            .GetMetaverseObjectIdsWithStrandedValuesContributedBySyncRuleAsync(seeded.SyncRuleId, seeded.ConnectedSystemId);

        Assert.That(ids, Is.EqualTo(new[] { seeded.PriorityMvoId }));
    }
}
