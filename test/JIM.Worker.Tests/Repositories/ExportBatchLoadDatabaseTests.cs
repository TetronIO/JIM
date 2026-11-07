// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using JIM.Application;
using JIM.Connectors.LDAP;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL coverage of how export execution selects and loads its batches (#2813). The selection reads summaries
/// alone, through the eligibility predicate and keyset cursor the counts and probes share; the batch's exports are then
/// loaded by id through the one load path every execution path uses. The in-memory provider cannot see what these
/// guard: the SQL the selection runs, and what the load's Include chain leaves out (the LDAP Connector's placeholder
/// check reads each member value's attribute, which only a real untracked load can fail to populate).
/// Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent. <c>SetUp</c> TRUNCATEs every table.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ExportBatchLoadDatabaseTests
{
    private const string PlaceholderDn = "cn=placeholder";
    private static readonly DateTime T0 = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

    private static readonly ActivitySource TestSource = new("JIM.Tests.ExportBatchLoad");

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

    /// <summary>
    /// A context tracking by default, as the worker's contexts are (JIM.Worker's Program.cs).
    /// </summary>
    private JimDbContext NewWorkerContext()
    {
        var context = NewContext();
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        return context;
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL export batch load tests.");

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
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    // ─── Selection ───

    /// <summary>
    /// The summary selection pages exactly the exports, in exactly the order, the full-graph selection it replaced
    /// did: every executable export of the Connected System and nothing else, in (CreatedAt, Id) order, a page at a
    /// time from the keyset cursor, across a timestamp tie, with the Run Profile Safeguards' exclusions applied. The
    /// seeded set holds one of each kind of export eligibility turns away (already exported, not exportable, another
    /// Connected System's, mid-execution), deferred exports among the eligible, and an Update eligible only by a change
    /// sent and not confirmed.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task GetExecutableExportBatchSummaryAsync_MixedQueue_PagesTheEligibleExportsInQueueOrderAsync(bool excludeDeletes)
    {
        var s = await SeedSchemaAsync();
        var otherSystemId = await SeedOtherSystemAsync();
        var seeded = new List<SeededExport>();
        var at = T0;
        DateTime Next() => at = at.AddSeconds(1);

        seeded.Add(await SeedExportAsync(s.SystemId, PendingExportChangeType.Create, PendingExportStatus.Pending, Next()));
        seeded.Add(await SeedExportAsync(s.SystemId, PendingExportChangeType.Create, PendingExportStatus.Exported, Next()));
        seeded.Add(await SeedExportAsync(s.SystemId, PendingExportChangeType.Delete, PendingExportStatus.Exported, Next()));
        seeded.Add(await SeedExportAsync(s.SystemId, PendingExportChangeType.Delete, PendingExportStatus.Pending, Next()));
        seeded.Add(await SeedExportAsync(s.SystemId, PendingExportChangeType.Update, PendingExportStatus.Pending, Next(),
            changes: [(s.MemberAttributeId, PendingExportAttributeChangeStatus.Pending)]));
        seeded.Add(await SeedExportAsync(s.SystemId, PendingExportChangeType.Update, PendingExportStatus.Exported, Next(),
            changes: [(s.MemberAttributeId, PendingExportAttributeChangeStatus.ExportedPendingConfirmation)]));
        seeded.Add(await SeedExportAsync(s.SystemId, PendingExportChangeType.Update, PendingExportStatus.ExportNotConfirmed, Next(),
            changes: [(s.MemberAttributeId, PendingExportAttributeChangeStatus.ExportedPendingConfirmation),
                      (s.MemberAttributeId, PendingExportAttributeChangeStatus.ExportedNotConfirmed)]));
        seeded.Add(await SeedExportAsync(s.SystemId, PendingExportChangeType.Create, PendingExportStatus.Pending, Next(), hasUnresolvedReferences: true));
        seeded.Add(await SeedExportAsync(s.SystemId, PendingExportChangeType.Create, PendingExportStatus.Executing, Next()));
        seeded.Add(await SeedExportAsync(otherSystemId, PendingExportChangeType.Create, PendingExportStatus.Pending, Next()));
        seeded.Add(await SeedExportAsync(s.SystemId, PendingExportChangeType.Update, PendingExportStatus.Pending, Next()));
        var tie = Next();
        for (var i = 0; i < 4; i++)
            seeded.Add(await SeedExportAsync(s.SystemId, PendingExportChangeType.Create, PendingExportStatus.Pending, tie));
        seeded.Add(await SeedExportAsync(s.SystemId, PendingExportChangeType.Update, PendingExportStatus.ExportNotConfirmed, Next(),
            hasUnresolvedReferences: true, changes: [(s.MemberAttributeId, PendingExportAttributeChangeStatus.Pending)]));
        seeded.Add(await SeedExportAsync(s.SystemId, PendingExportChangeType.Delete, PendingExportStatus.Pending, Next()));
        seeded.Add(await SeedExportAsync(s.SystemId, PendingExportChangeType.Create, PendingExportStatus.Parked, Next()));

        IReadOnlyCollection<PendingExportChangeType>? excluded = excludeDeletes ? [PendingExportChangeType.Delete] : null;
        var expected = seeded
            .Where(pe => pe.ConnectedSystemId == s.SystemId && IsExecutable(pe) && (excluded == null || !excluded.Contains(pe.ChangeType)))
            .OrderBy(pe => pe.CreatedAt).ThenBy(pe => pe.Id)
            .ToList();
        Assert.That(expected, Has.Count.GreaterThan(6), "sanity check: the queue spans several pages");

        await using var ctx = NewWorkerContext();
        var repository = new PostgresDataRepository(ctx).Sync;
        var paged = new List<ExecutableExportSummary>();
        DateTime? cursorCreatedAt = null;
        Guid? cursorId = null;
        for (var page = 0; page < 20; page++)
        {
            var batch = await repository.GetExecutableExportBatchSummaryAsync(s.SystemId, take: 3, cursorCreatedAt, cursorId, excluded);
            if (batch.Count == 0)
                break;
            Assert.That(batch, Has.Count.LessThanOrEqualTo(3), "a page never holds more than it was asked for");
            paged.AddRange(batch);
            (cursorCreatedAt, cursorId) = (batch[^1].CreatedAt, batch[^1].Id);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(paged.Select(pe => pe.Id), Is.EqualTo(expected.Select(pe => pe.Id)),
                "every eligible export exactly once, in (CreatedAt, Id) order, and nothing else");
            Assert.That(paged.Select(pe => (pe.ChangeType, pe.Status, pe.HasUnresolvedReferences, pe.CreatedAt)),
                Is.EqualTo(expected.Select(pe => (pe.ChangeType, pe.Status, pe.HasUnresolvedReferences, pe.CreatedAt))),
                "each summary carries what the selection decides from, and the cursor exactly as stored");
            Assert.That(ctx.ChangeTracker.Entries().Any(), Is.False, "the selection materialises no entity at all");
        }
    }

    /// <summary>
    /// The selection reads the Pending Export rows and, for an Update, whether it has a change to export; never the
    /// export's object or any of its values. The full-graph read it replaced loaded every member value of every group in
    /// the batch, for the parallel path to throw away (11 s of a 500k-user leaver run's export).
    /// </summary>
    [Test]
    public async Task ExecutableExportBatchSummaryQuery_ReadsNoObjectOrObjectValueAsync()
    {
        await using var ctx = NewContext();
        var repository = (ConnectedSystemRepository)new PostgresDataRepository(ctx).ConnectedSystems;
        var sql = repository.ExecutableExportBatchSummaryQuery(1, take: 100, T0, Guid.NewGuid(), [PendingExportChangeType.Delete]).ToQueryString();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sql, Does.Not.Contain("\"ConnectedSystemObjects\""), $"the selection reads no object. SQL:\n{sql}");
            Assert.That(sql, Does.Not.Contain("\"ConnectedSystemObjectAttributeValues\""), $"nor any object value. SQL:\n{sql}");
            Assert.That(sql, Does.Not.Contain("\"ConnectedSystemObjectTypeAttributes\""), $"nor any attribute. SQL:\n{sql}");
        }
    }

    // ─── The one load path, as the LDAP Connector reads it ───

    /// <summary>
    /// Removing one member of a populated groupOfNames, loaded through the one load path on a worker's (tracking)
    /// context, leaves the group with members, so no placeholder member is added. The LDAP Connector counts the
    /// group's current members by each value's attribute name: a load leaving the values' attributes unset counts no
    /// members, and adds the placeholder to a group that does not need it.
    /// </summary>
    [Test]
    public async Task GetPendingExportsByIdsAsync_PartialGroupMemberRemoval_LdapConnectorAddsNoPlaceholderAsync()
    {
        var s = await SeedSchemaAsync();
        var members = Enumerable.Range(1, 3).Select(i => $"cn=user{i},ou=people,dc=glitterband,dc=local").ToArray();
        var (exportId, groupDn) = await SeedGroupMemberRemovalAsync(s, members, members[0]);

        await using var ctx = NewWorkerContext();
        var batch = await new PostgresDataRepository(ctx).Sync.GetPendingExportsByIdsAsync([exportId]);

        var modifications = BuildModifications(batch.Single(), groupDn);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(modifications.Any(m => m.Operation == DirectoryAttributeOperation.Delete && m.Values.Contains(members[0])), Is.True,
                "the removed member is deleted from the group");
            Assert.That(modifications.Any(m => m.Operation == DirectoryAttributeOperation.Add), Is.False,
                "the group keeps two members, so no placeholder member is added");
            Assert.That(ctx.ChangeTracker.Entries().Any(), Is.False, "the batch is loaded untracked, on a tracking context too");
        }
    }

    /// <summary>
    /// Removing a groupOfNames' last member, loaded through the one load path, still adds the placeholder member the
    /// object class requires.
    /// </summary>
    [Test]
    public async Task GetPendingExportsByIdsAsync_LastGroupMemberRemoval_LdapConnectorAddsThePlaceholderAsync()
    {
        var s = await SeedSchemaAsync();
        const string onlyMember = "cn=user1,ou=people,dc=glitterband,dc=local";
        var (exportId, groupDn) = await SeedGroupMemberRemovalAsync(s, [onlyMember], onlyMember);

        await using var ctx = NewWorkerContext();
        var batch = await new PostgresDataRepository(ctx).Sync.GetPendingExportsByIdsAsync([exportId]);

        var modifications = BuildModifications(batch.Single(), groupDn);
        Assert.That(modifications.Any(m => m.Operation == DirectoryAttributeOperation.Add && m.Values.Contains(PlaceholderDn)), Is.True,
            "the group would be left with no member, so the placeholder member is added");
    }

    // ─── Execution on the parallel path ───

    /// <summary>
    /// Parallel batches, each loaded untracked on a worker's own (tracking) context, run end to end against PostgreSQL:
    /// the Connector is handed each object's values with their attributes, optimistic apply records what was written
    /// (the replaced value deleted, the new one inserted) through raw SQL with nothing tracked to detach, and the
    /// in-memory object the execution item carries reflects it (D10). The run reports the load and each part of the
    /// apply as spans, so a slow batch says where its time went (#2813).
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_ParallelBatchesLoadedUntracked_ApplyOptimisticallyAndReportEachPartAsync()
    {
        var s = await SeedSchemaAsync();
        var groups = new List<(Guid ObjectId, Guid ExportId)>();
        for (var i = 0; i < 3; i++)
            groups.Add(await SeedMailReplacementAsync(s, T0.AddSeconds(i), $"old{i}@glitterband.local", $"new{i}@glitterband.local"));

        var handedOver = new ConcurrentBag<PendingExport>();
        Mock<IConnector> CreateConnector()
        {
            var connector = new Mock<IConnector>();
            connector.Setup(c => c.Name).Returns("Recording Connector");
            connector.As<IConnectorExportUsingCalls>()
                .Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
                .ReturnsAsync((IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
                {
                    foreach (var export in exports)
                        handedOver.Add(export);
                    return exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList();
                });
            return connector;
        }

        await using var callerContext = NewWorkerContext();
        var callerRepository = new PostgresDataRepository(callerContext);
        using var jim = new JimApplication(callerRepository, syncRepository: callerRepository.Sync);
        var connectedSystem = (await jim.ConnectedSystems.GetConnectedSystemAsync(s.SystemId))!;

        ExportExecutionResult result;
        ActivityTraceId traceId;
        using (var root = TestSource.StartActivity("Test"))
        {
            Assert.That(root, Is.Not.Null, "the test's listener samples its own source");
            traceId = root!.TraceId;
            result = await jim.ExportExecution.ExecuteExportsAsync(connectedSystem, CreateConnector().Object, SyncRunMode.PreviewAndSync,
                new ExportExecutionOptions { BatchSize = 1, MaxParallelism = 2 }, CancellationToken.None,
                connectorFactory: () => CreateConnector().Object,
                repositoryFactory: () =>
                {
                    var batchRepository = new PostgresDataRepository(NewWorkerContext());
                    return new SyncRepositoryScope(batchRepository.Sync, batchRepository);
                });
        }

        await using var verify = NewContext();
        var mailValues = await verify.ConnectedSystemObjectAttributeValues
            .Where(av => av.AttributeId == s.MailAttributeId)
            .Select(av => new { ObjectId = EF.Property<Guid>(av, "ConnectedSystemObjectId"), av.StringValue })
            .ToListAsync();
        var spans = _stopped.Where(span => span.TraceId == traceId).ToList();
        var applies = spans.Where(span => span.DisplayName == "OptimisticApply").ToList();
        var applyParts = spans.Where(span => applies.Any(apply => apply.SpanId == span.ParentSpanId)).Select(span => span.DisplayName).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SuccessCount, Is.EqualTo(3));
            Assert.That(result.OptimisticApplyAppliedCount, Is.EqualTo(3));
            Assert.That(result.OptimisticApplyFailedCount, Is.Zero);
            Assert.That(handedOver.SelectMany(pe => pe.ConnectedSystemObject!.AttributeValues).All(av => av.Attribute?.Name != null), Is.True,
                "the Connector reads each value with its attribute");
            for (var i = 0; i < groups.Count; i++)
            {
                Assert.That(mailValues.Where(v => v.ObjectId == groups[i].ObjectId).Select(v => v.StringValue),
                    Is.EquivalentTo(new[] { $"new{i}@glitterband.local" }), "optimistic apply replaced the exported value");
            }
            Assert.That(result.ProcessedExportItems.Select(item => item.ConnectedSystemObject!.AttributeValues
                    .Where(av => av.AttributeId == s.MailAttributeId).Select(av => av.StringValue).Single()),
                Is.EquivalentTo(Enumerable.Range(0, 3).Select(i => $"new{i}@glitterband.local")),
                "the in-memory object each item carries holds what was written (D10)");

            Assert.That(spans.Count(span => span.DisplayName == "ReloadExportBatch"), Is.EqualTo(3), "each batch's load is a span");
            Assert.That(spans.Where(span => span.DisplayName == "ReloadExportBatch").All(span => Equals(span.GetTagItem("loadedCount"), 1)), Is.True);
            Assert.That(applies, Has.Count.EqualTo(3));
            Assert.That(applyParts.Count(name => name == "OptimisticApply.Calculate"), Is.EqualTo(3), "each apply reports its calculation");
            Assert.That(applyParts.Count(name => name == "OptimisticApply.Persist"), Is.EqualTo(3), "and its write");
            Assert.That(applyParts.Count(name => name == "OptimisticApply.FixUp"), Is.EqualTo(3), "and its in-memory fix-up");
        }
    }

    // ─── Helpers ───

    private sealed record Schema(int SystemId, int GroupTypeId, int DnAttributeId, int MemberAttributeId, int MailAttributeId);

    private sealed record SeededExport(
        Guid Id, int ConnectedSystemId, PendingExportChangeType ChangeType, PendingExportStatus Status, bool HasUnresolvedReferences,
        DateTime CreatedAt, IReadOnlyList<PendingExportAttributeChangeStatus> ChangeStatuses);

    /// <summary>
    /// What makes an export executable, stated independently of the repository: an eligible status, an Update with a
    /// change to export, and no Create or Delete already exported.
    /// </summary>
    private static bool IsExecutable(SeededExport export) =>
        export.Status is PendingExportStatus.Pending or PendingExportStatus.Exported or PendingExportStatus.ExportNotConfirmed
        && (export.ChangeType != PendingExportChangeType.Update
            || export.ChangeStatuses.Any(c => c is PendingExportAttributeChangeStatus.Pending or PendingExportAttributeChangeStatus.ExportedNotConfirmed))
        && !(export.ChangeType is PendingExportChangeType.Create or PendingExportChangeType.Delete && export.Status == PendingExportStatus.Exported);

    private async Task<Schema> SeedSchemaAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        var groupType = new ConnectedSystemObjectType { Name = "groupOfNames", ConnectedSystem = system, Selected = true };
        var dn = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName", ConnectedSystemObjectType = groupType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsSecondaryExternalId = true
        };
        var member = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = groupType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        var mail = new ConnectedSystemObjectTypeAttribute
        {
            Name = "mail", ConnectedSystemObjectType = groupType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        groupType.Attributes.Add(dn);
        groupType.Attributes.Add(member);
        groupType.Attributes.Add(mail);
        seed.AddRange(connectorDefinition, system, groupType);
        await seed.SaveChangesAsync();
        return new Schema(system.Id, groupType.Id, dn.Id, member.Id, mail.Id);
    }

    private async Task<int> SeedOtherSystemAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Other Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband APAC", ConnectorDefinition = connectorDefinition };
        seed.AddRange(connectorDefinition, system);
        await seed.SaveChangesAsync();
        return system.Id;
    }

    private async Task<SeededExport> SeedExportAsync(int connectedSystemId, PendingExportChangeType changeType, PendingExportStatus status,
        DateTime createdAt, bool hasUnresolvedReferences = false, (int AttributeId, PendingExportAttributeChangeStatus Status)[]? changes = null)
    {
        changes ??= [];
        await using var seed = NewContext();
        var export = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = connectedSystemId, ChangeType = changeType, Status = status,
            HasUnresolvedReferences = hasUnresolvedReferences, CreatedAt = createdAt
        };
        foreach (var (attributeId, changeStatus) in changes)
        {
            export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(), AttributeId = attributeId, ChangeType = PendingExportAttributeChangeType.Add, Status = changeStatus,
                StringValue = $"cn=user{Guid.NewGuid():N},ou=people,dc=glitterband,dc=local"
            });
        }
        seed.Add(export);
        await seed.SaveChangesAsync();
        return new SeededExport(export.Id, connectedSystemId, changeType, status, hasUnresolvedReferences, createdAt,
            changes.Select(c => c.Status).ToList());
    }

    /// <summary>
    /// Seeds a groupOfNames holding the given members, as the directory's import recorded them, and an Update removing
    /// one of them. Returns the export's id and the group's DN.
    /// </summary>
    private async Task<(Guid ExportId, string GroupDn)> SeedGroupMemberRemovalAsync(Schema s, string[] currentMembers, string memberToRemove)
    {
        var groupDn = $"cn=Group {Guid.NewGuid():N},ou=groups,dc=glitterband,dc=local";
        await using var seed = NewContext();
        var group = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = s.SystemId, TypeId = s.GroupTypeId, Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined, SecondaryExternalIdAttributeId = s.DnAttributeId
        };
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.DnAttributeId, StringValue = groupDn });
        foreach (var member in currentMembers)
        {
            group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), AttributeId = s.MemberAttributeId, UnresolvedReferenceValue = member
            });
        }
        seed.Add(group);

        var export = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = s.SystemId, ConnectedSystemObjectId = group.Id, ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending, CreatedAt = T0
        };
        export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = s.MemberAttributeId, ChangeType = PendingExportAttributeChangeType.Remove,
            Status = PendingExportAttributeChangeStatus.Pending, StringValue = memberToRemove
        });
        seed.Add(export);
        await seed.SaveChangesAsync();
        return (export.Id, groupDn);
    }

    /// <summary>
    /// Seeds a group holding one mail value, and an Update replacing it with another. Returns the object's and the
    /// export's ids.
    /// </summary>
    private async Task<(Guid ObjectId, Guid ExportId)> SeedMailReplacementAsync(Schema s, DateTime createdAt, string oldMail, string newMail)
    {
        await using var seed = NewContext();
        var group = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = s.SystemId, TypeId = s.GroupTypeId, Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined, SecondaryExternalIdAttributeId = s.DnAttributeId
        };
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = s.DnAttributeId, StringValue = $"cn=Group {Guid.NewGuid():N},ou=groups,dc=glitterband,dc=local"
        });
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.MailAttributeId, StringValue = oldMail });
        seed.Add(group);

        var export = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = s.SystemId, ConnectedSystemObjectId = group.Id, ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending, CreatedAt = createdAt
        };
        export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = s.MailAttributeId, ChangeType = PendingExportAttributeChangeType.Remove,
            Status = PendingExportAttributeChangeStatus.Pending, StringValue = oldMail
        });
        export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = s.MailAttributeId, ChangeType = PendingExportAttributeChangeType.Add,
            Status = PendingExportAttributeChangeStatus.Pending, StringValue = newMail
        });
        seed.Add(export);
        await seed.SaveChangesAsync();
        return (group.Id, export.Id);
    }

    /// <summary>
    /// The member modifications the LDAP Connector would send an OpenLDAP directory for the export, by operation, each
    /// with the values it carries.
    /// </summary>
    private static List<(DirectoryAttributeOperation Operation, List<string> Values)> BuildModifications(PendingExport export, string groupDn)
    {
        var settings = new List<ConnectedSystemSettingValue>
        {
            new() { Setting = new ConnectorDefinitionSetting { Name = "Delete Behaviour" }, StringValue = "Delete" }
        };
        var connector = new LdapConnectorExport(new Mock<ILdapOperationExecutor>().Object, settings, Log.Logger, 1,
            LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE, LdapDirectoryType.OpenLDAP, PlaceholderDn);

        return connector.BuildModifyRequests(export, groupDn)
            .SelectMany(request => Enumerable.Range(0, request.Modifications.Count).Select(i => request.Modifications[i]))
            .Where(modification => modification.Name.Equals("member", StringComparison.OrdinalIgnoreCase))
            .Select(modification => (modification.Operation,
                Enumerable.Range(0, modification.Count).Select(i => modification[i]?.ToString() ?? string.Empty).ToList()))
            .ToList();
    }
}
