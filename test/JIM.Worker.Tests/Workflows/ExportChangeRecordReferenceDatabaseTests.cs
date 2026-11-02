// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;
using Activity = JIM.Models.Activities.Activity;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Real-PostgreSQL export of a group's members (#2700): the change record the export writes names each member by its
/// DN and records the member's id with it, as the change record reference fix-up used to leave it, so the fix-up after
/// the export has nothing to read.
/// </summary>
/// <remarks>
/// The first export to a large Connected System marked every member reference it wrote (8.3 million at 500,000 objects)
/// and the fix-up then took 164 s to find each member again by its DN, although export resolution had already found it.
/// Runs the real export processor against a tracking context, as the Worker does, and reads the fix-up's span through a
/// trace the test starts itself. Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ExportChangeRecordReferenceDatabaseTests
{
    private static readonly ActivitySource TestSource = new("JIM.Tests.ExportChangeRecordReferences");

    private static readonly (string Name, string Dn)[] Members =
    [
        ("Alice", "CN=Alice,OU=People,DC=glitterband,DC=local"),
        ("Bob", "CN=Bob,OU=People,DC=glitterband,DC=local"),
        ("Carol", "CN=Carol,OU=People,DC=glitterband,DC=local")
    ];

    private string _connectionString = null!;
    private ActivityListener _listener = null!;
    private readonly ConcurrentQueue<System.Diagnostics.Activity> _stopped = new();

    private int _systemId;
    private int _memberAttributeId;
    private Dictionary<string, Guid> _memberCsoIdsByDn = [];

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL export change record reference tests.");
        _connectionString = connectionString!;

        TestUtilities.SetEnvironmentVariables();
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
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        await SeedAsync();
    }

    private JimDbContext NewContext(bool trackAll = false)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        // The Worker tracks by default (src/JIM.Worker/Program.cs); an export run must too.
        if (trackAll)
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        return new JimDbContext(builder.Options);
    }

    /// <summary>
    /// Every member's object has a row, so the export's change record is written with each member's id and nothing is
    /// left for the fix-up: the final state is the one the fix-up produced before (the id, the DN, the stub flag).
    /// </summary>
    [Test]
    public async Task Export_GroupWithMembersWhoseObjectsExist_WritesEachMembersIdAndLeavesTheFixupNothingMarkedAsync()
    {
        var spans = await RecordAsync(ExportAsync);

        var memberValues = await ReadMemberValuesAsync();
        var find = spans.Single(s => s.DisplayName == "Cso.ResolveChangeReferences.FindCandidates");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(memberValues.Select(v => (v.StringValue, v.ReferenceValueId)),
                Is.EquivalentTo(_memberCsoIdsByDn.Select(pair => ((string?)pair.Key, (Guid?)pair.Value))),
                "each member's DN with its own object's id, as the fix-up used to leave them");
            Assert.That(memberValues.All(v => v.IsPendingExportStub), Is.True, "the history still shows each member by its DN");
            Assert.That(memberValues.Any(v => v.IsUnresolvedReference), Is.False, "nothing is left for the fix-up");
            Assert.That(find.GetTagItem("markedCount"), Is.EqualTo(0L), "the fix-up after the export reads no marked values");
        }
    }

    #region Harness

    private sealed record StoredValue(string? StringValue, Guid? ReferenceValueId, bool IsUnresolvedReference, bool IsPendingExportStub);

    /// <summary>
    /// One Connected System of users and groups, each named by its DN as its Secondary External ID; three users joined to
    /// Metaverse Objects, and a group whose Pending Export adds all three as members, staged by Metaverse Object id as a
    /// synchronisation stages them.
    /// </summary>
    private async Task SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };

        ConnectedSystemObjectType NewType(string name)
        {
            var type = new ConnectedSystemObjectType { Name = name, ConnectedSystem = system, Selected = true };
            type.Attributes.Add(new ConnectedSystemObjectTypeAttribute
            {
                Name = "objectGUID", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
            });
            type.Attributes.Add(new ConnectedSystemObjectTypeAttribute
            {
                Name = "distinguishedName", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsSecondaryExternalId = true
            });
            return type;
        }

        var userType = NewType("user");
        var groupType = NewType("group");
        var member = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = groupType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        groupType.Attributes.Add(member);
        var person = new MetaverseObjectType { Name = "Person", PluralName = "People" };

        seed.AddRange(connectorDefinition, system, userType, groupType, person);
        await seed.SaveChangesAsync();
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            { ConnectedSystemId = system.Id, Name = "Export", RunType = ConnectedSystemRunType.Export });

        ConnectedSystemObject NewObject(ConnectedSystemObjectType type, string dn, Guid? metaverseObjectId)
        {
            var externalId = type.Attributes.Single(a => a.IsExternalId);
            var secondaryExternalId = type.Attributes.Single(a => a.IsSecondaryExternalId);
            var cso = new ConnectedSystemObject
            {
                ConnectedSystemId = system.Id, TypeId = type.Id, ExternalIdAttributeId = externalId.Id,
                SecondaryExternalIdAttributeId = secondaryExternalId.Id, Status = ConnectedSystemObjectStatus.Normal,
                MetaverseObjectId = metaverseObjectId,
                JoinType = metaverseObjectId.HasValue ? ConnectedSystemObjectJoinType.Provisioned : ConnectedSystemObjectJoinType.NotJoined
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, AttributeId = externalId.Id, StringValue = Guid.NewGuid().ToString() });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, AttributeId = secondaryExternalId.Id, StringValue = dn });
            return cso;
        }

        var memberMvoIds = new List<Guid>();
        var memberCsos = new Dictionary<string, ConnectedSystemObject>();
        foreach (var (name, dn) in Members)
        {
            var mvo = new MetaverseObject { Type = person };
            seed.Add(mvo);
            await seed.SaveChangesAsync();
            memberMvoIds.Add(mvo.Id);
            memberCsos[dn] = NewObject(userType, dn, mvo.Id);
        }

        var team = NewObject(groupType, "CN=Team,OU=Groups,DC=glitterband,DC=local", metaverseObjectId: null);
        seed.AddRange(memberCsos.Values);
        seed.Add(team);
        await seed.SaveChangesAsync();

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = system.Id, ConnectedSystemObjectId = team.Id,
            ChangeType = PendingExportChangeType.Update, Status = PendingExportStatus.Pending, HasUnresolvedReferences = true
        };
        foreach (var memberMvoId in memberMvoIds)
        {
            pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(), AttributeId = member.Id, ChangeType = PendingExportAttributeChangeType.Add,
                UnresolvedReferenceValue = memberMvoId.ToString()
            });
        }
        seed.PendingExports.Add(pendingExport);
        await seed.SaveChangesAsync();

        _systemId = system.Id;
        _memberAttributeId = member.Id;
        _memberCsoIdsByDn = memberCsos.ToDictionary(pair => pair.Key, pair => pair.Value.Id);
    }

    /// <summary>Runs one export on its own tracking context, as the Worker does, through a Connector that accepts everything.</summary>
    private async Task ExportAsync()
    {
        await using var ctx = NewContext(trackAll: true);
        // Wired as the Worker wires it (src/JIM.Worker/Program.cs): the export servers use the repository's own sync side.
        var repository = new PostgresDataRepository(ctx);
        using var jim = new JimApplication(repository, repository.Sync);
        var system = await jim.ConnectedSystems.GetConnectedSystemAsync(_systemId, withChangeTracking: true)
            ?? throw new InvalidOperationException("Connected System not found.");
        var runProfile = system.RunProfiles!.Single(rp => rp.RunType == ConnectedSystemRunType.Export);
        var activity = new Activity
        {
            TargetName = runProfile.Name, Status = ActivityStatus.InProgress, ConnectedSystemId = system.Id,
            ConnectedSystemRunProfileId = runProfile.Id, ConnectedSystemRunType = ConnectedSystemRunType.Export
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();

        var workerTask = new SynchronisationWorkerTask(system.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(), Status = WorkerTaskStatus.Processing, Activity = activity
        };
        var processor = new SyncExportTaskProcessor(new SyncServer(jim), jim.SyncRepository, new MockCallConnector(),
            system, runProfile, workerTask, new CancellationTokenSource());
        await processor.PerformExportAsync();

        var errors = activity.RunProfileExecutionItems.Where(i => i.ErrorType is { } e && e != ActivityRunProfileExecutionItemErrorType.NotSet).ToList();
        Assert.That(errors, Is.Empty, $"the export reported errors: {string.Join("; ", errors.Select(e => e.ErrorMessage))}");
    }

    /// <summary>The member values of the change records the export wrote.</summary>
    private async Task<List<StoredValue>> ReadMemberValuesAsync()
    {
        await using var ctx = NewContext();
        return await ctx.ConnectedSystemObjectChangeAttributeValues
            .AsNoTracking()
            .Where(v => EF.Property<int?>(v.ConnectedSystemObjectChangeAttribute, "AttributeId") == _memberAttributeId)
            .Select(v => new StoredValue(v.StringValue, EF.Property<Guid?>(v, "ReferenceValueId"), v.IsUnresolvedReference, v.IsPendingExportStub))
            .ToListAsync();
    }

    /// <summary>
    /// Runs <paramref name="action"/> under a span of the test's own and returns every span completed in its trace.
    /// </summary>
    private async Task<List<System.Diagnostics.Activity>> RecordAsync(Func<Task> action)
    {
        ActivityTraceId traceId;
        using (var root = TestSource.StartActivity("Test"))
        {
            Assert.That(root, Is.Not.Null, "the test's listener samples its own source");
            traceId = root!.TraceId;
            await action();
        }
        return [.. _stopped.Where(s => s.TraceId == traceId)];
    }

    #endregion
}
