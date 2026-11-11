// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL regression for the candidate read behind withdrawing queued changes nothing authorises any more
/// (<c>GetUpdatePendingExportsWithQueuedChangesPossiblyWithoutAuthorityAsync</c>, the export's
/// <c>WithdrawQueuedChanges.FindCandidates</c> step), under statistics taken while the Pending Export tables held no
/// live row (#2915).
/// </summary>
/// <remarks>
/// <para>
/// On a 500,000-user leaver step the read found no candidate and took 30.4 s doing it. It asked for the Update Pending
/// Exports with at least one change matching the authority test, as an <c>EXISTS</c> with <c>OR</c> branches. The tables
/// had last been analysed empty, so the planner expected one Pending Export, and ran a nested loop semi join whose inner
/// side read the whole <c>PendingExportId</c> index with no index condition: all 41,281 changes on each of 5,421 loops,
/// 223,784,301 rows removed by the join filter. The cost was Pending Exports times changes.
/// </para>
/// <para>
/// The read must instead reach the changes only by their own <c>PendingExportId</c>, so that no plan can read the table
/// once per Pending Export. Statistics are made stale the way a run leaves them, as in
/// <see cref="PendingExportChangeLoadStaleStatisticsDatabaseTests"/>. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when
/// absent. <c>SetUp</c> TRUNCATEs every table.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class QueuedChangeCandidatesStaleStatisticsDatabaseTests
{
    private const int ExportCount = 1000;
    private const int ChangesPerExport = 3;
    private const int BuffersPerQueuedExport = 20;
    private const int DeletedRuleId = 999_999;
    private static readonly DateTime T0 = new(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);

    // The tables the candidate read reads its Pending Exports and changes from, analysed together while neither holds a live row.
    private static readonly string[] StaleTables = ["PendingExports", PendingExportChangeScans.ChangesTable];

    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL queued change candidate tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        await ExecuteAsync(string.Join(" ", StaleTables.Select(t => $"ALTER TABLE \"{t}\" SET (autovacuum_enabled = false);")));
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        if (_connectionString != null)
            await ExecuteAsync(string.Join(" ", StaleTables.Select(t => $"ALTER TABLE \"{t}\" RESET (autovacuum_enabled);")));
    }

    private JimDbContext NewContext(CommandRecordingInterceptor? recorder = null)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        if (recorder != null)
            builder.AddInterceptors(recorder);
        return new JimDbContext(builder.Options);
    }

    // ─── The plan ───

    /// <summary>
    /// With a thousand authorised Update Pending Exports queued after the tables were analysed empty, the candidate read
    /// reaches the changes only through an index condition on their <c>PendingExportId</c>, reads each change at most once,
    /// and costs each statement a bounded number of buffers per queued Pending Export: never a read of one table per row of
    /// another.
    /// </summary>
    [Test]
    public async Task GetUpdatePendingExportsWithQueuedChangesPossiblyWithoutAuthorityAsync_StatisticsTakenWhileTheTablesWereEmpty_ReadsEachChangeOnceByPendingExportIdAsync()
    {
        var seeded = await SeedUnderStaleStatisticsAsync();

        var recorder = new CommandRecordingInterceptor();
        List<PendingExport> candidates;
        await using (var ctx = NewContext(recorder))
            candidates = await new PostgresDataRepository(ctx).Sync
                .GetUpdatePendingExportsWithQueuedChangesPossiblyWithoutAuthorityAsync(seeded.SystemId, []);

        Assert.That(candidates.Select(pe => pe.Id), Is.EqualTo(new[] { seeded.StaleExportId }),
            "the one export whose change has lost its Attribute Flow, among a thousand still authorised");

        var (scans, statementBuffers, plans) = await PendingExportChangeScans.ExplainWithBuffersAsync(_connectionString, recorder);
        var described = string.Join("\n", scans);
        var queued = ExportCount + 1;
        var changesHeld = queued * ChangesPerExport;
        // A queued Pending Export costs a handful of buffers in a read that is linear in the queue: an index probe for it,
        // one for its object, its changes' heap pages. A read of one table per row of another costs thousands.
        var bufferBudget = BuffersPerQueuedExport * queued;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scans, Is.Not.Empty, $"the read reaches the changes. Plans:\n{plans}");
            Assert.That(scans.All(s => s.IndexCondition?.Contains("\"PendingExportId\"", StringComparison.Ordinal) == true), Is.True,
                $"every read of the changes seeks them by Pending Export id, never reading the table or its index whole:\n{described}\n\nPlans:\n{plans}");
            Assert.That(scans.All(s => s.RowsRead <= changesHeld), Is.True,
                $"no read of the changes reads more than the {changesHeld} the table holds:\n{described}");
            Assert.That(statementBuffers, Has.All.LessThanOrEqualTo(bufferBudget),
                $"each statement touches at most {BuffersPerQueuedExport} buffers per queued Pending Export ({bufferBudget}); " +
                $"touched: {string.Join(", ", statementBuffers)}");
        }
    }

    // ─── Equivalence with the EXISTS read ───

    /// <summary>
    /// The candidate read returns exactly the Pending Exports the <c>EXISTS</c> read it replaces returned, each with every
    /// one of its changes and its Connected System Object, across every branch of the authority test: an object no longer
    /// joined, a Synchronisation Rule disabled or deleted, an Attribute Flow disabled or never on the change's own rule,
    /// and the class membership attribute exempt from the last; with each case's neighbours that must not qualify (changes
    /// already sent, changes with no rule, exports executing, Creates and Deletes, exports with no changes or no object,
    /// and another Connected System's queue).
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task GetUpdatePendingExportsWithQueuedChangesPossiblyWithoutAuthorityAsync_MixedQueue_ReturnsWhatTheExistsReadDidAsync(bool classMembershipExempt)
    {
        var fixture = await SeedMixedQueueAsync();
        var classAttributeIds = classMembershipExempt ? new[] { fixture.ObjectClassAttributeId } : [];

        List<PendingExport> expected;
        await using (var reference = NewContext())
            expected = await FindCandidatesAsTheExistsReadDidAsync(reference, fixture.SystemId, classAttributeIds);
        List<PendingExport> candidates;
        await using (var ctx = NewContext())
            candidates = await new PostgresDataRepository(ctx).Sync
                .GetUpdatePendingExportsWithQueuedChangesPossiblyWithoutAuthorityAsync(fixture.SystemId, classAttributeIds);

        var expectedNames = new List<string>
        {
            "flowDisabled", "flowDisabledBesideAuthorised", "flowOnAnotherRuleOnly", "ruleDisabled", "ruleDeleted",
            "classMembershipRuleDisabled", "unjoined", "unjoinedBesideUnattributed", "resend", "exportedButRequeued", "parked",
            "noObjectRuleDisabled"
        };
        if (!classMembershipExempt)
            expectedNames.Add("classMembership");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expected.Select(pe => fixture.NameOf[pe.Id]), Is.EquivalentTo(expectedNames),
                "the fixture exercises each branch as intended (read the old way)");
            Assert.That(candidates.Select(pe => pe.Id), Is.EquivalentTo(expected.Select(pe => pe.Id)), "the same candidates as the EXISTS read");
            Assert.That(candidates.Select(pe => pe.Id).Distinct().Count(), Is.EqualTo(candidates.Count), "each candidate once");
            Assert.That(candidates.ToDictionary(pe => pe.Id, Describe), Is.EquivalentTo(expected.ToDictionary(pe => pe.Id, Describe)),
                "each candidate with all of its changes, its object and its scalars, as the EXISTS read loaded them");
        }
    }

    private static string Describe(PendingExport pe) =>
        $"{pe.ConnectedSystemId} {pe.ChangeType} {pe.Status} cso={pe.ConnectedSystemObjectId}/{pe.ConnectedSystemObject?.Id} " +
        $"mvo={pe.ConnectedSystemObject?.MetaverseObjectId} changes=[{string.Join("; ", pe.AttributeValueChanges.OrderBy(c => c.Id)
            .Select(c => $"{c.Id} {c.AttributeId} {c.SyncRuleId} {c.Status} {c.ChangeType} {c.StringValue} {c.PendingExportId}"))}]";

    /// <summary>
    /// The candidate read as it stood before #2915: the Update Pending Exports with at least one change that matches the
    /// authority test, as one <c>EXISTS</c> with <c>OR</c> branches, each loaded with all its changes and its object.
    /// </summary>
    private static Task<List<PendingExport>> FindCandidatesAsTheExistsReadDidAsync(JimDbContext database, int connectedSystemId, IReadOnlyCollection<int> classMembershipAttributeIds)
    {
        var classAttributeIds = classMembershipAttributeIds.ToList();
        return database.PendingExports
            .AsNoTracking()
            .AsSplitQuery()
            .Include(pe => pe.AttributeValueChanges)
            .Include(pe => pe.ConnectedSystemObject)
            .Where(pe => pe.ConnectedSystemId == connectedSystemId
                      && pe.ChangeType == PendingExportChangeType.Update
                      && pe.Status != PendingExportStatus.Executing)
            .Where(pe => pe.AttributeValueChanges.Any(c =>
                (c.Status == PendingExportAttributeChangeStatus.Pending || c.Status == PendingExportAttributeChangeStatus.ExportedNotConfirmed)
                && c.SyncRuleId != null
                && ((pe.ConnectedSystemObject != null && pe.ConnectedSystemObject.MetaverseObjectId == null)
                    || !database.SyncRules.Any(r => r.Id == c.SyncRuleId && r.Enabled)
                    || (!classAttributeIds.Contains(c.AttributeId)
                        && !database.SyncRuleMappings.Any(m => m.SyncRuleId == c.SyncRuleId && m.Enabled && m.TargetConnectedSystemAttributeId == c.AttributeId)))))
            .ToListAsync();
    }

    // ─── Seeding: the stale statistics ───

    private sealed record StaleSeed(int SystemId, Guid StaleExportId);

    /// <summary>
    /// The statistics a run leaves: an earlier run's Pending Exports and changes written and then removed, and both tables
    /// analysed holding pages but no live row. Then this run's queue, written with no analysis after: thousands of joined
    /// people, each with an Update Pending Export whose changes an enabled Attribute Flow still authorises, and one more
    /// whose change's Attribute Flow is disabled.
    /// </summary>
    private async Task<StaleSeed> SeedUnderStaleStatisticsAsync()
    {
        var schema = await SeedSchemaAsync();
        await WriteAuthorisedQueueAsync(schema, ExportCount / 3, "earlier");
        await ExecuteAsync($"DELETE FROM \"{PendingExportChangeScans.ChangesTable}\"; DELETE FROM \"PendingExports\";");
        await ExecuteAsync($"ANALYZE {string.Join(", ", StaleTables.Select(t => $"\"{t}\""))};");

        await WriteAuthorisedQueueAsync(schema, ExportCount, "person");

        await using var seed = NewContext();
        var cso = NewJoinedPerson(schema, await seed.MetaverseObjectTypes.SingleAsync(t => t.Id == schema.MvTypeId));
        var stale = NewUpdate(schema.SystemId, cso, T0.AddDays(1));
        foreach (var attributeId in new[] { schema.Mail, schema.Description, schema.UnflowedAttributeId })
            stale.AttributeValueChanges.Add(NewChange(attributeId, schema.RuleId, "stale"));
        seed.AddRange(cso, stale);
        await seed.SaveChangesAsync();

        return new StaleSeed(schema.SystemId, stale.Id);
    }

    private async Task WriteAuthorisedQueueAsync(StaleSchema schema, int count, string prefix)
    {
        await using var seed = NewContext();
        var mvType = await seed.MetaverseObjectTypes.SingleAsync(t => t.Id == schema.MvTypeId);
        for (var n = 0; n < count; n++)
        {
            var cso = NewJoinedPerson(schema, mvType);
            var export = NewUpdate(schema.SystemId, cso, T0.AddSeconds(n));
            foreach (var attributeId in new[] { schema.Mail, schema.Description, schema.Title })
                export.AttributeValueChanges.Add(NewChange(attributeId, schema.RuleId, $"{prefix} {n}"));
            seed.AddRange(cso, export);
        }

        await seed.SaveChangesAsync();
    }

    private sealed record StaleSchema(int SystemId, int TypeId, int MvTypeId, int RuleId, int Mail, int Description, int Title, int UnflowedAttributeId);

    private async Task<StaleSchema> SeedSchemaAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "inetOrgPerson", ConnectedSystem = system, Selected = true };
        var mail = NewAttribute(type, "mail");
        var description = NewAttribute(type, "description");
        var title = NewAttribute(type, "title");
        var employeeType = NewAttribute(type, "employeeType");
        var mvType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        seed.AddRange(connectorDefinition, system, type, mvType);
        await seed.SaveChangesAsync();

        var rule = new SyncRule
        {
            Name = "Glitterband Export", ConnectedSystem = system, ConnectedSystemObjectType = type, MetaverseObjectType = mvType,
            Direction = SyncRuleDirection.Export, Enabled = true
        };
        foreach (var attribute in new[] { mail, description, title })
            rule.AttributeFlowRules.Add(new SyncRuleMapping { SyncRule = rule, TargetConnectedSystemAttribute = attribute, Enabled = true });
        rule.AttributeFlowRules.Add(new SyncRuleMapping { SyncRule = rule, TargetConnectedSystemAttribute = employeeType, Enabled = false });
        seed.Add(rule);
        await seed.SaveChangesAsync();

        return new StaleSchema(system.Id, type.Id, mvType.Id, rule.Id, mail.Id, description.Id, title.Id, employeeType.Id);
    }

    /// <summary>A person joined to a Metaverse Object of its own, of the Metaverse Object Type <paramref name="mvType"/>.</summary>
    private static ConnectedSystemObject NewJoinedPerson(StaleSchema schema, MetaverseObjectType mvType) => new()
    {
        Id = Guid.NewGuid(), ConnectedSystemId = schema.SystemId, TypeId = schema.TypeId, Status = ConnectedSystemObjectStatus.Normal,
        MetaverseObject = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType }
    };

    // ─── Seeding: the mixed queue ───

    private sealed class MixedQueue
    {
        public int SystemId;
        public int ObjectClassAttributeId;
        public readonly Dictionary<Guid, string> NameOf = new();
    }

    /// <summary>
    /// One Connected System with three export Synchronisation Rules and a queue of Update Pending Exports, one per case,
    /// each named for what it exercises; and a second Connected System whose queue holds a change that would qualify.
    /// <list type="bullet">
    /// <item>"Directory Export" (enabled): mail and title flowed, displayName's Attribute Flow disabled.</item>
    /// <item>"Directory Names" (enabled): displayName flowed, nothing else.</item>
    /// <item>"Old Directory Export" (disabled): mail flowed.</item>
    /// </list>
    /// </summary>
    private async Task<MixedQueue> SeedMixedQueueAsync()
    {
        var fixture = new MixedQueue();
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var mail = NewAttribute(type, "mail");
        var displayName = NewAttribute(type, "displayName");
        var title = NewAttribute(type, "title");
        var objectClass = NewAttribute(type, "objectClass");
        var otherSystem = new ConnectedSystem { Name = "Other Directory", ConnectorDefinition = connectorDefinition };
        var otherType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = otherSystem, Selected = true };
        var otherMail = NewAttribute(otherType, "mail");
        var mvType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        seed.AddRange(connectorDefinition, system, type, otherSystem, otherType, mvType);
        await seed.SaveChangesAsync();

        SyncRule Rule(string name, bool enabled, params (ConnectedSystemObjectTypeAttribute Attribute, bool Enabled)[] flows)
        {
            var rule = new SyncRule
            {
                Name = name, ConnectedSystem = system, ConnectedSystemObjectType = type, MetaverseObjectType = mvType,
                Direction = SyncRuleDirection.Export, Enabled = enabled
            };
            foreach (var (attribute, flowEnabled) in flows)
                rule.AttributeFlowRules.Add(new SyncRuleMapping { SyncRule = rule, TargetConnectedSystemAttribute = attribute, Enabled = flowEnabled });
            seed.Add(rule);
            return rule;
        }
        var export = Rule("Directory Export", true, (mail, true), (title, true), (displayName, false));
        var names = Rule("Directory Names", true, (displayName, true));
        var old = Rule("Old Directory Export", false, (mail, true));
        await seed.SaveChangesAsync();

        void Add(string name, string objectState, params (ConnectedSystemObjectTypeAttribute Attribute, int? RuleId, PendingExportAttributeChangeStatus Status)[] changes) =>
            AddExport(name, objectState, PendingExportChangeType.Update, PendingExportStatus.Pending, changes);

        void AddExport(string name, string objectState, PendingExportChangeType changeType, PendingExportStatus status,
            params (ConnectedSystemObjectTypeAttribute Attribute, int? RuleId, PendingExportAttributeChangeStatus Status)[] changes)
        {
            var onOther = objectState == "other";
            ConnectedSystemObject? cso = objectState == "none" ? null : new ConnectedSystemObject
            {
                Id = Guid.NewGuid(), Type = onOther ? otherType : type, ConnectedSystem = onOther ? otherSystem : system,
                Status = ConnectedSystemObjectStatus.Normal,
                MetaverseObject = objectState == "unjoined" ? null : new MetaverseObject { Id = Guid.NewGuid(), Type = mvType }
            };
            var pe = new PendingExport
            {
                Id = Guid.NewGuid(), ConnectedSystemId = onOther ? otherSystem.Id : system.Id, ConnectedSystemObject = cso,
                ChangeType = changeType, Status = status, CreatedAt = T0
            };
            foreach (var (attribute, ruleId, changeStatus) in changes)
            {
                var change = NewChange(attribute.Id, ruleId, name);
                change.Status = changeStatus;
                pe.AttributeValueChanges.Add(change);
            }
            if (cso != null)
                seed.Add(cso);
            seed.Add(pe);
            fixture.NameOf[pe.Id] = name;
        }

        const PendingExportAttributeChangeStatus queued = PendingExportAttributeChangeStatus.Pending;
        const PendingExportAttributeChangeStatus resent = PendingExportAttributeChangeStatus.ExportedNotConfirmed;
        const PendingExportAttributeChangeStatus sent = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;

        // Candidates: an Attribute Flow disabled, or never on the change's own rule.
        Add("flowDisabled", "joined", (displayName, export.Id, queued));
        Add("flowDisabledBesideAuthorised", "joined", (mail, export.Id, queued), (displayName, export.Id, queued), (title, null, queued));
        Add("flowOnAnotherRuleOnly", "joined", (title, names.Id, queued));
        // Candidates: the Synchronisation Rule disabled or deleted, the class membership attribute included.
        Add("ruleDisabled", "joined", (mail, old.Id, queued));
        Add("ruleDeleted", "joined", (mail, DeletedRuleId, queued));
        Add("classMembershipRuleDisabled", "joined", (objectClass, old.Id, queued));
        // Candidates: the object no longer joined, whatever authorises the change.
        Add("unjoined", "unjoined", (mail, export.Id, queued));
        Add("unjoinedBesideUnattributed", "unjoined", (title, null, queued), (mail, export.Id, resent));
        // Candidates: every export status but Executing, and a change sent but not confirmed.
        AddExport("resend", "joined", PendingExportChangeType.Update, PendingExportStatus.ExportNotConfirmed, (displayName, export.Id, resent));
        AddExport("exportedButRequeued", "joined", PendingExportChangeType.Update, PendingExportStatus.Exported, (displayName, export.Id, queued));
        AddExport("parked", "joined", PendingExportChangeType.Update, PendingExportStatus.Parked, (mail, old.Id, queued));
        // Candidate: an export with no object, whose rule is disabled.
        Add("noObjectRuleDisabled", "none", (mail, old.Id, queued));
        // Candidate only when the class membership attribute is not exempt: its rule is enabled, and nothing flows it.
        Add("classMembership", "joined", (objectClass, export.Id, queued));

        // Not candidates.
        Add("authorised", "joined", (mail, export.Id, queued), (displayName, names.Id, queued), (title, export.Id, resent));
        Add("unattributed", "joined", (displayName, null, queued));
        Add("unjoinedUnattributed", "unjoined", (mail, null, queued));
        Add("unjoinedAlreadySent", "unjoined", (mail, export.Id, sent));
        Add("staleButSent", "joined", (displayName, export.Id, sent), (mail, old.Id, sent));
        Add("noChanges", "joined");
        Add("noObjectAuthorised", "none", (mail, export.Id, queued));
        AddExport("executing", "unjoined", PendingExportChangeType.Update, PendingExportStatus.Executing, (mail, old.Id, queued));
        AddExport("create", "unjoined", PendingExportChangeType.Create, PendingExportStatus.Pending, (displayName, export.Id, queued));
        AddExport("delete", "unjoined", PendingExportChangeType.Delete, PendingExportStatus.Pending, (mail, old.Id, queued));
        AddExport("otherSystem", "other", PendingExportChangeType.Update, PendingExportStatus.Pending, (otherMail, DeletedRuleId, queued));

        await seed.SaveChangesAsync();
        fixture.SystemId = system.Id;
        fixture.ObjectClassAttributeId = objectClass.Id;
        return fixture;
    }

    // ─── Shared ───

    private static ConnectedSystemObjectTypeAttribute NewAttribute(ConnectedSystemObjectType type, string name)
    {
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = name, ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        type.Attributes.Add(attribute);
        return attribute;
    }

    private static PendingExport NewUpdate(int systemId, ConnectedSystemObject cso, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(), ConnectedSystemId = systemId, ConnectedSystemObject = cso, ChangeType = PendingExportChangeType.Update,
        Status = PendingExportStatus.Pending, CreatedAt = createdAt
    };

    private static PendingExportAttributeValueChange NewChange(int attributeId, int? syncRuleId, string value = "value") => new()
    {
        Id = Guid.NewGuid(), AttributeId = attributeId, SyncRuleId = syncRuleId, StringValue = value,
        ChangeType = PendingExportAttributeChangeType.Update, Status = PendingExportAttributeChangeStatus.Pending
    };

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
