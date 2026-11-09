// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the Connected System Object load Pending Export reconciliation reads each page with
/// (#2669). Reconciliation compares a reference by its id and its string, never by the object it points at, so the
/// load must not bring every referenced member's Connected System Object along with a group: at Scale500k25kGroups
/// that cost the Target Delta Import 26 s. Runs on a <c>NoTracking</c> context, matching the
/// <c>AsNoTracking</c> load, and opt-in via the <c>JIM_TEST_RESET_*</c> environment variables like the other
/// <c>RequiresPostgres</c> fixtures.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ReconciliationCsoLoadDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL reconciliation load tests.");

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

    private sealed record SeededGroup(
        int ConnectedSystemId,
        Guid GroupCsoId,
        int MemberAttributeId,
        Dictionary<string, Guid> MemberCsoIds,
        Dictionary<string, string> MemberDns,
        Dictionary<Guid, Guid?> MemberRowReferenceIds);

    /// <summary>
    /// A group whose target object holds Alice and Dave as resolved references (each keeping its DN, as resolution
    /// leaves it) and Erin as an unresolved one in a different case; Bob and Carol exist but are not members.
    /// </summary>
    private async Task<SeededGroup> SeedGroupAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband LDAP", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };
        var memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = csType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        var dnAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, IsSecondaryExternalId = true, Selected = true
        };
        csType.Attributes.Add(memberAttr);
        csType.Attributes.Add(dnAttr);
        seed.AddRange(connectorDefinition, system, csType);
        await seed.SaveChangesAsync();

        var dns = new Dictionary<string, string>();
        var members = new Dictionary<string, ConnectedSystemObject>();
        foreach (var (name, dn) in new[] { "alice", "bob", "carol", "dave", "erin" }
                     .Select(n => (n, $"uid={n},ou=People,dc=glitterband,dc=local")))
        {
            dns[name] = dn;
            var cso = new ConnectedSystemObject { Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), ConnectedSystemObject = cso, Attribute = dnAttr, StringValue = dn
            });
            members[name] = cso;
        }

        var groupCso = new ConnectedSystemObject { Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal };
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = groupCso, Attribute = dnAttr,
            StringValue = "cn=Team Alpha,ou=Groups,dc=glitterband,dc=local"
        });
        foreach (var name in new[] { "alice", "dave" })
        {
            groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), ConnectedSystemObject = groupCso, Attribute = memberAttr,
                ReferenceValue = members[name], UnresolvedReferenceValue = dns[name]
            });
        }
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = groupCso, Attribute = memberAttr,
            UnresolvedReferenceValue = dns["erin"].ToUpperInvariant()
        });

        seed.AddRange(members.Values);
        seed.Add(groupCso);
        await seed.SaveChangesAsync();

        return new SeededGroup(
            system.Id,
            groupCso.Id,
            memberAttr.Id,
            members.ToDictionary(m => m.Key, m => m.Value.Id),
            dns,
            groupCso.AttributeValues.Where(av => av.AttributeId == memberAttr.Id)
                .ToDictionary(av => av.Id, av => av.ReferenceValue?.Id));
    }

    /// <summary>
    /// The reconciliation load returns each object's attribute values with their ids, attribute definitions and the
    /// referenced object's id, but not the referenced objects themselves.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsCoreByIdsNoTrackingAsync_Group_ReturnsReferenceIdsWithoutReferencedObjectsAsync()
    {
        var seeded = await SeedGroupAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var csos = await repository.Sync.GetConnectedSystemObjectsCoreByIdsNoTrackingAsync(seeded.ConnectedSystemId, [seeded.GroupCsoId]);

        Assert.That(csos, Has.Count.EqualTo(1));
        var group = csos[0];
        var memberValues = group.AttributeValues.Where(av => av.AttributeId == seeded.MemberAttributeId).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.AttributeValues, Has.Count.EqualTo(4), "The DN and the three member values");
            Assert.That(group.AttributeValues.Select(av => av.Id), Is.All.Not.EqualTo(Guid.Empty));
            Assert.That(group.AttributeValues.Select(av => av.Attribute), Is.All.Not.Null,
                "Reconciliation compares by the attribute's type and case rules, so its definition must be loaded");
            Assert.That(memberValues.ToDictionary(av => av.Id, av => av.ReferenceValueId),
                Is.EquivalentTo(seeded.MemberRowReferenceIds), "Each reference keeps the id of the object it names");
            Assert.That(memberValues.Select(av => av.ReferenceValue), Is.All.Null,
                "The referenced members' Connected System Objects must not be loaded");
            Assert.That(memberValues.Select(av => av.UnresolvedReferenceValue),
                Is.EquivalentTo(new[] { seeded.MemberDns["alice"], seeded.MemberDns["dave"], seeded.MemberDns["erin"].ToUpperInvariant() }));
            Assert.That(ctx.ChangeTracker.Entries().Count(), Is.Zero, "The load must not track what it reads");
        }
    }

    /// <summary>
    /// Reconciliation over the lighter load reaches the same verdicts as over the full one, for a group with members
    /// added and removed: an added member present and a removed member absent are confirmed (by the referenced
    /// object's id, and an unresolved one by its DN ignoring case); an added member absent and a removed member still
    /// present are not confirmed, and retried.
    /// </summary>
    [Test]
    public async Task ReconcileCsoAgainstPendingExport_OverCoreLoad_SameVerdictsAsFullLoadAsync()
    {
        var seeded = await SeedGroupAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var memberAttribute = await ctx.ConnectedSystemAttributes.SingleAsync(a => a.Id == seeded.MemberAttributeId);

        PendingExportAttributeValueChange Change(string name, PendingExportAttributeChangeType changeType, bool resolved) => new()
        {
            Id = Guid.NewGuid(),
            Attribute = memberAttribute,
            AttributeId = memberAttribute.Id,
            ChangeType = changeType,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ResolvedReferenceCsoId = resolved ? seeded.MemberCsoIds[name] : null,
            StringValue = resolved ? seeded.MemberDns[name] : null,
            UnresolvedReferenceValue = resolved ? null : seeded.MemberDns[name]
        };

        // One set of change ids for both runs, so the verdicts compare change by change.
        var template = new Dictionary<string, PendingExportAttributeValueChange>
        {
            ["add alice"] = Change("alice", PendingExportAttributeChangeType.Add, resolved: true),
            ["add bob"] = Change("bob", PendingExportAttributeChangeType.Add, resolved: true),
            ["remove carol"] = Change("carol", PendingExportAttributeChangeType.Remove, resolved: true),
            ["remove dave"] = Change("dave", PendingExportAttributeChangeType.Remove, resolved: true),
            ["add erin"] = Change("erin", PendingExportAttributeChangeType.Add, resolved: false)
        };

        PendingExport NewPendingExport() => new()
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = seeded.ConnectedSystemId,
            ConnectedSystemObjectId = seeded.GroupCsoId,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Exported,
            AttributeValueChanges = new(template.Values.Select(c => new PendingExportAttributeValueChange
            {
                Id = c.Id, Attribute = c.Attribute, AttributeId = c.AttributeId, ChangeType = c.ChangeType, Status = c.Status,
                ResolvedReferenceCsoId = c.ResolvedReferenceCsoId, StringValue = c.StringValue,
                UnresolvedReferenceValue = c.UnresolvedReferenceValue
            }).ToList())
        };

        var engine = new SyncEngine();
        PendingExportReconciliationResult Reconcile(ConnectedSystemObject cso)
        {
            var result = new PendingExportReconciliationResult();
            engine.ReconcileCsoAgainstPendingExport(cso, NewPendingExport(), result);
            return result;
        }

        var fullLoad = (await repository.Sync.GetConnectedSystemObjectsByIdsNoTrackingAsync(seeded.ConnectedSystemId, [seeded.GroupCsoId])).Single();
        var coreLoad = (await repository.Sync.GetConnectedSystemObjectsCoreByIdsNoTrackingAsync(seeded.ConnectedSystemId, [seeded.GroupCsoId])).Single();

        var overFull = Reconcile(fullLoad);
        var overCore = Reconcile(coreLoad);

        var expectedConfirmed = new[] { template["add alice"].Id, template["remove carol"].Id, template["add erin"].Id };
        var expectedRetried = new[] { template["add bob"].Id, template["remove dave"].Id };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(overCore.ConfirmedChanges.Select(c => c.Id), Is.EquivalentTo(expectedConfirmed), "Confirmed over the Core load");
            Assert.That(overCore.RetryChanges.Select(c => c.Id), Is.EquivalentTo(expectedRetried), "Retried over the Core load");
            Assert.That(overCore.RetryChanges.Select(c => c.Status), Is.All.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed));
            Assert.That(overCore.RetryChanges.ToDictionary(c => c.Id, c => c.LastImportedValue),
                Is.EquivalentTo(overFull.RetryChanges.ToDictionary(c => c.Id, c => c.LastImportedValue)),
                "What the import showed is recorded the same way");
            Assert.That(overCore.ConfirmedChanges.Select(c => c.Id), Is.EquivalentTo(overFull.ConfirmedChanges.Select(c => c.Id)));
            Assert.That(overCore.RetryChanges.Select(c => c.Id), Is.EquivalentTo(overFull.RetryChanges.Select(c => c.Id)));
            Assert.That(overCore.PendingExportToDelete, Is.Null);
            Assert.That(overCore.PendingExportToUpdate?.Status, Is.EqualTo(overFull.PendingExportToUpdate?.Status));
            Assert.That(overCore.PendingExportToUpdate?.Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed));
        }
    }

    #region Probed values (#2669)

    private async Task<ConnectedSystemObjectTypeAttribute> LoadAttributeAsync(JimDbContext ctx, int attributeId) =>
        await ctx.ConnectedSystemAttributes.SingleAsync(a => a.Id == attributeId);

    private static PendingExportAttributeValueChange MemberChange(ConnectedSystemObjectTypeAttribute attribute,
        PendingExportAttributeChangeType changeType, Guid? resolvedTo = null, string? stringValue = null, string? unresolvedValue = null) => new()
    {
        Id = Guid.NewGuid(), Attribute = attribute, AttributeId = attribute.Id, ChangeType = changeType,
        Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
        ResolvedReferenceCsoId = resolvedTo, StringValue = stringValue, UnresolvedReferenceValue = unresolvedValue
    };

    private static PendingExport PendingExportFor(SeededGroup seeded, Guid csoId, IEnumerable<PendingExportAttributeValueChange> changes) => new()
    {
        Id = Guid.NewGuid(), ConnectedSystemId = seeded.ConnectedSystemId, ConnectedSystemObjectId = csoId,
        ChangeType = PendingExportChangeType.Update, Status = PendingExportStatus.Exported, AttributeValueChanges = new(changes.ToList())
    };

    /// <summary>
    /// Adds member rows to a seeded object: each resolved to the given object (or unresolved), holding the given DN.
    /// </summary>
    private async Task AddMemberRowsAsync(Guid csoId, int attributeId, params (Guid? ReferenceValueId, string Dn)[] rows)
    {
        await using var ctx = NewContext();
        var cso = await ctx.ConnectedSystemObjects.AsTracking().SingleAsync(c => c.Id == csoId);
        var attribute = await ctx.ConnectedSystemAttributes.AsTracking().SingleAsync(a => a.Id == attributeId);
        foreach (var (referenceValueId, dn) in rows)
        {
            ctx.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), ConnectedSystemObject = cso, Attribute = attribute,
                ReferenceValueId = referenceValueId, UnresolvedReferenceValue = dn
            });
        }

        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// A group object with no member values, in the seeded system and type.
    /// </summary>
    private async Task<Guid> AddEmptyGroupAsync(SeededGroup seeded)
    {
        await using var ctx = NewContext();
        var template = await ctx.ConnectedSystemObjects.AsTracking().Include(c => c.Type).Include(c => c.ConnectedSystem)
            .SingleAsync(c => c.Id == seeded.GroupCsoId);
        var group = new ConnectedSystemObject { Type = template.Type, ConnectedSystem = template.ConnectedSystem, Status = ConnectedSystemObjectStatus.Normal };
        ctx.Add(group);
        await ctx.SaveChangesAsync();
        return group.Id;
    }

    /// <summary>
    /// The reconciliation load leaves out the values of the attributes it is told to: the member values reconciliation
    /// probes instead. The object's DN and its name, which the execution item's snapshots read, are still loaded.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsCoreByIdsNoTrackingAsync_ExcludedAttribute_LeavesOnlyItsValuesOutAsync()
    {
        var seeded = await SeedGroupAsync();
        int cnAttributeId;
        await using (var seed = NewContext())
        {
            var type = await seed.ConnectedSystemObjectTypes.AsTracking().SingleAsync(t => t.Attributes.Any(a => a.Id == seeded.MemberAttributeId));
            var cn = new ConnectedSystemObjectTypeAttribute
            {
                Name = "cn", ConnectedSystemObjectType = type, Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, Selected = true
            };
            seed.Add(cn);
            var group = await seed.ConnectedSystemObjects.AsTracking().SingleAsync(c => c.Id == seeded.GroupCsoId);
            seed.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = group, Attribute = cn, StringValue = "Team Alpha" });
            await seed.SaveChangesAsync();
            cnAttributeId = cn.Id;
        }

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var group2 = (await repository.Sync.GetConnectedSystemObjectsCoreByIdsNoTrackingAsync(seeded.ConnectedSystemId, [seeded.GroupCsoId],
            [seeded.MemberAttributeId])).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group2.AttributeValues.Where(av => av.AttributeId == seeded.MemberAttributeId), Is.Empty, "The probed member values are not loaded");
            Assert.That(group2.AttributeValues, Has.Count.EqualTo(2), "The DN and the name are");
            Assert.That(group2.AttributeValues.Any(av => av.AttributeId == cnAttributeId && av.StringValue == "Team Alpha"), Is.True);
            Assert.That(group2.Name, Is.EqualTo("Team Alpha"));
            Assert.That(group2.AttributeValues.Select(av => av.Attribute), Is.All.Not.Null);
            Assert.That(ctx.ChangeTracker.Entries().Count(), Is.Zero, "The load must not track what it reads");
        }
    }

    /// <summary>
    /// Loading a group for reconciliation with its member attribute probed (left out) never reads the membership. Every
    /// statement the load runs is replayed under EXPLAIN ANALYZE and the rows read from the attribute values table counted:
    /// a filter leaving the attribute out reads every member row of the group to discard it.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsCoreByIdsNoTrackingAsync_MemberAttributeProbed_ReadsNoMemberRowAsync()
    {
        const int members = 20_000;
        var seeded = await LargeGroupImportSeed.SeedAsync(() => NewContext(), _connectionString, userCount: members, memberCount: members);

        var recorder = new CommandRecordingInterceptor();
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(recorder)
            .Options;
        List<ConnectedSystemObject> loaded;
        await using (var ctx = new JimDbContext(options))
        {
            loaded = await new PostgresDataRepository(ctx).Sync.GetConnectedSystemObjectsCoreByIdsNoTrackingAsync(seeded.SystemId,
                [seeded.GroupCsoId], [seeded.MemberAttributeId]);
            Assert.That(ctx.ChangeTracker.Entries().Count(), Is.Zero, "The load must not track what it reads");
        }

        var rowsRead = await PlanRowsRead.FromAsync(_connectionString, recorder.Commands, PlanRowsRead.AttributeValues);

        var group = loaded.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.AttributeValues.Any(av => av.AttributeId == seeded.MemberAttributeId), Is.False, "The probed member values are not loaded");
            Assert.That(group.AttributeValues, Has.Count.EqualTo(2), "The group's DN and GUID are");
            Assert.That(group.AttributeValues.All(av => av.Attribute != null && ReferenceEquals(av.ConnectedSystemObject, group)), Is.True);
            Assert.That(rowsRead, Is.LessThanOrEqualTo(10),
                $"the load read {rowsRead:N0} attribute value rows for a group of {members:N0} members; it needs the group's 2 other values");
        }
    }

    /// <summary>
    /// The database only narrows: for every change, the rows the probe returns include every row the .NET comparison
    /// matches over the full load. Covered: resolved rows by id, a row resolved to another object retaining a member's
    /// DN in capitals (which matches nothing, a resolved row naming its object by id), unresolved rows in other cases, and
    /// unresolved rows whose non-ASCII characters JIM folds into ASCII (the dotted capital I, the Kelvin sign), which only
    /// a database whose <c>lower()</c> folds them likewise finds. Then the same for a case-exact attribute, whose strings
    /// are sought exactly among unresolved rows (#2871); a change on it naming no object matches a resolved row by its
    /// DN, so the planner loads that pair in full instead.
    /// </summary>
    [Test]
    public async Task GetReconciliationProbeValuesAsync_EveryChange_CandidatesIncludeEveryDotNetMatchAsync()
    {
        var seeded = await SeedGroupAsync();
        await AddMemberRowsAsync(seeded.GroupCsoId, seeded.MemberAttributeId,
            (seeded.MemberCsoIds["bob"], seeded.MemberDns["carol"].ToUpperInvariant()),
            (null, "uid=nİck,ou=People,dc=glitterband,dc=local"),
            (null, "uid=Kate,ou=People,dc=glitterband,dc=local"),
            (null, "UID=Frank,OU=People,DC=glitterband,DC=local"));

        foreach (var caseExact in new[] { false, true })
        {
            await using (var update = NewContext())
            {
                await update.ConnectedSystemAttributes.Where(a => a.Id == seeded.MemberAttributeId)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.CaseExact, caseExact));
            }

            await using var ctx = NewContext();
            var repository = new PostgresDataRepository(ctx);
            var member = await LoadAttributeAsync(ctx, seeded.MemberAttributeId);
            string Dn(string uid) => $"uid={uid},ou=People,dc=glitterband,dc=local";

            var changes = new Dictionary<string, PendingExportAttributeValueChange>();
            foreach (var name in new[] { "alice", "bob", "carol", "dave", "erin" })
            {
                changes[$"add {name}"] = MemberChange(member, PendingExportAttributeChangeType.Add, seeded.MemberCsoIds[name], seeded.MemberDns[name]);
                changes[$"remove {name} in capitals"] = MemberChange(member, PendingExportAttributeChangeType.Remove, seeded.MemberCsoIds[name],
                    seeded.MemberDns[name].ToUpperInvariant());
            }
            foreach (var uid in new[] { "nick", "kate", "frank" })
                changes[$"remove {uid}"] = MemberChange(member, PendingExportAttributeChangeType.Remove, Guid.NewGuid(), Dn(uid));
            changes["remove all"] = MemberChange(member, PendingExportAttributeChangeType.RemoveAll);

            // Naming no object, so matched by string among resolved rows too: never probed, on either attribute (#2871).
            var namingNoObject = MemberChange(member, PendingExportAttributeChangeType.Remove, unresolvedValue: seeded.MemberDns["alice"]);
            var databaseFolds = await repository.Sync.DoesDatabaseFoldCaseLikeIdentifierKeyAsync();
            var withNamingNoObject = SyncEngine.PlanReconciliationProbes(
                [PendingExportFor(seeded, seeded.GroupCsoId, changes.Values.Append(namingNoObject))], databaseFolds);

            var plan = SyncEngine.PlanReconciliationProbes([PendingExportFor(seeded, seeded.GroupCsoId, changes.Values)], databaseFolds);
            var answer = (await repository.Sync.GetReconciliationProbeValuesAsync(plan.Questions)).Single();
            var fullLoad = (await repository.Sync.GetConnectedSystemObjectsByIdsNoTrackingAsync(seeded.ConnectedSystemId, [seeded.GroupCsoId])).Single();
            var memberRows = fullLoad.AttributeValues.Where(av => av.AttributeId == seeded.MemberAttributeId).ToList();
            var candidateIds = answer.CandidateValues.Select(c => c.Id).ToHashSet();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(withNamingNoObject.Questions, Is.Empty, $"caseExact={caseExact}");
                Assert.That(withNamingNoObject.FullLoadPairCount, Is.EqualTo(1), $"caseExact={caseExact}");
                Assert.That(memberRows.Count(row => SyncEngine.ValueExistsOnCso([row], namingNoObject)), Is.EqualTo(1),
                    $"caseExact={caseExact}: naming no object, the change matches Alice's resolved row by its DN");
                Assert.That(plan.FullLoadPairCount, Is.Zero, $"caseExact={caseExact}");
                Assert.That(answer.ProbedChangeIds, Is.EquivalentTo(changes.Values.Select(c => c.Id)));
                Assert.That(answer.CappedValueCount, Is.EqualTo(2), "Seven member rows, capped at two");
                foreach (var (name, change) in changes.Where(c => c.Value.ChangeType != PendingExportAttributeChangeType.RemoveAll))
                {
                    var matches = memberRows.Where(row => SyncEngine.ValueExistsOnCso([row], change)).Select(row => row.Id).ToList();
                    Assert.That(candidateIds.IsSupersetOf(matches), Is.True, $"caseExact={caseExact}: {name} matches rows the probe did not return");
                }

                if (!caseExact)
                {
                    Assert.That(memberRows.Count(row => SyncEngine.ValueExistsOnCso([row], changes["remove nick"])), Is.EqualTo(1),
                        "The dotted capital I folds into the ASCII DN");
                    Assert.That(memberRows.Count(row => SyncEngine.ValueExistsOnCso([row], changes["remove kate"])), Is.EqualTo(1),
                        "The Kelvin sign folds into the ASCII DN");
                    Assert.That(memberRows.Count(row => SyncEngine.ValueExistsOnCso([row], changes["remove carol in capitals"])), Is.Zero,
                        "A row resolved to Bob names Bob, whatever DN it retains");
                }
            }
        }
    }

    /// <summary>
    /// The count a verdict depends on is the attribute's value count capped at two: none, one, and two for several.
    /// </summary>
    [Test]
    public async Task GetReconciliationProbeValuesAsync_CountAsked_ReturnsCountCappedAtTwoAsync()
    {
        var seeded = await SeedGroupAsync();
        var emptyGroupId = await AddEmptyGroupAsync(seeded);
        var oneMemberGroupId = await AddEmptyGroupAsync(seeded);
        await AddMemberRowsAsync(oneMemberGroupId, seeded.MemberAttributeId, (seeded.MemberCsoIds["alice"], seeded.MemberDns["alice"]));

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var member = await LoadAttributeAsync(ctx, seeded.MemberAttributeId);

        var plan = SyncEngine.PlanReconciliationProbes(
            new[] { emptyGroupId, oneMemberGroupId, seeded.GroupCsoId }
                .Select(id => PendingExportFor(seeded, id, [MemberChange(member, PendingExportAttributeChangeType.RemoveAll)])),
            databaseFoldsCaseLikeIdentifierKey: true);
        var answers = (await repository.Sync.GetReconciliationProbeValuesAsync(plan.Questions)).ToDictionary(a => a.ConnectedSystemObjectId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answers, Has.Count.EqualTo(3));
            Assert.That(answers[emptyGroupId].CappedValueCount, Is.EqualTo(0));
            Assert.That(answers[oneMemberGroupId].CappedValueCount, Is.EqualTo(1));
            Assert.That(answers[seeded.GroupCsoId].CappedValueCount, Is.EqualTo(2));
            Assert.That(answers.Values.SelectMany(a => a.CandidateValues), Is.Empty, "A count-only question returns no rows");
        }
    }

    /// <summary>
    /// The test database (and JIM's own, C.UTF-8) folds the characters JIM folds into ASCII as JIM does, so reconciliation
    /// may ask it to match references ignoring case.
    /// </summary>
    [Test]
    public async Task DoesDatabaseFoldCaseLikeIdentifierKeyAsync_TestDatabase_ReturnsTrueAsync()
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        Assert.That(await repository.Sync.DoesDatabaseFoldCaseLikeIdentifierKeyAsync(), Is.True);
    }

    /// <summary>
    /// Reconciliation as it now runs (#2669), with the member attribute probed and left out of the load, reaches the same
    /// verdicts as over the full load, for a group with members added and removed (resolved changes, as export
    /// resolution writes them). What the import showed is recorded for each unconfirmed change on its own: the rows
    /// matching it, or that its value is absent. It deliberately no longer equals the full load's record, which lists
    /// every member of the group: on a large group that wrote the whole member list once per retried change.
    /// </summary>
    [Test]
    public async Task ReconcileCsoAgainstPendingExport_OverProbedValues_SameVerdictsAsFullLoadAsync()
    {
        var seeded = await SeedGroupAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var member = await LoadAttributeAsync(ctx, seeded.MemberAttributeId);

        var template = new Dictionary<string, PendingExportAttributeValueChange>();
        void Add(string key, string name, PendingExportAttributeChangeType changeType, string? dn = null) =>
            template[key] = MemberChange(member, changeType, seeded.MemberCsoIds[name], dn ?? seeded.MemberDns[name]);
        Add("add alice", "alice", PendingExportAttributeChangeType.Add);
        Add("add bob", "bob", PendingExportAttributeChangeType.Add);
        Add("remove carol", "carol", PendingExportAttributeChangeType.Remove);
        Add("remove dave", "dave", PendingExportAttributeChangeType.Remove);
        Add("add erin in another case", "erin", PendingExportAttributeChangeType.Add, seeded.MemberDns["erin"]);

        PendingExport NewPendingExport() => PendingExportFor(seeded, seeded.GroupCsoId, template.Values.Select(c => new PendingExportAttributeValueChange
        {
            Id = c.Id, Attribute = c.Attribute, AttributeId = c.AttributeId, ChangeType = c.ChangeType, Status = c.Status,
            ResolvedReferenceCsoId = c.ResolvedReferenceCsoId, StringValue = c.StringValue, UnresolvedReferenceValue = c.UnresolvedReferenceValue
        }));

        var engine = new SyncEngine();
        var fullLoad = (await repository.Sync.GetConnectedSystemObjectsByIdsNoTrackingAsync(seeded.ConnectedSystemId, [seeded.GroupCsoId])).Single();
        var overFull = new PendingExportReconciliationResult();
        engine.ReconcileCsoAgainstPendingExport(fullLoad, NewPendingExport(), overFull);

        var probedExport = NewPendingExport();
        var plan = SyncEngine.PlanReconciliationProbes([probedExport], await repository.Sync.DoesDatabaseFoldCaseLikeIdentifierKeyAsync());
        var answers = await repository.Sync.GetReconciliationProbeValuesAsync(plan.Questions);
        var probedLoad = (await repository.Sync.GetConnectedSystemObjectsCoreByIdsNoTrackingAsync(seeded.ConnectedSystemId, [seeded.GroupCsoId],
            answers.Select(a => a.AttributeId).ToList())).Single();
        var overProbe = new PendingExportReconciliationResult();
        engine.ReconcileCsoAgainstPendingExport(probedLoad, probedExport, overProbe, probedValuesByAttributeId: answers.ToDictionary(a => a.AttributeId));

        var expectedConfirmed = new[] { template["add alice"].Id, template["remove carol"].Id, template["add erin in another case"].Id };
        var expectedRetried = new[] { template["add bob"].Id, template["remove dave"].Id };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Questions, Has.Count.EqualTo(1), "The member attribute is probed");
            Assert.That(probedLoad.AttributeValues.Where(av => av.AttributeId == seeded.MemberAttributeId), Is.Empty);
            Assert.That(overFull.ConfirmedChanges.Select(c => c.Id), Is.EquivalentTo(expectedConfirmed), "Confirmed over the full load");
            Assert.That(overProbe.ConfirmedChanges.Select(c => c.Id), Is.EquivalentTo(expectedConfirmed), "Confirmed over the probe");
            Assert.That(overProbe.RetryChanges.Select(c => c.Id), Is.EquivalentTo(expectedRetried), "Retried over the probe");
            Assert.That(overProbe.RetryChanges.Select(c => c.Id), Is.EquivalentTo(overFull.RetryChanges.Select(c => c.Id)));
            Assert.That(overProbe.PendingExportToUpdate?.Status, Is.EqualTo(overFull.PendingExportToUpdate?.Status));
            Assert.That(overProbe.RetryChanges.ToDictionary(c => c.Id, c => c.LastImportedValue), Is.EquivalentTo(new Dictionary<Guid, string?>
            {
                [template["add bob"].Id] = "(value not present)",
                [template["remove dave"].Id] = seeded.MemberDns["dave"]
            }), "Bounded to what matched each change");
        }
    }

    /// <summary>
    /// The probe must find its rows through indexes keyed on what it is looking for, never by reading a group's members
    /// (#2669): the referenced ids on the reference index, exact and lowered strings alike on the partial functional
    /// index over unresolved references (#2871), and the capped count by stopping after two rows of the
    /// object's own attribute. The same shape the reference recall query's plan check holds that query to.
    /// </summary>
    [Test]
    public async Task GetReconciliationProbeValuesAsync_LargeGroup_PlanReadsOnlyTheSoughtRowsThroughIndexesAsync()
    {
        var seeded = await SeedGroupAsync();
        var aliceId = seeded.MemberCsoIds["alice"];

        await using var ctx = NewContext();
        var connection = (Npgsql.NpgsqlConnection)ctx.Database.GetDbConnection();
        await connection.OpenAsync();

        // A large group: 2,000 more members held as resolved references (each keeping its DN, as resolution leaves it)
        // and 20,000 still unresolved. Copied from seeded rows so every other column takes a valid value.
        await using (var populate = new Npgsql.NpgsqlCommand("""
            INSERT INTO "ConnectedSystemObjects"
            SELECT (jsonb_populate_record(cso, jsonb_build_object('Id', md5('member' || n)::uuid))).*
            FROM "ConnectedSystemObjects" AS cso, generate_series(1, 2000) AS n
            WHERE cso."Id" = @aliceId;

            INSERT INTO "ConnectedSystemObjectAttributeValues"
            SELECT (jsonb_populate_record(av, jsonb_build_object(
                'Id', md5('resolved' || n)::uuid,
                'ReferenceValueId', md5('member' || n)::uuid,
                'UnresolvedReferenceValue', 'uid=member' || n || ',ou=People,dc=glitterband,dc=local'))).*
            FROM "ConnectedSystemObjectAttributeValues" AS av, generate_series(1, 2000) AS n
            WHERE av."ConnectedSystemObjectId" = @groupCsoId AND av."ReferenceValueId" = @aliceId;

            INSERT INTO "ConnectedSystemObjectAttributeValues"
            SELECT (jsonb_populate_record(av, jsonb_build_object(
                'Id', md5('unresolved' || n)::uuid,
                'UnresolvedReferenceValue', 'uid=pending' || n || ',ou=People,dc=glitterband,dc=local'))).*
            FROM "ConnectedSystemObjectAttributeValues" AS av, generate_series(1, 20000) AS n
            WHERE av."ConnectedSystemObjectId" = @groupCsoId AND av."ReferenceValueId" IS NULL
              AND av."AttributeId" = @memberAttributeId;

            -- Each member holds its own DN, as every object does, so the statistics see a table of many objects' values
            -- rather than one group's: the count's plan is judged against the shape a real table has.
            INSERT INTO "ConnectedSystemObjectAttributeValues"
            SELECT (jsonb_populate_record(av, jsonb_build_object(
                'Id', md5('dn' || n)::uuid,
                'ConnectedSystemObjectId', md5('member' || n)::uuid,
                'StringValue', 'uid=member' || n || ',ou=People,dc=glitterband,dc=local'))).*
            FROM "ConnectedSystemObjectAttributeValues" AS av, generate_series(1, 2000) AS n
            WHERE av."ConnectedSystemObjectId" = @aliceId;

            ANALYZE "ConnectedSystemObjects";
            ANALYZE "ConnectedSystemObjectAttributeValues";
            """, connection))
        {
            populate.Parameters.AddWithValue("groupCsoId", seeded.GroupCsoId);
            populate.Parameters.AddWithValue("aliceId", aliceId);
            populate.Parameters.AddWithValue("memberAttributeId", seeded.MemberAttributeId);
            await populate.ExecuteNonQueryAsync();
        }

        async Task<List<string>> ExplainAsync(Npgsql.NpgsqlCommand explain)
        {
            var lines = new List<string>();
            await using var reader = await explain.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                lines.Add(reader.GetString(0));
            return lines;
        }

        await using var explainValues = new Npgsql.NpgsqlCommand(
            "EXPLAIN " + JIM.PostgresData.Repositories.SyncRepository.ReconciliationProbeValuesSql, connection);
        JIM.PostgresData.Repositories.SyncRepository.AddReconciliationProbeValuesParameters(explainValues,
            [seeded.GroupCsoId], [seeded.MemberAttributeId], [aliceId], [seeded.MemberDns["alice"]], [seeded.MemberDns["erin"]]);
        var valuesPlan = await ExplainAsync(explainValues);

        await using var explainCount = new Npgsql.NpgsqlCommand(
            "EXPLAIN " + JIM.PostgresData.Repositories.SyncRepository.ReconciliationProbeCountSql, connection);
        JIM.PostgresData.Repositories.SyncRepository.AddReconciliationProbeCountParameters(explainCount,
            [seeded.GroupCsoId], [seeded.MemberAttributeId]);
        var countPlan = await ExplainAsync(explainCount);

        var valuesText = string.Join("\n", valuesPlan);
        var countText = string.Join("\n", countPlan);
        var indexConditions = valuesPlan.Where(line => line.Contains("Index Cond:")).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(valuesPlan.Concat(countPlan).Any(line => line.Contains("Seq Scan on \"ConnectedSystemObjectAttributeValues\"")), Is.False,
                "The probe scans every attribute value.\n" + valuesText + "\n" + countText);
            Assert.That(indexConditions.Any(line => line.Contains("\"ReferenceValueId\" = ANY")), Is.True,
                "The referenced id must be an index condition.\n" + valuesText);
            // Both string arms seek outstanding values by the lowered string (#2871); the exact arm then compares exactly.
            Assert.That(indexConditions.Count(line => line.Contains("lower(")), Is.EqualTo(2),
                "The lowered string must be an index condition of both string arms.\n" + valuesText);
            Assert.That(valuesPlan.Where(line => line.Contains("Filter:")).Any(line => line.Contains("lower(")), Is.False,
                "The string must not be compared row by row after a scan.\n" + valuesText);
            Assert.That(countPlan.Any(line => line.Contains("Limit")), Is.True,
                "The count must stop after two rows.\n" + countText);
            Assert.That(countPlan.Any(line => line.Contains("Index Cond:") && line.Contains("\"ConnectedSystemObjectId\" = p.cso_id")), Is.True,
                "The count must read the object's own rows through the (object, attribute) index.\n" + countText);
        }
    }

    #endregion
}
