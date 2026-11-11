// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;
using Activity = JIM.Models.Activities.Activity;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// The page batch behind import joins (#2020) decides every object on a page exactly as the per-object lookups did,
/// on real PostgreSQL: the same page is synchronised once with each, from identical databases, and every object's
/// outcome compared. The page mixes what the batch must not change: Object Matching Rule precedence, an ambiguous
/// match, no match, several rules and several import Synchronisation Rules (agreeing and disagreeing), Text in another
/// case under a case-insensitive rule and a case-sensitive one, each matchable data type, an obsolete object's pending
/// disconnection then a rejoin to its Metaverse Object, a Metaverse Object already joined from this system, two
/// objects matching one Metaverse Object, and two objects sharing a value no Metaverse Object holds yet (the first
/// projects; the database cannot show the second the projection until the page flushes, with or without the batch).
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class JoinMatchBatchEquivalenceDatabaseTests
{
    private string _connectionString = null!;
    private ActivityListener _listener = null!;
    private volatile bool _recording;
    private readonly ConcurrentQueue<(string Name, bool Batched)> _spans = new();

    // Fixed ids, so both runs see identical databases and the page holds the objects in this order.
    private static readonly Guid M1 = Id(0xA1), M2 = Id(0xA2), M3 = Id(0xA3), M4 = Id(0xA4), M5 = Id(0xA5), M6 = Id(0xA6),
        M7 = Id(0xA7), M8 = Id(0xA8), M9 = Id(0xA9), M10 = Id(0xAA), M11 = Id(0xAB), M12 = Id(0xAC), M13 = Id(0xAD), M14 = Id(0xAE),
        M15 = Id(0xAF), M16 = Id(0xB0), M17 = Id(0xB1);
    private static readonly Guid ObjectKey = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL join batch equivalence tests.");
        _connectionString = connectionString!;

        TestUtilities.SetEnvironmentVariables();
        using (var ctx = NewContext())
            ctx.Database.Migrate();

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "JIM.Sync",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span =>
            {
                if (_recording && span.DisplayName.StartsWith("Join.", StringComparison.Ordinal))
                    _spans.Enqueue((span.DisplayName, span.GetTagItem("batched") is true));
            }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _listener?.Dispose();

    [Test]
    public async Task FullSync_MixedPage_BatchedJoinsDecideEveryObjectAsThePerObjectLookupsDoAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        var systemId = await SeedAsync();
        var (perObject, perObjectSpans) = await RunAsync(systemId, ConnectedSystemRunType.FullSynchronisation, batched: false);

        await PostgresTestDatabase.ResetAsync(_connectionString);
        systemId = await SeedAsync();
        var (batched, batchedSpans) = await RunAsync(systemId, ConnectedSystemRunType.FullSynchronisation, batched: true);

        AssertTheScenarioIsExercised(batched);
        Assert.That(batched, Is.EqualTo(perObject), "every object's outcome, batched against per-object");
        AssertTheBatchAnsweredEveryJoin(perObjectSpans, batchedSpans);
    }

    [Test]
    public async Task DeltaSync_MixedPage_BatchedJoinsDecideEveryObjectAsThePerObjectLookupsDoAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        var systemId = await SeedAsync();
        var (perObject, perObjectSpans) = await RunAsync(systemId, ConnectedSystemRunType.DeltaSynchronisation, batched: false);

        await PostgresTestDatabase.ResetAsync(_connectionString);
        systemId = await SeedAsync();
        var (batched, batchedSpans) = await RunAsync(systemId, ConnectedSystemRunType.DeltaSynchronisation, batched: true);

        AssertTheScenarioIsExercised(batched);
        Assert.That(batched, Is.EqualTo(perObject), "every object's outcome, batched against per-object");
        AssertTheBatchAnsweredEveryJoin(perObjectSpans, batchedSpans);
    }

    /// <summary>
    /// The outcomes the page is built to produce, so an equivalence that held only because nothing happened fails here.
    /// </summary>
    private static void AssertTheScenarioIsExercised(IReadOnlyList<string> outcomes)
    {
        var byObject = outcomes.ToDictionary(o => o[..o.IndexOf(':')]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(byObject["C01"], Does.Contain("joined M1;").And.Contain("Joined"), "the Employee ID rule matches");
            Assert.That(byObject["C02"], Does.Contain("joined M2;"), "ignoring case, 'E2' matches 'e2'");
            Assert.That(byObject["C03"], Does.Contain("AmbiguousMatch").And.Contain("candidates M3,M4"), "two identities hold 'DUP'");
            Assert.That(byObject["C04"], Does.Contain("joined M5;"), "no Employee ID: the mail rule, next in order, matches");
            Assert.That(byObject["C05"], Does.Contain("joined M6;"), "Employee ID is tried before mail and wins");
            Assert.That(byObject["C06"], Does.Contain("projected by C06"), "respecting case, 'A@X' does not match 'a@x'");
            Assert.That(byObject["C07"], Does.Contain("joined M8;"), "Number");
            Assert.That(byObject["C08"], Does.Contain("joined M9;"), "LongNumber");
            Assert.That(byObject["C09"], Does.Contain("joined M10;"), "Decimal, differing only in scale");
            Assert.That(byObject["C10"], Does.Contain("joined M11;"), "Guid");
            Assert.That(byObject["C11"], Does.Contain("projected by C11"), "nothing holds the value");
            Assert.That(byObject["C12"], Does.Contain("deleted").Or.Contain("Obsolete"), "the obsolete object is torn down");
            Assert.That(byObject["C13"], Does.Contain("joined M12;"), "the pending disconnection frees M12 for the rejoin");
            Assert.That(byObject["C15"], Does.Contain("CouldNotJoinDueToExistingJoin"), "M13 already has an object from this system");
            Assert.That(byObject["C16"], Does.Contain("joined M14;"), "the first of two objects matching M14 joins");
            Assert.That(byObject["C17"], Does.Contain("CouldNotJoinDueToExistingJoin"), "the second is refused within the page");
            Assert.That(byObject["C18"], Does.Contain("projected by C18"), "the first object with a new value projects");
            Assert.That(byObject["C19"], Does.Contain("projected by C19"), "the second cannot see the first's projection");
            Assert.That(byObject["C20"], Does.Contain("joined M15;"), "both import rules find M15");
            Assert.That(byObject["C21"], Does.Contain("AmbiguousMatch"), "the import rules disagree");
        }
    }

    /// <summary>
    /// The per-object run looked each join up itself; the batched run's joins all read the batch, so its only lookups are
    /// the batch's own.
    /// </summary>
    private static void AssertTheBatchAnsweredEveryJoin(
        IReadOnlyList<(string Name, bool Batched)> perObjectSpans, IReadOnlyList<(string Name, bool Batched)> batchedSpans)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(perObjectSpans.Count(s => s.Name == "Join.MatchLookup"), Is.GreaterThan(20), "precondition: a lookup per object");
            Assert.That(batchedSpans.Count(s => s.Name == "Join.MatchLookup" && !s.Batched), Is.Zero, "per-object lookups");
            Assert.That(batchedSpans.Count(s => s.Name == "Join.LoadMatch"), Is.Zero, "per-object loads");
            Assert.That(batchedSpans.Count(s => s.Name == "Join.ExistingJoinCount"), Is.Zero, "per-object join counts");
            Assert.That(batchedSpans.Count(s => s.Name == "Join.MatchLookup" && s.Batched), Is.LessThanOrEqualTo(10),
                "one batched lookup per lookup key and round, however many objects");
        }
    }

    /// <summary>
    /// Synchronises the HR system once, as the Worker does, and describes each of its objects' outcomes: its join, the
    /// Metaverse Object it ends on (by name, a projection by the object that made it), and each execution item's change,
    /// error and candidates.
    /// </summary>
    private async Task<(List<string> Outcomes, List<(string Name, bool Batched)> Spans)> RunAsync(int systemId, ConnectedSystemRunType runType, bool batched)
    {
        Guid activityId;
        _spans.Clear();
        _recording = true;
        try
        {
            await using var ctx = NewContext(trackAll: true);
            var repository = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repository, syncRepository: repository.Sync);
            var system = await jim.ConnectedSystems.GetConnectedSystemAsync(systemId, withChangeTracking: true)
                ?? throw new InvalidOperationException("Connected System not found.");
            var runProfile = system.RunProfiles!.Single(rp => rp.RunType == runType);
            var activity = new Activity
            {
                TargetName = runProfile.Name, Status = ActivityStatus.InProgress, ConnectedSystemId = system.Id,
                ConnectedSystemRunProfileId = runProfile.Id, ConnectedSystemRunType = runType
            };
            ctx.Activities.Add(activity);
            await ctx.SaveChangesAsync();
            activityId = activity.Id;

            if (runType == ConnectedSystemRunType.FullSynchronisation)
            {
                await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), jim.SyncRepository,
                    system, runProfile, activity, new CancellationTokenSource()) { PrefetchJoinMatchesForTests = batched }.PerformFullSyncAsync();
            }
            else
            {
                await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), jim.SyncRepository,
                    system, runProfile, activity, new CancellationTokenSource()) { PrefetchJoinMatchesForTests = batched }.PerformDeltaSyncAsync();
            }
        }
        finally
        {
            _recording = false;
        }

        await using var read = NewContext();
        var objects = await read.ConnectedSystemObjects.Where(c => c.ConnectedSystemId == systemId)
            .Select(c => new { c.Id, c.MetaverseObjectId, c.JoinType, c.Status }).ToListAsync();
        var items = await read.ActivityRunProfileExecutionItems.Where(i => i.ActivityId == activityId)
            .Select(i => new { i.Id, i.ConnectedSystemObjectId, i.ObjectChangeType, i.ErrorType }).ToListAsync();
        var itemIds = items.Select(i => i.Id).ToList();
        var candidates = await read.ActivityRunProfileExecutionItemMatchCandidates.Where(c => itemIds.Contains(c.ActivityRunProfileExecutionItemId))
            .Select(c => new { c.ActivityRunProfileExecutionItemId, c.CandidateId }).ToListAsync();

        var names = new Dictionary<Guid, string>
        {
            [M1] = "M1", [M2] = "M2", [M3] = "M3", [M4] = "M4", [M5] = "M5", [M6] = "M6", [M7] = "M7", [M8] = "M8",
            [M9] = "M9", [M10] = "M10", [M11] = "M11", [M12] = "M12", [M13] = "M13", [M14] = "M14", [M15] = "M15", [M16] = "M16",
            [M17] = "M17"
        };
        foreach (var projecting in objects.Where(o => o.JoinType == ConnectedSystemObjectJoinType.Projected && o.MetaverseObjectId.HasValue))
            names[projecting.MetaverseObjectId!.Value] = $"projected by {Label(projecting.Id)}";
        string Name(Guid id) => names.GetValueOrDefault(id, "unknown");

        var outcomes = Enumerable.Range(1, 21).Select(n => Cso(n)).Select(csoId =>
        {
            var held = objects.SingleOrDefault(o => o.Id == csoId);
            var joined = held == null ? "deleted" : $"{held.JoinType} {held.Status} {(held.MetaverseObjectId is { } mvoId ? "joined " + Name(mvoId) : "unjoined")}";
            var itemDescriptions = items.Where(i => i.ConnectedSystemObjectId == csoId)
                .Select(i => $"{i.ObjectChangeType}/{i.ErrorType} candidates " + string.Join(",", candidates
                    .Where(c => c.ActivityRunProfileExecutionItemId == i.Id).Select(c => Name(c.CandidateId)).Order(StringComparer.Ordinal)))
                .Order(StringComparer.Ordinal);
            return $"{Label(csoId)}: {joined}; {string.Join("; ", itemDescriptions)}";
        }).ToList();

        var mvoCount = await read.MetaverseObjects.CountAsync();
        outcomes.Add($"Metaverse Objects: {mvoCount}");
        return (outcomes, _spans.ToList());
    }

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:x12}");
    private static Guid Cso(int n) => Guid.Parse($"10000000-0000-0000-0000-{n:x12}");
    private static string Label(Guid csoId) => $"C{Convert.ToInt32(csoId.ToString()[^12..], 16):00}";

    /// <summary>
    /// The Person type with a matchable attribute of each data type; fourteen identities; and the HR system's page of
    /// twenty-one objects, under two import Synchronisation Rules: "HR" (projects; matches on Employee ID ignoring case,
    /// then mail respecting case, then each other data type) and "HR Alt" (does not project; matches on Alt ID ignoring
    /// case). Returns the HR system's id.
    /// </summary>
    private async Task<int> SeedAsync()
    {
        int systemId, typeId;
        Dictionary<string, int> attributeIds;
        await using (var seed = NewContext())
        {
            foreach (var (key, valueType, value) in new[]
                     {
                         (Constants.SettingKeys.SyncPageSize, ServiceSettingValueType.Integer, "500"),
                         ("ChangeTracking.CsoChanges.Enabled", ServiceSettingValueType.Boolean, "false"),
                         ("ChangeTracking.MvoChanges.Enabled", ServiceSettingValueType.Boolean, "false")
                     })
            {
                seed.ServiceSettingItems.Add(new ServiceSetting
                {
                    Key = key, DisplayName = key, Category = ServiceSettingCategory.Synchronisation,
                    ValueType = valueType, DefaultValue = value, Value = value
                });
            }

            var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People", DeletionRule = MetaverseObjectDeletionRule.Manual };
            MetaverseAttribute MvAttribute(string name, AttributeDataType type) => new()
            {
                Name = name, Type = type, AttributePlurality = AttributePlurality.SingleValued, MetaverseObjectTypes = [mvType]
            };
            var mvDisplayName = MvAttribute("Display Name", AttributeDataType.Text);
            var mvEmployeeId = MvAttribute("Employee ID", AttributeDataType.Text);
            var mvMail = MvAttribute("Mail", AttributeDataType.Text);
            var mvStaffNumber = MvAttribute("Staff Number", AttributeDataType.Number);
            var mvBadge = MvAttribute("Badge", AttributeDataType.LongNumber);
            var mvRate = MvAttribute("Rate", AttributeDataType.Decimal);
            var mvKey = MvAttribute("Object Key", AttributeDataType.Guid);
            var mvAltId = MvAttribute("Alt ID", AttributeDataType.Text);

            var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "HR", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule };
            var type = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = system, Selected = true };
            ConnectedSystemObjectTypeAttribute CsAttribute(string name, AttributeDataType dataType, bool externalId = false)
            {
                var attribute = new ConnectedSystemObjectTypeAttribute
                {
                    Name = name, ConnectedSystemObjectType = type, Type = dataType, AttributePlurality = AttributePlurality.SingleValued,
                    Selected = true, IsExternalId = externalId
                };
                type.Attributes.Add(attribute);
                return attribute;
            }
            var csId = CsAttribute("id", AttributeDataType.Text, externalId: true);
            var csDisplayName = CsAttribute("displayName", AttributeDataType.Text);
            var csEmployeeId = CsAttribute("employeeId", AttributeDataType.Text);
            var csMail = CsAttribute("mail", AttributeDataType.Text);
            var csStaffNumber = CsAttribute("staffNumber", AttributeDataType.Number);
            var csBadge = CsAttribute("badge", AttributeDataType.LongNumber);
            var csRate = CsAttribute("rate", AttributeDataType.Decimal);
            var csKey = CsAttribute("objectKey", AttributeDataType.Guid);
            var csAltId = CsAttribute("altId", AttributeDataType.Text);

            seed.AddRange(connectorDefinition, system, type, mvType, mvDisplayName, mvEmployeeId, mvMail, mvStaffNumber, mvBadge, mvRate, mvKey, mvAltId);
            await seed.SaveChangesAsync();
            foreach (var runType in new[] { ConnectedSystemRunType.FullSynchronisation, ConnectedSystemRunType.DeltaSynchronisation })
                seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile { ConnectedSystemId = system.Id, Name = runType.ToString(), RunType = runType });

            ObjectMatchingRule Matching(SyncRule rule, int order, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source, bool caseSensitive) => new()
            {
                Order = order, CaseSensitive = caseSensitive, SyncRule = rule, TargetMetaverseAttribute = target,
                Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = source }]
            };

            var hr = new SyncRule
            {
                Name = "HR", ConnectedSystem = system, Direction = SyncRuleDirection.Import, Enabled = true,
                ConnectedSystemObjectType = type, MetaverseObjectType = mvType, ProjectToMetaverse = true
            };
            hr.AttributeFlowRules.Add(new SyncRuleMapping
            {
                SyncRule = hr, TargetMetaverseAttribute = mvDisplayName,
                Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = csDisplayName } }
            });
            // Deliberately added out of order: precedence is the Order, not the order of the list.
            hr.ObjectMatchingRules.Add(Matching(hr, 1, mvMail, csMail, caseSensitive: true));
            hr.ObjectMatchingRules.Add(Matching(hr, 0, mvEmployeeId, csEmployeeId, caseSensitive: false));
            hr.ObjectMatchingRules.Add(Matching(hr, 2, mvStaffNumber, csStaffNumber, caseSensitive: false));
            hr.ObjectMatchingRules.Add(Matching(hr, 3, mvBadge, csBadge, caseSensitive: false));
            hr.ObjectMatchingRules.Add(Matching(hr, 4, mvRate, csRate, caseSensitive: false));
            hr.ObjectMatchingRules.Add(Matching(hr, 5, mvKey, csKey, caseSensitive: false));

            var hrAlt = new SyncRule
            {
                Name = "HR Alt", ConnectedSystem = system, Direction = SyncRuleDirection.Import, Enabled = true,
                ConnectedSystemObjectType = type, MetaverseObjectType = mvType, ProjectToMetaverse = false
            };
            hrAlt.ObjectMatchingRules.Add(Matching(hrAlt, 0, mvAltId, csAltId, caseSensitive: false));
            seed.SyncRules.AddRange(hr, hrAlt);
            await seed.SaveChangesAsync();

            // The identities. M3 and M4 share an Employee ID; M7's mail is what C05 also holds, behind its Employee ID.
            void Identity(Guid id, params (MetaverseAttribute Attribute, Action<MetaverseObjectAttributeValue> Set)[] values)
            {
                var mvo = new MetaverseObject { Id = id, Type = mvType };
                foreach (var (attribute, set) in values)
                {
                    var value = new MetaverseObjectAttributeValue { Attribute = attribute, MetaverseObject = mvo };
                    set(value);
                    mvo.AttributeValues.Add(value);
                }
                seed.MetaverseObjects.Add(mvo);
            }
            Identity(M1, (mvEmployeeId, v => v.StringValue = "E1"), (mvMail, v => v.StringValue = "a@x"));
            Identity(M2, (mvEmployeeId, v => v.StringValue = "e2"));
            Identity(M3, (mvEmployeeId, v => v.StringValue = "DUP"));
            Identity(M4, (mvEmployeeId, v => v.StringValue = "dup"));
            Identity(M5, (mvMail, v => v.StringValue = "m5@x"));
            Identity(M6, (mvEmployeeId, v => v.StringValue = "E6"), (mvMail, v => v.StringValue = "m6@x"));
            Identity(M7, (mvMail, v => v.StringValue = "e6@x"));
            Identity(M8, (mvStaffNumber, v => v.IntValue = 100));
            Identity(M9, (mvBadge, v => v.LongValue = 9_000_000_000L));
            Identity(M10, (mvRate, v => v.DecimalValue = 12.50m));
            Identity(M11, (mvKey, v => v.GuidValue = ObjectKey));
            Identity(M12, (mvEmployeeId, v => v.StringValue = "E12"));
            Identity(M13, (mvEmployeeId, v => v.StringValue = "E13"));
            Identity(M14, (mvEmployeeId, v => v.StringValue = "E14"));
            Identity(M15, (mvEmployeeId, v => v.StringValue = "E20"), (mvAltId, v => v.StringValue = "ALT20"));
            Identity(M16, (mvEmployeeId, v => v.StringValue = "E21"));
            Identity(M17, (mvAltId, v => v.StringValue = "ALT17"));
            await seed.SaveChangesAsync();

            (systemId, typeId) = (system.Id, type.Id);
            attributeIds = new Dictionary<string, int>
            {
                ["id"] = csId.Id, ["displayName"] = csDisplayName.Id, ["employeeId"] = csEmployeeId.Id, ["mail"] = csMail.Id,
                ["staffNumber"] = csStaffNumber.Id, ["badge"] = csBadge.Id, ["rate"] = csRate.Id, ["objectKey"] = csKey.Id,
                ["altId"] = csAltId.Id
            };
        }

        ConnectedSystemObject Object(int n, Action<ConnectedSystemObject, Func<string, ConnectedSystemObjectAttributeValue>>? values = null,
            ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal, Guid? joinedTo = null)
        {
            var cso = new ConnectedSystemObject
            {
                Id = Cso(n), ConnectedSystemId = systemId, TypeId = typeId, ExternalIdAttributeId = attributeIds["id"], Status = status,
                Created = DateTime.UtcNow.AddHours(-2), SynchronisationPending = true
            };
            ConnectedSystemObjectAttributeValue Value(string attribute)
            {
                var value = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeIds[attribute] };
                cso.AttributeValues.Add(value);
                return value;
            }
            Value("id").StringValue = $"hr-{n}";
            Value("displayName").StringValue = $"Person {n}";
            values?.Invoke(cso, Value);
            if (joinedTo is { } mvoId)
            {
                cso.MetaverseObjectId = mvoId;
                cso.JoinType = ConnectedSystemObjectJoinType.Joined;
                cso.DateJoined = DateTime.UtcNow.AddHours(-1);
            }
            return cso;
        }

        var page = new List<ConnectedSystemObject>
        {
            Object(1, (_, v) => v("employeeId").StringValue = "E1"),
            Object(2, (_, v) => v("employeeId").StringValue = "E2"),
            Object(3, (_, v) => v("employeeId").StringValue = "Dup"),
            Object(4, (_, v) => v("mail").StringValue = "m5@x"),
            Object(5, (_, v) => { v("employeeId").StringValue = "E6"; v("mail").StringValue = "e6@x"; }),
            Object(6, (_, v) => v("mail").StringValue = "A@X"),
            Object(7, (_, v) => v("staffNumber").IntValue = 100),
            Object(8, (_, v) => v("badge").LongValue = 9_000_000_000L),
            Object(9, (_, v) => v("rate").DecimalValue = 12.5m),
            Object(10, (_, v) => v("objectKey").GuidValue = ObjectKey),
            Object(11, (_, v) => v("employeeId").StringValue = "NOBODY"),
            Object(12, (_, v) => v("employeeId").StringValue = "E12", ConnectedSystemObjectStatus.Obsolete, joinedTo: M12),
            Object(13, (_, v) => v("employeeId").StringValue = "e12"),
            Object(14, (_, v) => v("employeeId").StringValue = "E13", joinedTo: M13),
            Object(15, (_, v) => v("employeeId").StringValue = "E13"),
            Object(16, (_, v) => v("employeeId").StringValue = "E14"),
            Object(17, (_, v) => v("employeeId").StringValue = "e14"),
            Object(18, (_, v) => v("employeeId").StringValue = "NEW"),
            Object(19, (_, v) => v("employeeId").StringValue = "NEW"),
            Object(20, (_, v) => { v("employeeId").StringValue = "E20"; v("altId").StringValue = "alt20"; }),
            Object(21, (_, v) => { v("employeeId").StringValue = "E21"; v("altId").StringValue = "ALT17"; })
        };

        await using var ctx = NewContext();
        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync(page);
        return systemId;
    }
}
