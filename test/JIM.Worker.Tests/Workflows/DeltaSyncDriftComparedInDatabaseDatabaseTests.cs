// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using JIM.Worker.Tests.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// #2818, end to end: a Target system's Delta Synchronisation of groups whose only reader of their members is Drift
/// Detection, run twice on the same generated data, once loading every group whole (the threshold at its maximum) and once
/// leaving out every member attribute it can (the threshold at zero). The two runs must leave the same outcome: the same
/// Pending Exports with the same changes, the same Run Profile Execution Items and outcomes, the same Metaverse Objects and
/// the same change records; and the run that left members out must leave every Connected System Object's values exactly
/// as they were.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class DeltaSyncDriftComparedInDatabaseDatabaseTests
{
    private const int Seed = 2818;
    private const int PageSize = 6;

    private string _connectionString = null!;

    private JimDbContext NewContext(bool trackAll = false)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        // The Worker tracks by default (src/JIM.Worker/Program.cs); a synchronisation run must too.
        if (trackAll)
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL Delta Synchronisation drift comparison workflow.");
        _connectionString = connectionString!;

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [Test]
    public async Task DeltaSync_GroupsComparedInTheDatabase_LeaveTheSameOutcomeAsGroupsLoadedWholeAsync()
    {
        var whole = await RunAsync(threshold: int.MaxValue);
        var leftOut = await RunAsync(threshold: 0);

        TestContext.Out.WriteLine($"#2818 workflow: whole load left {whole.Counts.LeftOutObjects} object(s) out; " +
                                  $"threshold 0 left {leftOut.Counts.LeftOutObjects} out and asked {leftOut.Counts.DriftQuestions} question(s).");
        TestContext.Out.WriteLine(leftOut.Outcome);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whole.Counts.LeftOutObjects, Is.Zero, "the maximum threshold loads every group whole");
            Assert.That(leftOut.Counts.LeftOutObjects, Is.GreaterThan(5), "the zero threshold leaves most groups' members out");
            Assert.That(leftOut.Counts.DriftQuestions, Is.EqualTo(leftOut.Counts.LeftOutObjects), "Drift Detection asked about each group left out");
            Assert.That(whole.Outcome, Does.Contain("DriftCorrection"), "the generated groups drift");
            Assert.That(FirstDifference(whole.Outcome, leftOut.Outcome), Is.Null, "the two runs leave the same outcome");
            Assert.That(leftOut.ValuesBefore, Is.EqualTo(whole.ValuesBefore), "both runs start from the same data");
            Assert.That(leftOut.ValuesAfter, Is.EqualTo(whole.ValuesAfter), "both runs leave every object's values the same");
            Assert.That(leftOut.GroupRowsAfter, Is.EqualTo(leftOut.GroupRowsBefore),
                "every group keeps every value row it held (the obsolete users' deletion clears only the references to them)");
        }
    }

    private sealed record RunResult((int LeftOutObjects, int DriftQuestions) Counts, string Outcome, string ValuesBefore, string ValuesAfter,
        string GroupRowsBefore, string GroupRowsAfter);

    /// <summary>
    /// Resets the database, seeds the generated data, and runs one Delta Synchronisation at the given threshold.
    /// </summary>
    private async Task<RunResult> RunAsync(int threshold)
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        var systemId = await SeedAsync(new Random(Seed));
        var valuesBefore = await DescribeConnectedSystemObjectValuesAsync();
        var groupRowsBefore = await DescribeConnectedSystemObjectValuesAsync(groupRowsOnly: true);

        (int, int) counts;
        await using (var ctx = NewContext(trackAll: true))
        {
            var repository = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repository, syncRepository: repository.Sync);
            var system = await jim.ConnectedSystems.GetConnectedSystemAsync(systemId, withChangeTracking: true)
                ?? throw new InvalidOperationException("Connected System not found.");
            var runProfile = system.RunProfiles!.Single(rp => rp.RunType == ConnectedSystemRunType.DeltaSynchronisation);
            var activity = new Activity
            {
                TargetName = runProfile.Name, Status = ActivityStatus.InProgress, ConnectedSystemId = system.Id,
                ConnectedSystemRunProfileId = runProfile.Id, ConnectedSystemRunType = ConnectedSystemRunType.DeltaSynchronisation
            };
            ctx.Activities.Add(activity);
            await ctx.SaveChangesAsync();

            var processor = new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), jim.SyncRepository,
                system, runProfile, activity, new CancellationTokenSource())
            {
                SynchronisationValueLoadThresholdForTests = threshold
            };
            await processor.PerformDeltaSyncAsync();
            counts = processor.SynchronisationValueLoadCountsForTests;
        }

        return new RunResult(counts, await DescribeOutcomeAsync(), valuesBefore, await DescribeConnectedSystemObjectValuesAsync(),
            groupRowsBefore, await DescribeConnectedSystemObjectValuesAsync(groupRowsOnly: true));
    }

    /// <summary>
    /// A Target system of users and groups, every group joined and marked for synchronisation, its Metaverse Object's
    /// Static Members exported to its member attribute by an Enforce State rule, and nothing else reading the attribute.
    /// The groups disagree with their Metaverse Objects in every way the comparison has to answer: members the Metaverse
    /// Object no longer holds, members it holds that the group lacks, members not joined to anything, references not yet
    /// resolved, values held under another data type, members held twice, a group holding only text, a group nesting
    /// another, users left obsolete in the same run, and a group whose Pending Export already queues a change.
    /// </summary>
    private async Task<int> SeedAsync(Random random)
    {
        var seed = await DriftComparisonSeed.CreateAsync(() => NewContext(), _connectionString);

        await using (var ctx = NewContext())
        {
            foreach (var (key, valueType, value) in new[]
                     {
                         (Constants.SettingKeys.SyncPageSize, ServiceSettingValueType.Integer, PageSize.ToString()),
                         ("ChangeTracking.CsoChanges.Enabled", ServiceSettingValueType.Boolean, "true"),
                         ("ChangeTracking.MvoChanges.Enabled", ServiceSettingValueType.Boolean, "true")
                     })
            {
                ctx.ServiceSettingItems.Add(new ServiceSetting
                {
                    Key = key, DisplayName = key, Category = ServiceSettingCategory.Synchronisation,
                    ValueType = valueType, DefaultValue = value, Value = value
                });
            }

            ctx.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
                { ConnectedSystemId = seed.SystemId, Name = "Delta Synchronisation", RunType = ConnectedSystemRunType.DeltaSynchronisation });

            var groupType = await ctx.ConnectedSystemObjectTypes.Include(t => t.Attributes).SingleAsync(t => t.Id == seed.GroupTypeId);
            var groupMvType = await ctx.MetaverseObjectTypes.SingleAsync(t => t.Id == seed.GroupMvTypeId);
            var staticMembers = await ctx.MetaverseAttributes.SingleAsync(a => a.Id == seed.StaticMembersAttributeId);
            var system = await ctx.ConnectedSystems.SingleAsync(s => s.Id == seed.SystemId);
            var rule = new SyncRule
            {
                Name = "Group Membership", ConnectedSystem = system, Direction = SyncRuleDirection.Export, Enabled = true, EnforceState = true,
                ConnectedSystemObjectType = groupType, MetaverseObjectType = groupMvType
            };
            rule.AttributeFlowRules.Add(new SyncRuleMapping
            {
                SyncRule = rule, TargetConnectedSystemAttribute = groupType.Attributes.Single(a => a.Id == seed.MemberAttributeId),
                Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = staticMembers } }
            });
            ctx.SyncRules.Add(rule);
            await ctx.SaveChangesAsync();
        }

        var users = Enumerable.Range(0, 48).Select(i => (CsoId: NextGuid(random), MvoId: i % 9 == 0 ? (Guid?)null : NextGuid(random),
            Status: i % 17 == 5 ? ConnectedSystemObjectStatus.Obsolete : ConnectedSystemObjectStatus.Normal)).ToList();
        await seed.AddUsersAsync(users.Where(u => u.Status == ConnectedSystemObjectStatus.Normal).ToList(), synchronisationPending: false);
        await seed.AddUsersAsync(users.Where(u => u.Status == ConnectedSystemObjectStatus.Obsolete).ToList(), synchronisationPending: true);

        var groups = Enumerable.Range(0, 16).Select(_ => (CsoId: NextGuid(random), MvoId: (Guid?)NextGuid(random), Status: ConnectedSystemObjectStatus.Normal)).ToList();
        await seed.AddGroupsAsync(groups);

        for (var g = 0; g < groups.Count; g++)
        {
            var heldUsers = users.Where(_ => random.Next(3) != 0).ToList();
            var rows = new List<DriftComparisonSeed.MemberRow>();
            foreach (var user in heldUsers)
            {
                rows.Add(g == 3
                    ? new DriftComparisonSeed.MemberRow(NextGuid(random), StringValue: $"uid={user.CsoId:N}")
                    : new DriftComparisonSeed.MemberRow(NextGuid(random), ReferenceValueId: user.CsoId, UnresolvedReferenceValue: $"uid={user.CsoId:N}"));
                if (random.Next(15) == 0)
                    rows.Add(new DriftComparisonSeed.MemberRow(NextGuid(random), ReferenceValueId: user.CsoId, UnresolvedReferenceValue: $"uid={user.CsoId:N}"));
            }

            if (random.Next(4) == 0)
                rows.Add(new DriftComparisonSeed.MemberRow(NextGuid(random), UnresolvedReferenceValue: "uid=not-yet-imported"));
            if (random.Next(6) == 0)
                rows.Add(new DriftComparisonSeed.MemberRow(NextGuid(random), StringValue: "held as text"));
            if (g is 0 or 9)
                rows.Add(new DriftComparisonSeed.MemberRow(NextGuid(random), ReferenceValueId: groups[g + 1].CsoId));
            await seed.AddMemberRowsAsync(groups[g].CsoId, rows);

            // The Metaverse Object holds most of what the group holds (one group exactly), loses some, and gains others.
            var expected = g == 5
                ? heldUsers.Where(u => u.MvoId.HasValue).Select(u => u.MvoId!.Value).Distinct().ToList()
                : users.Where(u => u.MvoId.HasValue && random.Next(4) != 0).Select(u => u.MvoId!.Value).ToList();
            if (g is 0 or 9)
                expected.Add(groups[g + 1].MvoId!.Value);
            await seed.AddStaticMembersAsync(groups[g].MvoId!.Value, expected);
        }

        // A change already queued for one group, which its correction replaces or joins.
        await using (var ctx = NewContext())
        {
            ctx.PendingExports.Add(new PendingExport
            {
                Id = NextGuid(random), ConnectedSystemId = seed.SystemId, ConnectedSystemObjectId = groups[7].CsoId, ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Pending, SourceMetaverseObjectId = groups[7].MvoId, CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                AttributeValueChanges =
                {
                    new PendingExportAttributeValueChange
                    {
                        Id = NextGuid(random), AttributeId = seed.MemberAttributeId, ChangeType = PendingExportAttributeChangeType.Add,
                        UnresolvedReferenceValue = users[1].MvoId!.Value.ToString()
                    }
                }
            });
            await ctx.SaveChangesAsync();
        }

        await seed.AnalyseAsync();
        return seed.SystemId;
    }

    private static Guid NextGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    /// <summary>
    /// Everything the run leaves behind that the two paths could differ in, one line per row, sorted, with the ids the run
    /// draws fresh (Pending Exports', their changes', items' and change records') left out.
    /// </summary>
    private async Task<string> DescribeOutcomeAsync()
    {
        await using var ctx = NewContext();
        var lines = new List<string>();

        var exports = await ctx.PendingExports.AsNoTracking().Include(pe => pe.AttributeValueChanges).ToListAsync();
        lines.AddRange(exports.Select(pe => $"PendingExport {pe.ConnectedSystemObjectId} {pe.ChangeType} {pe.Status} {pe.SourceMetaverseObjectId} {pe.HasUnresolvedReferences}: " +
            string.Join("; ", pe.AttributeValueChanges
                .Select(c => $"{c.ChangeType} {c.AttributeId} {c.Status} {c.SyncRuleId} {c.SyncRuleName} {c.UnresolvedReferenceValue}{c.StringValue}")
                .Order(StringComparer.Ordinal))));

        var items = await ctx.ActivityRunProfileExecutionItems.AsNoTracking().Include(i => i.SyncOutcomes).ToListAsync();
        lines.AddRange(items.Select(i => $"Item {i.ConnectedSystemObjectId} {i.ObjectChangeType} {i.ErrorType} {i.ErrorMessage} {i.AttributeFlowCount}: " +
            string.Join("; ", i.SyncOutcomes.Select(o => $"{o.OutcomeType} {o.TargetEntityId} {o.DetailCount} {o.SyncRuleName} {o.DetailMessage}").Order(StringComparer.Ordinal))));

        lines.AddRange((await ctx.MetaverseObjects.AsNoTracking().ToListAsync())
            .Select(m => $"MetaverseObject {m.Id} {m.Status}"));
        lines.AddRange((await ctx.MetaverseObjectAttributeValues.AsNoTracking()
                .Select(v => new { MvoId = EF.Property<Guid>(v, "MetaverseObjectId"), v.AttributeId, v.ReferenceValueId, v.StringValue, v.ContributedBySystemId, v.NullValue })
                .ToListAsync())
            .Select(v => $"MetaverseValue {v.MvoId} {v.AttributeId} {v.ReferenceValueId} {v.StringValue} {v.ContributedBySystemId} {v.NullValue}"));
        lines.AddRange((await ctx.ConnectedSystemObjects.AsNoTracking().ToListAsync())
            .Select(c => $"ConnectedSystemObject {c.Id} {c.Status} {c.MetaverseObjectId} {c.JoinType} {c.SynchronisationPending}"));
        lines.AddRange((await ctx.MetaverseObjectChanges.AsNoTracking().ToListAsync())
            .Select(c => $"MetaverseObjectChange {c.MetaverseObjectId} {c.ChangeType} {c.ChangeInitiatorType} {c.SyncRuleId}"));
        lines.AddRange((await ctx.ConnectedSystemObjectChanges.AsNoTracking().ToListAsync())
            .Select(c => $"ConnectedSystemObjectChange {c.ConnectedSystemObjectId} {c.ChangeType}"));

        var activity = await ctx.Activities.AsNoTracking().SingleAsync();
        lines.Add($"Activity {activity.Status} {activity.ObjectsProcessed} {activity.ObjectsToProcess}");

        return string.Join("\n", lines.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Every Connected System Object value, one line per row, sorted; or only the groups' rows, without the reference each
    /// holds (a reference to an object the run deletes is cleared with it).
    /// </summary>
    private async Task<string> DescribeConnectedSystemObjectValuesAsync(bool groupRowsOnly = false)
    {
        await using var ctx = NewContext();
        var groupIds = (await ctx.ConnectedSystemObjects.AsNoTracking().Where(c => c.Type.Name == "group").Select(c => c.Id).ToListAsync()).ToHashSet();
        var values = await ctx.ConnectedSystemObjectAttributeValues.AsNoTracking()
            .Select(v => new { v.Id, CsoId = EF.Property<Guid>(v, "ConnectedSystemObjectId"), v.AttributeId, v.ReferenceValueId, v.UnresolvedReferenceValue, v.StringValue, v.IntValue })
            .ToListAsync();
        var lines = groupRowsOnly
            ? values.Where(v => groupIds.Contains(v.CsoId)).Select(v => $"{v.Id} {v.CsoId} {v.AttributeId} {v.UnresolvedReferenceValue} {v.StringValue} {v.IntValue}")
            : values.Select(v => $"{v.Id} {v.CsoId} {v.AttributeId} {v.ReferenceValueId} {v.UnresolvedReferenceValue} {v.StringValue} {v.IntValue}");
        var builder = new StringBuilder();
        foreach (var line in lines.Order(StringComparer.Ordinal))
            builder.AppendLine(line);
        return builder.ToString();
    }

    private static string? FirstDifference(string expected, string actual)
    {
        var (left, right) = (expected.Split('\n'), actual.Split('\n'));
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var (l, r) = (i < left.Length ? left[i] : "(none)", i < right.Length ? right[i] : "(none)");
            if (l != r)
                return $"line {i}:\n  whole:    {l}\n  left out: {r}";
        }

        return null;
    }
}
