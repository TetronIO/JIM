// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers.Preview;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL proof of the Object Matching adapter (#1457) on two counts at once: that the matching query
/// accepts the rules the adapter materialises from a proposal, and that running the preview over a live database
/// leaves every synchronisation-integrity table byte-identical.
///
/// The first is not a formality. The matching query reads each rule's attribute ENTITIES rather than their ids: it
/// switches on the source attribute's data type to pick a comparison, and filters on the target attribute's id
/// read off the navigation property. A stand-in carrying only ids matches nothing while looking perfectly well
/// formed, and the mocked unit fixture cannot tell the difference because its repository never looks at the
/// entities either. This test does, because the real query does.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ObjectMatchingPreviewIsolationDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Object Matching preview isolation tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(@"
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory') LOOP
                    EXECUTE 'TRUNCATE TABLE ""' || r.tablename || '"" RESTART IDENTITY CASCADE';
                END LOOP;
            END $$;");
    }

    [Test]
    public async Task EvaluateDeltasAsync_RuleRetargetedOverLiveDatabase_ReportsTheSwapAndPersistsNothingAsync()
    {
        // Arrange - matching on employeeID joins the unjoined account to Ada today; matching on mail would join it
        // to Grace instead, which is the identity corruption this preview exists to catch before it happens.
        var seeded = await SeedMatchingTopologyAsync();

        var before = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        List<PreviewDelta> deltas;
        List<PreviewImpactCount> counts;
        List<PreviewValidationFinding> findings;
        await using (var ctx = NewContext())
        {
            var repo = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repo, syncRepository: new JIM.PostgresData.Repositories.SyncRepository(repo));
            var adapter = new ObjectMatchingPreviewAdapter(jim);
            var context = new PreviewContext
            {
                Surface = ConfigurationChangePreviewSurface.ObjectMatching,
                ActivityId = Guid.CreateVersion7(),
                TargetId = seeded.ConnectedSystemId,
                ProposedConfiguration = new ObjectMatchingProposal(ObjectMatchingRuleMode.ConnectedSystem,
                [
                    new ObjectMatchingRuleProposal(
                        Order: 0,
                        ConnectedSystemObjectTypeId: seeded.ConnectedSystemObjectTypeId,
                        SyncRuleId: null,
                        MetaverseObjectTypeId: seeded.MetaverseObjectTypeId,
                        TargetMetaverseAttributeId: seeded.MetaverseEmailAttributeId,
                        CaseSensitive: false,
                        Sources: [new ObjectMatchingRuleSourceProposal(0, seeded.MailAttributeId)])
                ])
            };

            // Act - the full surface a preview run exercises
            findings = await adapter.ValidateAsync(context);
            deltas = [];
            await foreach (var delta in adapter.EvaluateDeltasAsync(context, CancellationToken.None))
                deltas.Add(delta);
            counts = await adapter.CountImpactAsync(context);
        }

        // Assert - the swap is reported against the real matching query, and nothing anywhere has changed
        var after = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Blocking), Is.False,
                "the proposal is well formed; nothing about it should block");
            Assert.That(deltas, Has.Count.EqualTo(1),
                "only the unjoined object can move; the joined one never re-runs matching");
            Assert.That(deltas[0].TransitionType,
                Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinDifferentMetaverseObject));
            Assert.That(deltas[0].MetaverseObjectId, Is.EqualTo(seeded.GraceId),
                "the delta names the identity the account would end up on");
            Assert.That(counts.Sum(c => c.ObjectCount), Is.EqualTo(deltas.Count),
                "the counts are the same stream the drill-down reports");
            Assert.That(() => after.AssertUnchangedSince(before), Throws.Nothing,
                "a preview that asked the matching engine twice must not have joined anything");
        }
    }

    private sealed record SeededTopology(
        int ConnectedSystemId,
        int ConnectedSystemObjectTypeId,
        int MetaverseObjectTypeId,
        int MetaverseEmailAttributeId,
        int MailAttributeId,
        Guid GraceId);

    /// <summary>
    /// Seeds one Connected System matching in Simple mode on employeeID, two Metaverse Objects (one reachable by
    /// employee id, the other by email), one UNJOINED object that the proposal would move between them, and one
    /// JOINED object that no matching change can move.
    /// </summary>
    private async Task<SeededTopology> SeedMatchingTopologyAsync()
    {
        await using var seedCtx = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Object Matching Preview Test Connector", BuiltIn = false };
        var connectedSystem = new ConnectedSystem
        {
            Name = "Object Matching Preview Source",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem,
            ConnectorDefinition = connectorDefinition
        };
        seedCtx.ConnectorDefinitions.Add(connectorDefinition);
        seedCtx.ConnectedSystems.Add(connectedSystem);
        await seedCtx.SaveChangesAsync();

        var csoType = new ConnectedSystemObjectType
        {
            ConnectedSystemId = connectedSystem.Id,
            Name = "User",
            Selected = true,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Name = "employeeID", Type = AttributeDataType.Text, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Name = "mail", Type = AttributeDataType.Text, Selected = true }
            ]
        };
        seedCtx.ConnectedSystemObjectTypes.Add(csoType);

        var employeeIdAttribute = new MetaverseAttribute
        {
            Name = "Employee ID",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            BuiltIn = false
        };
        var emailAttribute = new MetaverseAttribute
        {
            Name = "Email",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            BuiltIn = false
        };
        var mvType = new MetaverseObjectType
        {
            Name = "Person",
            PluralName = "People",
            BuiltIn = false,
            Attributes = [employeeIdAttribute, emailAttribute]
        };
        seedCtx.MetaverseObjectTypes.Add(mvType);
        await seedCtx.SaveChangesAsync();

        var employeeIdSourceAttribute = csoType.Attributes.Single(a => a.Name == "employeeID");
        var mailSourceAttribute = csoType.Attributes.Single(a => a.Name == "mail");
        var externalIdAttribute = csoType.Attributes.Single(a => a.IsExternalId);

        var importRule = new SyncRule
        {
            ConnectedSystemId = connectedSystem.Id,
            Name = "Object Matching Preview Import",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystemObjectTypeId = csoType.Id,
            MetaverseObjectTypeId = mvType.Id,
            ProjectToMetaverse = true
        };
        seedCtx.SyncRules.Add(importRule);

        // The stored rule: match employeeID against Employee ID, in Simple mode on the object type.
        seedCtx.Add(new ObjectMatchingRule
        {
            Order = 0,
            ConnectedSystemObjectTypeId = csoType.Id,
            MetaverseObjectTypeId = mvType.Id,
            TargetMetaverseAttributeId = employeeIdAttribute.Id,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttributeId = employeeIdSourceAttribute.Id }]
        });
        await seedCtx.SaveChangesAsync();

        var ada = MetaverseObjectWith(mvType, employeeIdAttribute, "E1", emailAttribute, "ada@corp.local");
        var grace = MetaverseObjectWith(mvType, employeeIdAttribute, "E9", emailAttribute, "shared@corp.local");
        var joinedIdentity = MetaverseObjectWith(mvType, employeeIdAttribute, "E5", emailAttribute, "already@corp.local");
        seedCtx.MetaverseObjects.AddRange(ada, grace, joinedIdentity);

        // The unjoined account: matches Ada on employee id today, Grace on mail under the proposal.
        var unjoined = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            TypeId = csoType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined
        };
        unjoined.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = externalIdAttribute.Id, GuidValue = Guid.NewGuid() });
        unjoined.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = employeeIdSourceAttribute.Id, StringValue = "E1" });
        unjoined.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = mailSourceAttribute.Id, StringValue = "shared@corp.local" });
        seedCtx.ConnectedSystemObjects.Add(unjoined);

        // The joined account, carrying values that would match differently if it were ever re-matched. It is not.
        var joined = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            TypeId = csoType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            MetaverseObject = joinedIdentity
        };
        joined.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = externalIdAttribute.Id, GuidValue = Guid.NewGuid() });
        joined.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = employeeIdSourceAttribute.Id, StringValue = "E1" });
        joined.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = mailSourceAttribute.Id, StringValue = "shared@corp.local" });
        seedCtx.ConnectedSystemObjects.Add(joined);

        await seedCtx.SaveChangesAsync();

        return new SeededTopology(
            connectedSystem.Id,
            csoType.Id,
            mvType.Id,
            emailAttribute.Id,
            mailSourceAttribute.Id,
            grace.Id);
    }

    private static MetaverseObject MetaverseObjectWith(
        MetaverseObjectType type,
        MetaverseAttribute employeeIdAttribute,
        string employeeId,
        MetaverseAttribute emailAttribute,
        string email)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = type };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), Attribute = employeeIdAttribute, StringValue = employeeId });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), Attribute = emailAttribute, StringValue = email });
        return mvo;
    }
}
