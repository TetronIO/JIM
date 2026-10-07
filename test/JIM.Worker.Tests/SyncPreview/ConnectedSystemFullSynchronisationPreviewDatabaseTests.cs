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
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncPreview;

/// <summary>
/// Real-PostgreSQL verification of the Full Synchronisation preview adapter (#1530). The walk tears obsolete objects down
/// through the run's own obsoletion core, which breaks joins and recalls values by mutating what it is handed; the
/// in-memory provider tracks every navigation and enforces no keys, so only a real database can show that the preview
/// leaves nothing behind, including on a tracking context the caller goes on to save (the Worker's own shape).
/// </summary>
/// <remarks>
/// The topology: an HR source whose object for John Smith an import has marked obsolete, and an Active Directory account
/// provisioned for him under a Delete Deprovisioning Action. John's type is deleted when HR, its authoritative source,
/// disconnects, so the Full Synchronisation would delete him and deprovision the account. A second HR object, Jane Doe,
/// is joined and unchanged. Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemFullSynchronisationPreviewDatabaseTests
{
    private static readonly IReadOnlyList<string> WatchedTables =
    [
        .. DatabaseIsolationSnapshot.SyncIntegrityTables,
        "ConnectedSystems",
        "SyncRules",
        "MetaverseObjectChanges",
        "MetaverseObjectChangeAttributeValues",
        "ConnectedSystemObjectChanges",
        "ActivityRunProfileExecutionItemSyncOutcomes",
        "CausalEdges",
        "DeferredReferences",
        "WorkerTasks"
    ];

    private string _connectionString = null!;

    private JimDbContext NewContext(bool tracking = true)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        if (!tracking)
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Full Synchronisation preview tests.");

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
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    [TestCase(true, TestName = "FullSynchronisationPreviewAdapter_EveryStageOnATrackingContextSavedAfterwards_LeavesTheIntegrityTablesByteIdenticalAsync")]
    [TestCase(false, TestName = "FullSynchronisationPreviewAdapter_EveryStageOnANoTrackingContext_LeavesTheIntegrityTablesByteIdenticalAsync")]
    public async Task FullSynchronisationPreviewAdapter_EveryStage_LeavesTheIntegrityTablesByteIdenticalAsync(bool tracking)
    {
        var topology = await SeedAsync();
        var before = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString, WatchedTables);

        List<PreviewValidationFinding> findings;
        PreviewCostEstimate estimate;
        List<PreviewImpactCount> counts;
        var deltas = new List<PreviewDelta>();
        await using (var ctx = NewContext(tracking))
        {
            var repo = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repo, syncRepository: new JIM.PostgresData.Repositories.SyncRepository(repo));
            var adapter = new ConnectedSystemFullSynchronisationPreviewAdapter(jim);
            var context = new PreviewContext
            {
                Surface = ConfigurationChangePreviewSurface.ConnectedSystemFullSynchronisation,
                ActivityId = Guid.CreateVersion7(),
                TargetId = topology.HrSystemId,
                ProposedConfiguration = new ConnectedSystemFullSynchronisationProposal()
            };

            // Every stage, in the framework's order, on one context: the framework saves its progress and results on
            // that context between them, and on the Worker the context tracks. Nothing a stage touched may ride along.
            findings = await adapter.ValidateAsync(context);
            estimate = await adapter.EstimateCostAsync(context);
            counts = await adapter.CountImpactAsync(context);
            await foreach (var delta in adapter.EvaluateDeltasAsync(context, CancellationToken.None))
                deltas.Add(delta);

            await ctx.SaveChangesAsync();
        }

        var after = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString, WatchedTables);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => after.AssertUnchangedSince(before), Throws.Nothing);
            Assert.That(findings, Is.Empty);
            Assert.That(estimate.AffectedObjects, Is.EqualTo(2), "John Smith and Jane Doe");
            Assert.That(counts.Select(c => (c.TransitionType, c.ObjectCount)), Is.EquivalentTo(new[]
            {
                (ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject, 1),
                (ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible, 1),
                (ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport, 1),
                (ActivityRunProfileExecutionItemSyncOutcomeType.WouldNotChange, 1)
            }), "John Smith is torn down and his account deprovisioned; Jane Doe would not change");
            Assert.That(deltas.Single(d => d.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport).ConnectedSystemObjectId,
                Is.EqualTo(topology.TargetCsoId), "the account deprovisioned");
            Assert.That(deltas.Single(d => d.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible).ObjectDisplayName,
                Is.EqualTo("John Smith"), "named as the administrator knows him");
        }
    }

    private sealed record Topology(int HrSystemId, Guid TargetCsoId);

    private async Task<Topology> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Full Synchronisation Preview Test Connector", BuiltIn = false };
        var hr = new ConnectedSystem { Name = "HR Source", ConnectorDefinition = connectorDefinition };
        var ad = new ConnectedSystem { Name = "AD Target", ConnectorDefinition = connectorDefinition };
        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.ConnectedSystems.AddRange(hr, ad);
        await seed.SaveChangesAsync();

        var hrType = new ConnectedSystemObjectType
        {
            ConnectedSystemId = hr.Id,
            Name = "HrUser",
            Selected = true,
            RemoveContributedAttributesOnObsoletion = true,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true }
            ]
        };
        var adType = new ConnectedSystemObjectType
        {
            ConnectedSystemId = ad.Id,
            Name = "TargetUser",
            Selected = true,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true }
            ]
        };
        seed.ConnectedSystemObjectTypes.AddRange(hrType, adType);
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
        {
            Name = "Full Synchronisation", ConnectedSystemId = hr.Id, RunType = ConnectedSystemRunType.FullSynchronisation
        });

        var mvType = new MetaverseObjectType
        {
            Name = "Person", PluralName = "People", BuiltIn = false,
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            DeletionTriggerConnectedSystemIds = [hr.Id]
        };
        var mvDisplayName = new MetaverseAttribute
        {
            Name = Constants.BuiltInAttributes.DisplayName, Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = [mvType]
        };
        seed.MetaverseObjectTypes.Add(mvType);
        seed.MetaverseAttributes.Add(mvDisplayName);
        await seed.SaveChangesAsync();

        var hrDisplayName = hrType.Attributes.Single(a => a.Name == "DisplayName");
        var importRule = new SyncRule
        {
            ConnectedSystemId = hr.Id, Name = "HR Import", Direction = SyncRuleDirection.Import, Enabled = true,
            ConnectedSystemObjectTypeId = hrType.Id, MetaverseObjectTypeId = mvType.Id, ProjectToMetaverse = true
        };
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            TargetMetaverseAttribute = mvDisplayName,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrDisplayName } }
        });

        var adDisplayName = adType.Attributes.Single(a => a.Name == "DisplayName");
        var exportRule = new SyncRule
        {
            ConnectedSystemId = ad.Id, Name = "AD Export", Direction = SyncRuleDirection.Export, Enabled = true,
            ConnectedSystemObjectTypeId = adType.Id, MetaverseObjectTypeId = mvType.Id, ProvisionToConnectedSystem = true,
            OutboundDeprovisionAction = OutboundDeprovisionAction.Delete
        };
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            TargetConnectedSystemAttribute = adDisplayName,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvDisplayName } }
        });
        seed.SyncRules.AddRange(importRule, exportRule);
        await seed.SaveChangesAsync();

        // John Smith, whose HR object an import has marked obsolete, and whose account was provisioned and confirmed.
        var john = Person(mvType, mvDisplayName, "John Smith", hr.Id, importRule.Id);
        var johnHr = HrObject(hr.Id, hrType, john, "John Smith", ConnectedSystemObjectStatus.Obsolete);
        var johnAd = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = ad.Id, TypeId = adType.Id, Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObject = john, JoinType = ConnectedSystemObjectJoinType.Provisioned, DateJoined = DateTime.UtcNow,
            ExternalIdAttributeId = adType.Attributes.Single(a => a.IsExternalId).Id
        };
        johnAd.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = adType.Attributes.Single(a => a.IsExternalId), GuidValue = Guid.NewGuid() });
        johnAd.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = adDisplayName, StringValue = "John Smith" });

        // Jane Doe, joined and unchanged, with nothing to provision (her account is not this test's subject).
        var jane = Person(mvType, mvDisplayName, "Jane Doe", hr.Id, importRule.Id);
        var janeHr = HrObject(hr.Id, hrType, jane, "Jane Doe", ConnectedSystemObjectStatus.Normal);
        var janeAd = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = ad.Id, TypeId = adType.Id, Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObject = jane, JoinType = ConnectedSystemObjectJoinType.Provisioned, DateJoined = DateTime.UtcNow,
            ExternalIdAttributeId = adType.Attributes.Single(a => a.IsExternalId).Id
        };
        janeAd.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = adType.Attributes.Single(a => a.IsExternalId), GuidValue = Guid.NewGuid() });
        janeAd.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = adDisplayName, StringValue = "Jane Doe" });

        seed.MetaverseObjects.AddRange(john, jane);
        seed.ConnectedSystemObjects.AddRange(johnHr, johnAd, janeHr, janeAd);
        await seed.SaveChangesAsync();

        return new Topology(hr.Id, johnAd.Id);
    }

    private static MetaverseObject Person(MetaverseObjectType type, MetaverseAttribute displayName, string name, int hrSystemId, int importRuleId)
    {
        var person = new MetaverseObject { Id = Guid.NewGuid(), Type = type, Origin = MetaverseObjectOrigin.Projected };
        person.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), Attribute = displayName, StringValue = name,
            ContributedBySystemId = hrSystemId, ContributedBySyncRuleId = importRuleId
        });
        return person;
    }

    private static ConnectedSystemObject HrObject(int hrSystemId, ConnectedSystemObjectType hrType, MetaverseObject person, string name,
        ConnectedSystemObjectStatus status)
    {
        var hrObject = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = hrSystemId, TypeId = hrType.Id, Status = status,
            MetaverseObject = person, JoinType = ConnectedSystemObjectJoinType.Projected, DateJoined = DateTime.UtcNow,
            ExternalIdAttributeId = hrType.Attributes.Single(a => a.IsExternalId).Id
        };
        hrObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = hrType.Attributes.Single(a => a.IsExternalId), GuidValue = Guid.NewGuid() });
        hrObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = hrType.Attributes.Single(a => a.Name == "DisplayName"), StringValue = name });
        return hrObject;
    }
}
