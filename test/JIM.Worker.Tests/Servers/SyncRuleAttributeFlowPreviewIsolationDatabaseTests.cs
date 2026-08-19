// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
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
/// Real-PostgreSQL proof of the G2 adapter's never-persists invariant (#1437): an Attribute Flow preview run over a
/// live database leaves every synchronisation-integrity table byte-identical.
///
/// The proof matters most on this adapter of all of them. It runs the synchronisation preview engine TWICE over
/// every object it evaluates, and the second run carries an unsaved Synchronisation Rule substituted for a stored
/// one. Attribute Flow is the part of the engine that writes: it mutates a working Metaverse Object, stages its
/// changes, and hands them to the outbound evaluation. "The preview applied the values it was only asked to
/// imagine" is therefore the concrete failure mode here, and it is one the mocked unit fixture structurally cannot
/// detect because its repositories have nothing to write to.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleAttributeFlowPreviewIsolationDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Attribute Flow preview isolation tests.");

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
    public async Task EvaluateDeltasAsync_MappingRetargetedOverLiveDatabase_ReportsTheValueChangeAndPersistsNothingAsync()
    {
        // Arrange - an import rule flowing mail into the Metaverse's Email attribute, and one joined object whose
        // Metaverse Object already holds the value that mapping produces
        var (ruleId, alternateAttributeId) = await SeedImportTopologyAsync();

        var before = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        List<PreviewDelta> deltas;
        List<PreviewImpactCount> counts;
        await using (var ctx = NewContext())
        {
            var repo = new PostgresDataRepository(ctx);

            // The sync repository is what the preview engine runs against, inside a rollback-only transaction; this
            // adapter is on that path for every object it evaluates rather than only for arrivals.
            using var jim = new JimApplication(repo, syncRepository: new JIM.PostgresData.Repositories.SyncRepository(repo));
            var adapter = new SyncRuleAttributeFlowPreviewAdapter(jim, new SyncEngine());
            var context = new PreviewContext
            {
                Surface = ConfigurationChangePreviewSurface.SynchronisationRuleAttributeFlow,
                ActivityId = Guid.CreateVersion7(),
                TargetId = ruleId,

                // Retargeted from the mail attribute to the alternate one, so the Metaverse Object's Email would be
                // rewritten: the engine mutates a working copy, stages the change, and must roll all of it back.
                ProposedConfiguration = new SyncRuleAttributeFlowProposal(
                [
                    new SyncRuleMappingProposal(
                        TargetMetaverseAttributeId: MetaverseEmailAttributeId,
                        TargetConnectedSystemAttributeId: null,
                        Sources: [new SyncRuleMappingSourceProposal(1, null, alternateAttributeId)])
                ])
            };

            // Act - the full evaluation surface a preview run exercises
            deltas = [];
            await foreach (var delta in adapter.EvaluateDeltasAsync(context, CancellationToken.None))
                deltas.Add(delta);
            counts = await adapter.CountImpactAsync(context);
        }

        // Assert - the rewrite is reported, and nothing anywhere has changed
        var after = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas.Any(d => d.AttributeName == "Email"
                && d.OldValue == "ada@corp.local"
                && d.NewValue == "ada.lovelace@corp.local"), Is.True,
                "the retargeted mapping rewrites the Metaverse Object's Email");
            Assert.That(counts.Sum(c => c.ObjectCount), Is.EqualTo(deltas.Count),
                "the counts are the same stream the drill-down reports");
            Assert.That(() => after.AssertUnchangedSince(before), Throws.Nothing,
                "a preview that flowed values onto a working Metaverse Object must not have written them");
        }
    }

    /// <summary>
    /// The id the seeded Metaverse Email attribute is given, captured so the proposal can name it.
    /// </summary>
    private int MetaverseEmailAttributeId { get; set; }

    /// <summary>
    /// Seeds one Connected System with an import Synchronisation Rule flowing its mail attribute into a Metaverse
    /// Email attribute, and one JOINED object whose Metaverse Object already holds exactly what that mapping
    /// produces (so the stored configuration would change nothing and the proposal's rewrite is the only delta).
    /// Returns the rule and the alternate source attribute's id.
    /// </summary>
    private async Task<(int RuleId, int AlternateAttributeId)> SeedImportTopologyAsync()
    {
        await using var seedCtx = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "G2 Preview Test Connector", BuiltIn = false };
        var connectedSystem = new ConnectedSystem
        {
            Name = "G2 Preview Source",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule,
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
                new ConnectedSystemObjectTypeAttribute { Name = "mail", Type = AttributeDataType.Text, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Name = "altMail", Type = AttributeDataType.Text, Selected = true }
            ]
        };
        seedCtx.ConnectedSystemObjectTypes.Add(csoType);

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
            Attributes = [emailAttribute]
        };
        seedCtx.MetaverseObjectTypes.Add(mvType);
        await seedCtx.SaveChangesAsync();
        MetaverseEmailAttributeId = emailAttribute.Id;

        var mailAttribute = csoType.Attributes.Single(a => a.Name == "mail");
        var altMailAttribute = csoType.Attributes.Single(a => a.Name == "altMail");
        var externalIdAttribute = csoType.Attributes.Single(a => a.IsExternalId);

        var importRule = new SyncRule
        {
            ConnectedSystemId = connectedSystem.Id,
            Name = "G2 Preview Import",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystemObjectTypeId = csoType.Id,
            MetaverseObjectTypeId = mvType.Id,
            ProjectToMetaverse = true
        };
        var mapping = new SyncRuleMapping
        {
            TargetMetaverseAttribute = emailAttribute
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 1, ConnectedSystemAttribute = mailAttribute });
        importRule.AttributeFlowRules.Add(mapping);
        seedCtx.SyncRules.Add(importRule);
        await seedCtx.SaveChangesAsync();

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = emailAttribute,
            StringValue = "ada@corp.local",
            ContributedBySystemId = connectedSystem.Id,
            ContributedBySyncRuleId = importRule.Id
        });
        seedCtx.MetaverseObjects.Add(mvo);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            TypeId = csoType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            MetaverseObject = mvo
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = externalIdAttribute.Id,
            GuidValue = Guid.NewGuid()
        });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = mailAttribute.Id,
            StringValue = "ada@corp.local"
        });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = altMailAttribute.Id,
            StringValue = "ada.lovelace@corp.local"
        });
        seedCtx.ConnectedSystemObjects.Add(cso);
        await seedCtx.SaveChangesAsync();

        return (importRule.Id, altMailAttribute.Id);
    }
}
