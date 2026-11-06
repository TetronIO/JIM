// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the two repository operations behind withdrawing queued Pending Export changes that
/// nothing authorises any more (run before every export): the candidate query
/// (<c>GetUpdatePendingExportsWithQueuedChangesPossiblyWithoutAuthorityAsync</c>, whose correlated subqueries against
/// Synchronisation Rules and Attribute Flows the in-memory repository cannot exercise) and the withdrawal itself
/// (<c>WithdrawPendingExportAttributeChangesAsync</c>, raw SQL).
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class QueuedChangeAuthorityDatabaseTests
{
    private static readonly ActivitySource TestSource = new("JIM.Tests.QueuedChangeAuthority");

    private string _connectionString = null!;
    private ActivityListener? _listener;
    private readonly ConcurrentQueue<System.Diagnostics.Activity> _stopped = new();

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL queued change authority tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("JIM.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _stopped.Enqueue
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _listener?.Dispose();

    [SetUp]
    public async Task SetUp() => await PostgresTestDatabase.ResetAsync(_connectionString);

    private sealed class Seed
    {
        public int ConnectedSystemId;
        public int ObjectClassAttributeId;
        public readonly Dictionary<string, Guid> PendingExportIds = new();
        public readonly Dictionary<string, Guid> ChangeIds = new();
    }

    /// <summary>
    /// One Connected System with an enabled export rule (mail flowed; displayName's Attribute Flow disabled) and a
    /// disabled one, and one Update Pending Export per case, each named for what it exercises.
    /// </summary>
    private async Task<Seed> SeedAsync()
    {
        await using var seed = NewContext();
        var result = new Seed();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        ConnectedSystemObjectTypeAttribute Attribute(string name)
        {
            var attribute = new ConnectedSystemObjectTypeAttribute
            {
                Name = name, ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued, Selected = true
            };
            csType.Attributes.Add(attribute);
            return attribute;
        }
        var mail = Attribute("mail");
        var displayName = Attribute("displayName");
        var objectClass = Attribute("objectClass");
        // The Connected System computes this type's class membership, as an LDAP directory does.
        csType.Tags.Add(new ConnectedSystemObjectTypeTag
        {
            ConnectedSystemObjectType = csType, Key = ObjectTypeTags.Keys.ClassMembershipAttribute, Value = "objectClass"
        });
        var mvType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        seed.AddRange(connectorDefinition, system, csType, mvType);
        await seed.SaveChangesAsync();

        var enabledRule = new SyncRule
        {
            Name = "Directory Export", ConnectedSystem = system, ConnectedSystemObjectType = csType, MetaverseObjectType = mvType,
            Direction = SyncRuleDirection.Export, Enabled = true
        };
        enabledRule.AttributeFlowRules.Add(new SyncRuleMapping { SyncRule = enabledRule, TargetConnectedSystemAttribute = mail, Enabled = true });
        enabledRule.AttributeFlowRules.Add(new SyncRuleMapping { SyncRule = enabledRule, TargetConnectedSystemAttribute = displayName, Enabled = false });
        var disabledRule = new SyncRule
        {
            Name = "Old Directory Export", ConnectedSystem = system, ConnectedSystemObjectType = csType, MetaverseObjectType = mvType,
            Direction = SyncRuleDirection.Export, Enabled = false
        };
        disabledRule.AttributeFlowRules.Add(new SyncRuleMapping { SyncRule = disabledRule, TargetConnectedSystemAttribute = mail, Enabled = true });
        seed.AddRange(enabledRule, disabledRule);
        await seed.SaveChangesAsync();

        void Add(string name, ConnectedSystemObjectTypeAttribute attribute, int? syncRuleId,
            bool joined = true,
            PendingExportChangeType changeType = PendingExportChangeType.Update,
            PendingExportStatus status = PendingExportStatus.Pending,
            PendingExportAttributeChangeStatus changeStatus = PendingExportAttributeChangeStatus.Pending)
        {
            var cso = new ConnectedSystemObject
            {
                Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
                MetaverseObject = joined ? new MetaverseObject { Type = mvType } : null
            };
            var change = new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(), AttributeId = attribute.Id, StringValue = "value", SyncRuleId = syncRuleId,
                ChangeType = PendingExportAttributeChangeType.Update, Status = changeStatus
            };
            var pe = new PendingExport
            {
                Id = Guid.NewGuid(), ConnectedSystemId = system.Id, ConnectedSystemObject = cso, ChangeType = changeType,
                Status = status, CreatedAt = DateTime.UtcNow, AttributeValueChanges = { change }
            };
            seed.AddRange(cso, pe);
            result.PendingExportIds[name] = pe.Id;
            result.ChangeIds[name] = change.Id;
        }

        // Candidates.
        Add("attributeFlowDisabled", displayName, enabledRule.Id);
        Add("ruleDisabled", mail, disabledRule.Id);
        Add("ruleDeleted", mail, syncRuleId: 999_999);
        Add("notJoined", mail, enabledRule.Id, joined: false);
        Add("resendOfAttributeFlowDisabled", displayName, enabledRule.Id, status: PendingExportStatus.ExportNotConfirmed,
            changeStatus: PendingExportAttributeChangeStatus.ExportedNotConfirmed);

        // Not candidates.
        Add("stillFlowed", mail, enabledRule.Id);
        Add("classMembership", objectClass, enabledRule.Id);
        Add("noAttribution", displayName, syncRuleId: null);
        Add("alreadySent", displayName, enabledRule.Id, status: PendingExportStatus.Exported,
            changeStatus: PendingExportAttributeChangeStatus.ExportedPendingConfirmation);
        Add("executing", displayName, enabledRule.Id, status: PendingExportStatus.Executing);
        Add("create", displayName, enabledRule.Id, changeType: PendingExportChangeType.Create);

        await seed.SaveChangesAsync();
        result.ConnectedSystemId = system.Id;
        result.ObjectClassAttributeId = objectClass.Id;
        return result;
    }

    [Test]
    public async Task GetUpdatePendingExportsWithQueuedChangesPossiblyWithoutAuthorityAsync_ReturnsOnlyTheCandidatesAsync()
    {
        var seed = await SeedAsync();

        await using var ctx = NewContext();
        var candidates = await new PostgresDataRepository(ctx).Sync
            .GetUpdatePendingExportsWithQueuedChangesPossiblyWithoutAuthorityAsync(seed.ConnectedSystemId, [seed.ObjectClassAttributeId]);

        var expected = new[] { "attributeFlowDisabled", "ruleDisabled", "ruleDeleted", "notJoined", "resendOfAttributeFlowDisabled" }
            .Select(name => seed.PendingExportIds[name]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidates.Select(pe => pe.Id), Is.EquivalentTo(expected));
            Assert.That(candidates.All(pe => pe.AttributeValueChanges.Count == 1), Is.True, "the attribute changes are loaded for the decision");
            Assert.That(candidates.Single(pe => pe.Id == seed.PendingExportIds["notJoined"]).ConnectedSystemObject?.MetaverseObjectId, Is.Null,
                "the Connected System Object is loaded so the join can be read");
        }
    }

    /// <summary>
    /// The withdrawal runs before every export, and a 5,000-leaver deprovisioning at Scale500k25kGroups spent 23 s
    /// somewhere in it with nothing to show where (#2669). It reports itself as a span carrying what it read and what it
    /// withdrew, with one child per step, so the next measurement says which step costs what.
    /// </summary>
    [Test]
    public async Task WithdrawQueuedChangesWithoutAuthorityAsync_ReportsSpanWithCountsAndOneChildPerStepAsync()
    {
        var seed = await SeedAsync();

        await using var ctx = NewContext();
        ctx.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        var repository = new PostgresDataRepository(ctx);
        using var jim = new JimApplication(repository, syncRepository: repository.Sync);

        ActivityTraceId traceId;
        (int ChangesWithdrawn, int PendingExportsDeleted) result;
        using (var root = TestSource.StartActivity("Test"))
        {
            Assert.That(root, Is.Not.Null, "the test's listener samples its own source");
            traceId = root!.TraceId;
            result = await jim.ExportExecution.WithdrawQueuedChangesWithoutAuthorityAsync(seed.ConnectedSystemId, "Directory");
        }
        var spans = _stopped.Where(s => s.TraceId == traceId).ToList();

        var withdraw = spans.Single(s => s.DisplayName == "WithdrawQueuedChanges");
        var children = spans.Where(s => s.ParentSpanId == withdraw.SpanId).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo((5, 5)), "every candidate holds one stale change, and is left empty");
            Assert.That(withdraw.GetTagItem("connectedSystemId"), Is.EqualTo(seed.ConnectedSystemId));
            Assert.That(withdraw.GetTagItem("candidatePendingExportCount"), Is.EqualTo(5), "the Pending Exports read");
            Assert.That(withdraw.GetTagItem("candidateChangeCount"), Is.EqualTo(5), "the attribute changes read with them");
            Assert.That(withdraw.GetTagItem("changesWithdrawn"), Is.EqualTo(5));
            Assert.That(withdraw.GetTagItem("pendingExportsDeleted"), Is.EqualTo(5));
            Assert.That(children.Select(s => s.DisplayName), Is.EquivalentTo(new[]
            {
                "WithdrawQueuedChanges.LoadSyncRules", "WithdrawQueuedChanges.FindCandidates",
                "WithdrawQueuedChanges.Select", "WithdrawQueuedChanges.Withdraw"
            }), "one child per step");
        }
    }

    /// <summary>
    /// With nothing to withdraw, the withdrawal still reports itself, with zero counts and no write.
    /// </summary>
    [Test]
    public async Task WithdrawQueuedChangesWithoutAuthorityAsync_NothingStale_ReportsSpanWithZeroCountsAndNoWriteAsync()
    {
        var seed = await SeedAsync();
        await using (var arrange = NewContext())
        {
            var stale = new[] { "attributeFlowDisabled", "ruleDisabled", "ruleDeleted", "notJoined", "resendOfAttributeFlowDisabled" }
                .Select(name => seed.PendingExportIds[name]).ToList();
            await arrange.PendingExports.Where(pe => stale.Contains(pe.Id)).ExecuteDeleteAsync();
        }

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        using var jim = new JimApplication(repository, syncRepository: repository.Sync);

        ActivityTraceId traceId;
        using (var root = TestSource.StartActivity("Test"))
        {
            traceId = root!.TraceId;
            await jim.ExportExecution.WithdrawQueuedChangesWithoutAuthorityAsync(seed.ConnectedSystemId);
        }
        var spans = _stopped.Where(s => s.TraceId == traceId).ToList();

        var withdraw = spans.Single(s => s.DisplayName == "WithdrawQueuedChanges");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(withdraw.GetTagItem("candidatePendingExportCount"), Is.EqualTo(0));
            Assert.That(withdraw.GetTagItem("changesWithdrawn"), Is.EqualTo(0));
            Assert.That(spans.Any(s => s.DisplayName == "WithdrawQueuedChanges.Withdraw"), Is.False, "nothing to write");
        }
    }

    [Test]
    public async Task WithdrawPendingExportAttributeChangesAsync_DeletesTheChangesAndOnlyTheUpdateExportsLeftEmptyAsync()
    {
        var seed = await SeedAsync();

        // A second change on one export, so withdrawing the first leaves it non-empty.
        await using (var arrange = NewContext())
        {
            arrange.PendingExportAttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(), PendingExportId = seed.PendingExportIds["ruleDisabled"],
                AttributeId = seed.ObjectClassAttributeId, StringValue = "person", ChangeType = PendingExportAttributeChangeType.Add,
                Status = PendingExportAttributeChangeStatus.Pending
            });
            await arrange.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var (changesWithdrawn, pendingExportsDeleted) = await new PostgresDataRepository(ctx).Sync.WithdrawPendingExportAttributeChangesAsync(
            [seed.ChangeIds["attributeFlowDisabled"], seed.ChangeIds["ruleDisabled"], seed.ChangeIds["executing"], seed.ChangeIds["create"]]);

        await using var verify = NewContext();
        var remainingExportIds = await verify.PendingExports.Select(pe => pe.Id).ToListAsync();
        var remainingChangeIds = await verify.PendingExportAttributeValueChanges.Select(c => c.Id).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changesWithdrawn, Is.EqualTo(4));
            Assert.That(pendingExportsDeleted, Is.EqualTo(1), "only the emptied, non-executing Update export is deleted");
            Assert.That(remainingExportIds, Has.No.Member(seed.PendingExportIds["attributeFlowDisabled"]));
            Assert.That(remainingExportIds, Has.Member(seed.PendingExportIds["ruleDisabled"]), "it still carries a change");
            Assert.That(remainingExportIds, Has.Member(seed.PendingExportIds["executing"]), "a connector owns an executing export");
            Assert.That(remainingExportIds, Has.Member(seed.PendingExportIds["create"]), "a Create is not an Update");
            Assert.That(remainingChangeIds, Has.No.Member(seed.ChangeIds["attributeFlowDisabled"]));
            Assert.That(remainingChangeIds, Has.Member(seed.ChangeIds["stillFlowed"]));
        }
    }
}
