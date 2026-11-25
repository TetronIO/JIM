// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL proof of the counts behind the contribution mode impact report (#3059, Phase 3), a set-based raw SQL
/// read the in-memory store cannot run.
///
/// Two Connected Systems, A and B, each with an enabled import rule (RA, RB) flowing into "Account Audit Log", and a
/// disabled rule RD on A doing the same. Five people:
/// <list type="bullet">
/// <item>M1: joined to A and B, holding values from RA and RB (several contributors).</item>
/// <item>M2: joined to B only, holding a value from RA (no longer supplied) and one from RB.</item>
/// <item>M3: holding a value whose rule was deleted (no longer supplied).</item>
/// <item>M4: joined to A and B, holding values from RA only (may gain RB's).</item>
/// <item>M5: joined to A, holding a value from the disabled RD (no longer supplied), and a sign-in value, which no
/// contribution mode affects.</item>
/// </list>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ContributionModeImpactCountsDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL contribution mode impact tests.");

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

    [Test]
    public async Task GetContributionModeImpactCountsAsync_CountsEachKindOfObjectAsync()
    {
        var attributeId = await SeedAsync();

        await using var ctx = NewContext();
        var counts = await new PostgresDataRepository(ctx).Metaverse.GetContributionModeImpactCountsAsync(attributeId);

        Assert.That(counts, Is.EqualTo(new ContributionModeImpactCounts(
            ValuesNoLongerSupplied: 3,
            ObjectsWithValuesNoLongerSupplied: 3,
            ObjectsWithSeveralContributors: 1,
            ObjectsLosingValuesUnderPriority: 4,
            ObjectsThatMayGainValues: 1)));
    }

    private async Task<int> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var ctx = NewContext();

        var definition = new ConnectorDefinition { Name = $"imp-def-{suffix}" };
        ctx.ConnectorDefinitions.Add(definition);
        await ctx.SaveChangesAsync();
        var systemA = new ConnectedSystem { Name = $"imp-a-{suffix}", ConnectorDefinitionId = definition.Id, ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem };
        var systemB = new ConnectedSystem { Name = $"imp-b-{suffix}", ConnectorDefinitionId = definition.Id, ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem };
        ctx.ConnectedSystems.AddRange(systemA, systemB);
        await ctx.SaveChangesAsync();
        var typeA = new ConnectedSystemObjectType { ConnectedSystemId = systemA.Id, Name = "user", Selected = true };
        var typeB = new ConnectedSystemObjectType { ConnectedSystemId = systemB.Id, Name = "user", Selected = true };
        var person = new MetaverseObjectType { Name = $"imp-person-{suffix}", PluralName = $"imp-people-{suffix}" };
        var auditLog = new MetaverseAttribute
        {
            Name = $"imp-log-{suffix}", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued,
            ContributionMode = AttributeContributionMode.Accumulate
        };
        ctx.ConnectedSystemObjectTypes.AddRange(typeA, typeB);
        ctx.MetaverseObjectTypes.Add(person);
        ctx.MetaverseAttributes.Add(auditLog);
        await ctx.SaveChangesAsync();

        SyncRule Rule(string name, ConnectedSystem system, ConnectedSystemObjectType type, bool enabled)
        {
            var rule = new SyncRule
            {
                Name = $"{name}-{suffix}", Direction = SyncRuleDirection.Import, Enabled = enabled,
                ConnectedSystemId = system.Id, ConnectedSystemObjectTypeId = type.Id, MetaverseObjectTypeId = person.Id
            };
            rule.AttributeFlowRules.Add(new SyncRuleMapping { SyncRule = rule, TargetMetaverseAttributeId = auditLog.Id });
            return rule;
        }
        var ra = Rule("ra", systemA, typeA, true);
        var rb = Rule("rb", systemB, typeB, true);
        var rd = Rule("rd", systemA, typeA, false);
        ctx.SyncRules.AddRange(ra, rb, rd);

        var people = Enumerable.Range(1, 5).Select(_ => new MetaverseObject { Id = Guid.NewGuid(), Type = person, Created = DateTime.UtcNow }).ToList();
        ctx.MetaverseObjects.AddRange(people);
        await ctx.SaveChangesAsync();
        var (m1, m2, m3, m4, m5) = (people[0], people[1], people[2], people[3], people[4]);

        void Join(MetaverseObject mvo, ConnectedSystem system, ConnectedSystemObjectType type) =>
            ctx.ConnectedSystemObjects.Add(new ConnectedSystemObject
            {
                Id = Guid.NewGuid(), ConnectedSystemId = system.Id, TypeId = type.Id, MetaverseObjectId = mvo.Id,
                Created = DateTime.UtcNow, Status = ConnectedSystemObjectStatus.Normal
            });
        Join(m1, systemA, typeA); Join(m1, systemB, typeB);
        Join(m2, systemB, typeB);
        Join(m4, systemA, typeA); Join(m4, systemB, typeB);
        Join(m5, systemA, typeA);

        void Value(MetaverseObject mvo, string text, SyncRule? rule, ConnectedSystem? system, ValueContributorKind kind = ValueContributorKind.SynchronisationRule) =>
            ctx.MetaverseObjectAttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(), MetaverseObject = mvo, AttributeId = auditLog.Id, StringValue = text,
                ContributedBySyncRuleId = rule?.Id, ContributedBySystemId = system?.Id, ContributedByKind = kind
            });
        Value(m1, "a1", ra, systemA); Value(m1, "b1", rb, systemB);
        Value(m2, "a2", ra, systemA); Value(m2, "b2", rb, systemB);
        Value(m3, "gone", null, systemA);
        Value(m4, "a4", ra, systemA);
        Value(m5, "d5", rd, systemA); Value(m5, "signed-in", null, null, ValueContributorKind.SignInClaim);
        await ctx.SaveChangesAsync();
        return auditLog.Id;
    }
}
