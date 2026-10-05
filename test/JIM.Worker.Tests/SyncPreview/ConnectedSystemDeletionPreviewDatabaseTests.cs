// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers.Preview;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncPreview;

/// <summary>
/// Real-PostgreSQL verification of the Connected System deletion preview (#134) and of the recall paths' scope-exit
/// deprovisioning it previews. The in-memory provider tracks every navigation, enforces no keys and runs no digest
/// SQL, so only a real database can show that the preview leaves nothing behind, including on a tracking context
/// the caller goes on to save (the Worker's own shape), and that the real run's write ordering holds.
/// </summary>
/// <remarks>
/// The topology: an HR source projecting a person and contributing DisplayName and Description, and an Active
/// Directory target whose export rule is scoped on Description with a Delete Deprovisioning Action, so deleting HR
/// clears Description and takes the account out of scope. Opt-in via the <c>JIM_TEST_RESET_*</c> environment
/// variables; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemDeletionPreviewDatabaseTests
{
    private const string HrDescription = "HR Description";

    /// <summary>
    /// The synchronisation-integrity tables plus everything the real deletion writes on its way: the system's own row
    /// (its status and deprovisioning checkpoint), change history, outcome trees, deferred references, worker tasks and
    /// the rules themselves. The preview may write none of them.
    /// </summary>
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
        "PendingPasswordChanges",
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL deletion preview tests.");

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

    [TestCase(true, TestName = "DeletionPreviewAdapter_EveryStageOnATrackingContextSavedAfterwards_LeavesTheIntegrityTablesByteIdenticalAsync")]
    [TestCase(false, TestName = "DeletionPreviewAdapter_EveryStageOnANoTrackingContext_LeavesTheIntegrityTablesByteIdenticalAsync")]
    public async Task DeletionPreviewAdapter_EveryStage_LeavesTheIntegrityTablesByteIdenticalAsync(bool tracking)
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
            var adapter = new ConnectedSystemDeletionPreviewAdapter(jim);
            var context = new PreviewContext
            {
                Surface = ConfigurationChangePreviewSurface.ConnectedSystemDeletion,
                ActivityId = Guid.CreateVersion7(),
                TargetId = topology.HrSystemId,
                ProposedConfiguration = new ConnectedSystemDeletionProposal()
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
            Assert.That(estimate, Is.EqualTo(new PreviewCostEstimate(1, 2)), "one HR object, contributing DisplayName and Description");
            Assert.That(counts.Select(c => (c.TransitionType, c.ObjectCount)), Is.EquivalentTo(new[]
            {
                (ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor, 1),
                (ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport, 1)
            }));
            Assert.That(deltas.Where(d => d.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor).Select(d => d.AttributeName),
                Is.EquivalentTo(new[] { "DisplayName", "Description" }), "the preview must have found what the deletion would clear");
            Assert.That(deltas.Where(d => d.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport).Select(d => d.ConnectedSystemObjectId),
                Is.EqualTo(new Guid?[] { topology.TargetCsoId }), "and the account the recall takes out of export scope");
        }
    }

    [Test]
    public async Task ExecuteSynchronisedDeprovisioningAsync_RecallTakesObjectOutOfExportScope_StagesTheDeleteAndCompletesAsync()
    {
        var topology = await SeedAsync();

        await using (var ctx = NewContext())
        {
            var repo = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repo, syncRepository: new JIM.PostgresData.Repositories.SyncRepository(repo));
            var task = await FenceAndQueueAsync(ctx, topology.HrSystemId);
            await jim.ConnectedSystems.ExecuteSynchronisedDeprovisioningAsync(task);
        }

        await using var verify = NewContext(tracking: false);
        var stagedForTarget = await verify.PendingExports.Where(pe => pe.ConnectedSystemObjectId == topology.TargetCsoId).ToListAsync();
        var remainingValues = await verify.MetaverseObjectAttributeValues.Where(av => av.MetaverseObject.Id == topology.MvoId).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stagedForTarget.Select(pe => pe.ChangeType).ToList(), Is.EqualTo(new[] { PendingExportChangeType.Delete }),
                "the account left the export rule's scope, so exactly one Delete must be staged for it");
            Assert.That(remainingValues, Is.Empty, "HR's values have no other contributor and are recalled");
            Assert.That(await verify.ConnectedSystems.AnyAsync(cs => cs.Id == topology.HrSystemId), Is.False,
                "the run must reach its final step and delete the system");
        }
    }

    private sealed record Topology(int HrSystemId, Guid MvoId, Guid TargetCsoId);

    private async Task<Topology> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Deletion Preview Test Connector", BuiltIn = false };
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
                new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Name = "Description", Type = AttributeDataType.Text, Selected = true }
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
                new ConnectedSystemObjectTypeAttribute { Name = "Description", Type = AttributeDataType.Text, Selected = true }
            ]
        };
        seed.ConnectedSystemObjectTypes.AddRange(hrType, adType);

        var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = false };
        var mvDisplayName = new MetaverseAttribute { Name = "DisplayName", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, MetaverseObjectTypes = [mvType] };
        var mvDescription = new MetaverseAttribute { Name = "Description", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, MetaverseObjectTypes = [mvType] };
        seed.MetaverseObjectTypes.Add(mvType);
        seed.MetaverseAttributes.AddRange(mvDisplayName, mvDescription);
        await seed.SaveChangesAsync();

        var hrDisplayName = hrType.Attributes.Single(a => a.Name == "DisplayName");
        var hrDescription = hrType.Attributes.Single(a => a.Name == "Description");
        var importRule = new SyncRule
        {
            ConnectedSystemId = hr.Id,
            Name = "HR Import",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystemObjectTypeId = hrType.Id,
            MetaverseObjectTypeId = mvType.Id,
            ProjectToMetaverse = true
        };
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            TargetMetaverseAttribute = mvDisplayName,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrDisplayName } }
        });
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            TargetMetaverseAttribute = mvDescription,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrDescription } }
        });

        var adDescription = adType.Attributes.Single(a => a.Name == "Description");
        var exportRule = new SyncRule
        {
            ConnectedSystemId = ad.Id,
            Name = "AD Export",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemObjectTypeId = adType.Id,
            MetaverseObjectTypeId = mvType.Id,
            ProvisionToConnectedSystem = true,
            OutboundDeprovisionAction = OutboundDeprovisionAction.Delete
        };
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            TargetConnectedSystemAttribute = adDescription,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvDescription } }
        });
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = [new SyncRuleScopingCriteria { MetaverseAttribute = mvDescription, ComparisonType = SearchComparisonType.Equals, StringValue = HrDescription, CaseSensitive = true }]
        });
        seed.SyncRules.AddRange(importRule, exportRule);
        await seed.SaveChangesAsync();

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType, Origin = MetaverseObjectOrigin.Projected };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), Attribute = mvDisplayName, StringValue = "John Smith",
            ContributedBySystemId = hr.Id, ContributedBySyncRuleId = importRule.Id
        });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), Attribute = mvDescription, StringValue = HrDescription,
            ContributedBySystemId = hr.Id, ContributedBySyncRuleId = importRule.Id
        });
        seed.MetaverseObjects.Add(mvo);

        var hrCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = hr.Id, TypeId = hrType.Id, Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObject = mvo, JoinType = ConnectedSystemObjectJoinType.Projected, DateJoined = DateTime.UtcNow,
            ExternalIdAttributeId = hrType.Attributes.Single(a => a.IsExternalId).Id
        };
        hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = hrType.Attributes.Single(a => a.IsExternalId), GuidValue = Guid.NewGuid() });
        hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = hrDisplayName, StringValue = "John Smith" });
        hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = hrDescription, StringValue = HrDescription });

        var adCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = ad.Id, TypeId = adType.Id, Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObject = mvo, JoinType = ConnectedSystemObjectJoinType.Provisioned, DateJoined = DateTime.UtcNow,
            ExternalIdAttributeId = adType.Attributes.Single(a => a.IsExternalId).Id
        };
        adCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = adType.Attributes.Single(a => a.IsExternalId), GuidValue = Guid.NewGuid() });
        adCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = adDescription, StringValue = HrDescription });

        seed.ConnectedSystemObjects.AddRange(hrCso, adCso);
        await seed.SaveChangesAsync();

        return new Topology(hr.Id, mvo.Id, adCso.Id);
    }

    /// <summary>
    /// The state the queue step leaves: the system fenced, and a persisted Synchronised Deprovisioning task with its
    /// Activity, so the run's checkpoint updates have a row to update.
    /// </summary>
    private static async Task<DeleteConnectedSystemWorkerTask> FenceAndQueueAsync(JimDbContext ctx, int connectedSystemId)
    {
        var system = await ctx.ConnectedSystems.SingleAsync(cs => cs.Id == connectedSystemId);
        system.Status = ConnectedSystemStatus.Deleting;

        var activity = new Activity
        {
            TargetName = system.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Deprovision,
            Status = ActivityStatus.InProgress,
            Executed = DateTime.UtcNow,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "Test"
        };
        ctx.Activities.Add(activity);

        var task = new DeleteConnectedSystemWorkerTask(connectedSystemId, evaluateMvoDeletionRules: true, deleteChangeHistory: false, synchronisedDeprovisioning: true)
        {
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "Test",
            Activity = activity
        };
        ctx.DeleteConnectedSystemWorkerTasks.Add(task);
        await ctx.SaveChangesAsync();
        return task;
    }
}
