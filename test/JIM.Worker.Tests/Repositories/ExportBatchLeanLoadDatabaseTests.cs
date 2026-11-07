// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.DirectoryServices.Protocols;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL coverage of the export batch's lean load (#2816): a large group's member attribute is loaded with
/// only the members its Pending Export's changes name, beside how many it holds, and everything else is loaded whole.
/// Guards that the outcomes are those of the whole load (the calculator's delta, the values applied, the LDAP Connector's
/// modifications, placeholder member included), that what was not loaded survives the batch untouched, and that an
/// object loaded in part is never saved back. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent. <c>SetUp</c>
/// TRUNCATEs every table.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ExportBatchLeanLoadDatabaseTests
{
    private const string PlaceholderDn = "cn=placeholder";
    private static readonly DateTime T0 = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);
    private static readonly ActivitySource TestSource = new("JIM.Tests.ExportBatchLeanLoad");

    private string _connectionString = null!;
    private ActivityListener? _listener;
    private readonly ConcurrentQueue<System.Diagnostics.Activity> _stopped = new();

    private JimDbContext NewContext(QueryTrackingBehavior tracking = QueryTrackingBehavior.TrackAll)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        var context = new JimDbContext(options);
        context.ChangeTracker.QueryTrackingBehavior = tracking;
        return context;
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL export batch lean load tests.");

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

    // ─── What the load reads ───

    [Test]
    public async Task GetPendingExportsByIdsAsync_LargeGroupLosingMembers_LoadsOnlyTheNamedMembersAndCountsTheRestAsync()
    {
        var s = await SeedSchemaAsync();
        var group = await SeedGroupAsync(s, memberCount: 50);
        var removed = group.Members.Take(3).ToList();
        var exportId = await SeedExportAsync(s, group.Id, T0, removed.Select(m => Remove(s, m)).ToArray());

        await using var ctx = NewContext();
        var batch = await new PostgresDataRepository(ctx).Sync.GetPendingExportsByIdsAsync([exportId]);

        var cso = batch.Single().ConnectedSystemObject!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.AttributeValues.Where(v => v.AttributeId == s.MemberAttributeId).Select(v => v.ReferenceValueId),
                Is.EquivalentTo(removed.Select(m => (Guid?)m.UserId)), "only the members the changes name");
            Assert.That(cso.AttributeValues.Where(v => v.AttributeId == s.DnAttributeId).Select(v => v.StringValue), Is.EqualTo(new[] { group.Dn }));
            Assert.That(cso.AttributeValues.All(v => v.Attribute?.Name != null && ReferenceEquals(v.ConnectedSystemObject, cso)), Is.True,
                "each loaded value carries its attribute and its object, as the whole load's did");
            Assert.That(cso.PartiallyLoadedAttributes![s.MemberAttributeId].AtLeastValueCount, Is.EqualTo(5), "three changes plus two");
            Assert.That(cso.HeldValueCount("member"), Is.EqualTo(5));
            Assert.That(cso.SecondaryExternalIdAttributeValue?.StringValue, Is.EqualTo(group.Dn));
            Assert.That(ctx.ChangeTracker.Entries().Any(), Is.False, "the batch is loaded untracked");
        }
    }

    [Test]
    public async Task GetPendingExportsByIdsAsync_GroupTheChangesCouldEmpty_LoadsItWholeAsync()
    {
        var s = await SeedSchemaAsync();
        var group = await SeedGroupAsync(s, memberCount: 3);
        var exportId = await SeedExportAsync(s, group.Id, T0, group.Members.Take(2).Select(m => Remove(s, m)).ToArray());

        await using var ctx = NewContext();
        var cso = (await new PostgresDataRepository(ctx).Sync.GetPendingExportsByIdsAsync([exportId])).Single().ConnectedSystemObject!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.AttributeValues.Count(v => v.AttributeId == s.MemberAttributeId), Is.EqualTo(3));
            Assert.That(cso.PartiallyLoadedAttributes, Is.Null);
        }
    }

    [Test]
    public async Task GetPendingExportsByIdsAsync_LargeGroupWithAWholeSetChange_LoadsItWholeAndSaysWhyAsync()
    {
        var s = await SeedSchemaAsync();
        var group = await SeedGroupAsync(s, memberCount: 20);
        var exportId = await SeedExportAsync(s, group.Id, T0, new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = s.MemberAttributeId, ChangeType = PendingExportAttributeChangeType.RemoveAll,
            Status = PendingExportAttributeChangeStatus.Pending
        });

        await using var ctx = NewContext();
        var cso = (await new PostgresDataRepository(ctx).Sync.GetPendingExportsByIdsAsync([exportId])).Single().ConnectedSystemObject!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.AttributeValues.Count(v => v.AttributeId == s.MemberAttributeId), Is.EqualTo(20));
            Assert.That(cso.IsPartiallyLoaded, Is.False);
            Assert.That(cso.LoadedWholeAttributes![s.MemberAttributeId], Is.EqualTo(ExportValueLoadFallbackReason.WholeSetChange));
        }
    }

    // ─── Never saved back ───

    [Test]
    public async Task SaveMethodsWritingTheValueSet_ObjectLoadedInPart_ThrowAndWriteNothingAsync()
    {
        var s = await SeedSchemaAsync();
        var group = await SeedGroupAsync(s, memberCount: 10);
        var exportId = await SeedExportAsync(s, group.Id, T0, Remove(s, group.Members[0]));

        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        var repository = new PostgresDataRepository(ctx);
        var cso = (await repository.Sync.GetPendingExportsByIdsAsync([exportId])).Single().ConnectedSystemObject!;
        Assert.That(cso.IsPartiallyLoaded, Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => repository.ConnectedSystems.UpdateConnectedSystemObjectAsync(cso), Throws.TypeOf<PartiallyLoadedObjectException>());
            Assert.That(() => repository.ConnectedSystems.UpdateConnectedSystemObjectWithNewAttributeValuesAsync(cso, []), Throws.TypeOf<PartiallyLoadedObjectException>());
            Assert.That(() => repository.ConnectedSystems.CreateConnectedSystemObjectAsync(cso), Throws.TypeOf<PartiallyLoadedObjectException>());
            Assert.That(() => repository.ConnectedSystems.CreateConnectedSystemObjectsAsync([cso]), Throws.TypeOf<PartiallyLoadedObjectException>());
            Assert.That(() => repository.Sync.UpdateConnectedSystemObjectAsync(cso), Throws.TypeOf<PartiallyLoadedObjectException>());
            Assert.That(() => repository.Sync.CreateConnectedSystemObjectsAsync([cso]), Throws.TypeOf<PartiallyLoadedObjectException>());
        }

        Assert.That(await CountValuesAsync(group.Id, s.MemberAttributeId), Is.EqualTo(10), "nothing was written");
    }

    // ─── Execution end to end ───

    /// <summary>
    /// A large group losing a few members is exported through the lean load: the removed members are deleted, every
    /// member the load did not load survives the batch untouched, and the load's span says the attribute was loaded in
    /// part, with the number of values loaded.
    /// </summary>
    [Test]
    public async Task ExecuteExportsAsync_LargeGroupLosingMembers_RemovesThemAndLeavesEveryUnloadedMemberUntouchedAsync()
    {
        var s = await SeedSchemaAsync();
        var group = await SeedGroupAsync(s, memberCount: 200);
        var removed = group.Members.Take(5).ToList();
        await SeedExportAsync(s, group.Id, T0, removed.Select(m => Remove(s, m)).ToArray());
        var before = await ReadValuesAsync(group.Id);

        var (result, spans) = await ExecuteAsync(s.SystemId);

        var after = await ReadValuesAsync(group.Id);
        var removedIds = removed.Select(m => m.ValueId).ToHashSet();
        var load = spans.Single(span => span.DisplayName == "ReloadExportBatch");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SuccessCount, Is.EqualTo(1));
            Assert.That(result.OptimisticApplyAppliedCount, Is.EqualTo(1));
            Assert.That(after, Is.EquivalentTo(before.Where(v => !removedIds.Contains(v.Id))),
                "every value the load did not load is held exactly as it was, and only the removed members are gone");
            Assert.That(load.GetTagItem("trimmedAttributeCount"), Is.EqualTo(1));
            Assert.That(load.GetTagItem("objectAttributeValueCount"), Is.EqualTo(6), "the DN and the five named members");
            Assert.That(load.GetTagItem("fallbackAttributeCount"), Is.EqualTo(0));
        }
    }

    /// <summary>
    /// The same mixed batch through the whole load and the lean load gives identical outcomes: the calculator's delta,
    /// the LDAP Connector's modifications (placeholder member included) and the values applied. The batch holds large
    /// groups gaining and losing members (one re-adding a member it holds), a large group whose text attribute is
    /// replaced, a group emptied, a group holding only the placeholder gaining its first member, an empty group gaining
    /// its first member, and large groups loaded whole because a change cannot be put as a question.
    /// </summary>
    [Test]
    public async Task LeanAndWholeLoads_MixedBatch_GiveIdenticalOutcomesAsync()
    {
        var s = await SeedSchemaAsync();
        var newUsers = await SeedUsersAsync(s, 6);
        var at = T0;
        DateTime Next() => at = at.AddSeconds(1);

        var big = await SeedGroupAsync(s, 40);
        await SeedExportAsync(s, big.Id, Next(), [
            .. big.Members.Take(5).Select(m => Remove(s, m)),
            .. newUsers.Take(3).Select(u => Add(s, u)),
            Add(s, big.Members[10])]);

        var bigWithMail = await SeedGroupAsync(s, 40, mail: "staff@glitterband.local");
        await SeedExportAsync(s, bigWithMail.Id, Next(), [
            .. bigWithMail.Members.Take(2).Select(m => Remove(s, m)),
            new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(), AttributeId = s.MailAttributeId, ChangeType = PendingExportAttributeChangeType.Update,
                Status = PendingExportAttributeChangeStatus.Pending, StringValue = "everyone@glitterband.local"
            }]);

        var emptied = await SeedGroupAsync(s, 3);
        await SeedExportAsync(s, emptied.Id, Next(), [.. emptied.Members.Select(m => Remove(s, m))]);

        var placeholderOnly = await SeedGroupAsync(s, 0, placeholderMember: true);
        await SeedExportAsync(s, placeholderOnly.Id, Next(), Add(s, newUsers[3]));

        var empty = await SeedGroupAsync(s, 0);
        await SeedExportAsync(s, empty.Id, Next(), Add(s, newUsers[4]));

        var clearedWhole = await SeedGroupAsync(s, 30);
        await SeedExportAsync(s, clearedWhole.Id, Next(), new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = s.MemberAttributeId, ChangeType = PendingExportAttributeChangeType.RemoveAll,
            Status = PendingExportAttributeChangeStatus.Pending
        });

        var namesNoObject = await SeedGroupAsync(s, 30);
        await SeedExportAsync(s, namesNoObject.Id, Next(), new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = s.MemberAttributeId, ChangeType = PendingExportAttributeChangeType.Remove,
            Status = PendingExportAttributeChangeStatus.Pending, StringValue = namesNoObject.Members[0].Dn.ToUpperInvariant()
        });

        var groups = new[] { big, bigWithMail, emptied, placeholderOnly, empty, clearedWhole, namesNoObject };
        var exportIds = await ReadExportIdsAsync();

        List<PendingExport> whole, lean;
        await using (var wholeContext = NewContext())
            whole = await LoadWholeAsTheLoadDidBefore2816Async(wholeContext, exportIds);
        await using (var leanContext = NewContext())
            lean = await new PostgresDataRepository(leanContext).Sync.GetPendingExportsByIdsAsync(exportIds);

        Assert.That(lean.Count(pe => pe.ConnectedSystemObject!.IsPartiallyLoaded), Is.EqualTo(2),
            "the two large groups whose changes can be asked about are loaded in part; the rest whole");

        // The LDAP Connector's modifications, placeholder member included.
        var dnByObject = groups.ToDictionary(g => g.Id, g => g.Dn);
        foreach (var (wholeExport, leanExport) in whole.Zip(lean))
        {
            Assert.That(Modifications(leanExport, dnByObject[leanExport.ConnectedSystemObjectId!.Value]),
                Is.EqualTo(Modifications(wholeExport, dnByObject[wholeExport.ConnectedSystemObjectId!.Value])),
                $"the LDAP modifications for {DescribeGroup(groups, wholeExport.ConnectedSystemObjectId!.Value)}");
        }

        // The calculator's delta.
        var wholeDelta = OptimisticExportApplyCalculator.CalculateDelta(whole);
        var leanDelta = OptimisticExportApplyCalculator.CalculateDelta(lean);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(leanDelta.RemovalValueIds, Is.EquivalentTo(wholeDelta.RemovalValueIds));
            Assert.That(leanDelta.Additions.Select(a => (a.ConnectedSystemObject.Id, Shape(a) with { Id = Guid.Empty })),
                Is.EquivalentTo(wholeDelta.Additions.Select(a => (a.ConnectedSystemObject.Id, Shape(a) with { Id = Guid.Empty }))), "additions get new ids each time");
            Assert.That(leanDelta.SkippedChangeCount, Is.EqualTo(wholeDelta.SkippedChangeCount));
            Assert.That(leanDelta.UnresolvedReferenceCount, Is.EqualTo(wholeDelta.UnresolvedReferenceCount));
            Assert.That(leanDelta.UnrecordedChanges, Is.EqualTo(wholeDelta.UnrecordedChanges));
        }

        // The values applied: what execution through the lean load leaves in the database is what the whole load's delta
        // describes.
        var before = new Dictionary<Guid, List<ValueShape>>();
        foreach (var group in groups)
            before[group.Id] = await ReadValuesAsync(group.Id);
        var removals = wholeDelta.RemovalValueIds.ToHashSet();
        var expected = groups.ToDictionary(g => g.Id, g => before[g.Id].Where(v => !removals.Contains(v.Id)).Select(v => v with { Id = Guid.Empty })
            .Concat(wholeDelta.Additions.Where(a => a.ConnectedSystemObject.Id == g.Id).Select(a => Shape(a) with { Id = Guid.Empty }))
            .ToList());

        var (result, spans) = await ExecuteAsync(s.SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SuccessCount, Is.EqualTo(groups.Length));
            Assert.That(result.OptimisticApplyFailedCount, Is.Zero);
            foreach (var group in groups)
            {
                Assert.That((await ReadValuesAsync(group.Id)).Select(v => v with { Id = Guid.Empty }), Is.EquivalentTo(expected[group.Id]),
                    $"the values applied to {DescribeGroup(groups, group.Id)}");
            }

            var load = spans.Single(span => span.DisplayName == "ReloadExportBatch");
            Assert.That(load.GetTagItem("trimmedAttributeCount"), Is.EqualTo(2));
            Assert.That(load.GetTagItem("fallbackAttributeCount"), Is.EqualTo(2));
            Assert.That(load.GetTagItem("fallbackReasons"), Is.EqualTo("NamesNoObject=1,WholeSetChange=1"));
        }
    }

    // ─── Helpers ───

    private sealed record Schema(int SystemId, int GroupTypeId, int UserTypeId, int DnAttributeId, int MemberAttributeId, int MailAttributeId,
        int UserDnAttributeId);

    private sealed record SeededMember(Guid ValueId, Guid UserId, string Dn);

    private sealed record SeededUser(Guid Id, string Dn);

    private sealed record SeededGroup(Guid Id, string Dn, List<SeededMember> Members);

    private sealed record ValueShape(Guid Id, int AttributeId, Guid? ReferenceValueId, string? UnresolvedReferenceValue, string? StringValue);

    private static ValueShape Shape(ConnectedSystemObjectAttributeValue value) =>
        new(value.Id, value.AttributeId, value.ReferenceValueId, value.UnresolvedReferenceValue, value.StringValue);

    private static string DescribeGroup(IEnumerable<SeededGroup> groups, Guid id) =>
        $"group {groups.ToList().FindIndex(g => g.Id == id)}";

    private static PendingExportAttributeValueChange Remove(Schema s, SeededMember member) => new()
    {
        Id = Guid.NewGuid(), AttributeId = s.MemberAttributeId, ChangeType = PendingExportAttributeChangeType.Remove,
        Status = PendingExportAttributeChangeStatus.Pending, StringValue = member.Dn, ResolvedReferenceCsoId = member.UserId
    };

    private static PendingExportAttributeValueChange Add(Schema s, SeededMember member) => new()
    {
        Id = Guid.NewGuid(), AttributeId = s.MemberAttributeId, ChangeType = PendingExportAttributeChangeType.Add,
        Status = PendingExportAttributeChangeStatus.Pending, StringValue = member.Dn, ResolvedReferenceCsoId = member.UserId
    };

    private static PendingExportAttributeValueChange Add(Schema s, SeededUser user) => new()
    {
        Id = Guid.NewGuid(), AttributeId = s.MemberAttributeId, ChangeType = PendingExportAttributeChangeType.Add,
        Status = PendingExportAttributeChangeStatus.Pending, StringValue = user.Dn, ResolvedReferenceCsoId = user.Id
    };

    private async Task<Schema> SeedSchemaAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        var groupType = new ConnectedSystemObjectType { Name = "groupOfNames", ConnectedSystem = system, Selected = true };
        var userType = new ConnectedSystemObjectType { Name = "inetOrgPerson", ConnectedSystem = system, Selected = true };
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
        var userDn = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName", ConnectedSystemObjectType = userType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsSecondaryExternalId = true
        };
        groupType.Attributes.Add(dn);
        groupType.Attributes.Add(member);
        groupType.Attributes.Add(mail);
        userType.Attributes.Add(userDn);
        seed.AddRange(connectorDefinition, system, groupType, userType);
        await seed.SaveChangesAsync();
        return new Schema(system.Id, groupType.Id, userType.Id, dn.Id, member.Id, mail.Id, userDn.Id);
    }

    private async Task<List<SeededUser>> SeedUsersAsync(Schema s, int count)
    {
        await using var seed = NewContext();
        var users = Enumerable.Range(0, count).Select(_ =>
        {
            var id = Guid.NewGuid();
            return new SeededUser(id, $"cn=user{id:N},ou=people,dc=glitterband,dc=local");
        }).ToList();
        seed.ConnectedSystemObjects.AddRange(users.Select(u => new ConnectedSystemObject
        {
            Id = u.Id, ConnectedSystemId = s.SystemId, TypeId = s.UserTypeId, ExternalIdAttributeId = s.UserDnAttributeId,
            Status = ConnectedSystemObjectStatus.Normal, JoinType = ConnectedSystemObjectJoinType.NotJoined
        }));
        await seed.SaveChangesAsync();
        return users;
    }

    /// <summary>
    /// A groupOfNames whose members are user objects, each held as a resolved reference keeping the user's DN (as an
    /// import records it); optionally holding only the placeholder member (unresolved: it names no object), and a mail
    /// value.
    /// </summary>
    private async Task<SeededGroup> SeedGroupAsync(Schema s, int memberCount, bool placeholderMember = false, string? mail = null)
    {
        var users = await SeedUsersAsync(s, memberCount);
        var dn = $"cn=Group {Guid.NewGuid():N},ou=groups,dc=glitterband,dc=local";
        await using var seed = NewContext();
        var group = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = s.SystemId, TypeId = s.GroupTypeId, ExternalIdAttributeId = s.DnAttributeId,
            SecondaryExternalIdAttributeId = s.DnAttributeId, Status = ConnectedSystemObjectStatus.Normal, JoinType = ConnectedSystemObjectJoinType.NotJoined
        };
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.DnAttributeId, StringValue = dn });
        if (mail != null)
            group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.MailAttributeId, StringValue = mail });
        if (placeholderMember)
            group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.MemberAttributeId, UnresolvedReferenceValue = PlaceholderDn });

        var members = users.Select(u => new SeededMember(Guid.NewGuid(), u.Id, u.Dn)).ToList();
        foreach (var member in members)
        {
            group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = member.ValueId, AttributeId = s.MemberAttributeId, ReferenceValueId = member.UserId, UnresolvedReferenceValue = member.Dn
            });
        }
        seed.Add(group);
        await seed.SaveChangesAsync();
        return new SeededGroup(group.Id, dn, members);
    }

    private async Task<Guid> SeedExportAsync(Schema s, Guid groupId, DateTime createdAt, params PendingExportAttributeValueChange[] changes)
    {
        await using var seed = NewContext();
        var export = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = s.SystemId, ConnectedSystemObjectId = groupId, ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending, CreatedAt = createdAt
        };
        export.AttributeValueChanges.AddRange(changes);
        seed.Add(export);
        await seed.SaveChangesAsync();
        return export.Id;
    }

    private async Task<List<Guid>> ReadExportIdsAsync()
    {
        await using var ctx = NewContext();
        return await ctx.PendingExports.OrderBy(pe => pe.CreatedAt).ThenBy(pe => pe.Id).Select(pe => pe.Id).ToListAsync();
    }

    private async Task<List<ValueShape>> ReadValuesAsync(Guid objectId)
    {
        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        return await ctx.ConnectedSystemObjectAttributeValues
            .Where(v => EF.Property<Guid>(v, "ConnectedSystemObjectId") == objectId)
            .Select(v => new ValueShape(v.Id, v.AttributeId, v.ReferenceValueId, v.UnresolvedReferenceValue, v.StringValue))
            .ToListAsync();
    }

    private async Task<int> CountValuesAsync(Guid objectId, int attributeId)
    {
        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        return await ctx.ConnectedSystemObjectAttributeValues
            .CountAsync(v => EF.Property<Guid>(v, "ConnectedSystemObjectId") == objectId && v.AttributeId == attributeId);
    }

    /// <summary>
    /// The batch as the load read it before #2816: every value of every object, with its attribute. The reference the
    /// lean load is held to.
    /// </summary>
    private static Task<List<PendingExport>> LoadWholeAsTheLoadDidBefore2816Async(JimDbContext context, IReadOnlyCollection<Guid> ids) =>
        context.PendingExports
            .AsNoTrackingWithIdentityResolution()
            .AsSplitQuery()
            .Include(pe => pe.AttributeValueChanges).ThenInclude(avc => avc.Attribute)
            .Include(pe => pe.ConnectedSystemObject).ThenInclude(cso => cso!.Type)
            .Include(pe => pe.ConnectedSystemObject).ThenInclude(cso => cso!.AttributeValues).ThenInclude(av => av.Attribute)
            .Where(pe => ids.Contains(pe.Id))
            .OrderBy(pe => pe.CreatedAt).ThenBy(pe => pe.Id)
            .ToListAsync();

    private async Task<(ExportExecutionResult Result, List<System.Diagnostics.Activity> Spans)> ExecuteAsync(int systemId)
    {
        var connector = new Mock<IConnector>();
        connector.Setup(c => c.Name).Returns("Recording Connector");
        connector.As<IConnectorExportUsingCalls>()
            .Setup(c => c.ExportAsync(It.IsAny<IList<PendingExport>>(), It.IsAny<CancellationToken>(), It.IsAny<IConnectorProgress>()))
            .ReturnsAsync((IList<PendingExport> exports, CancellationToken _, IConnectorProgress _) =>
                exports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList());

        await using var callerContext = NewContext();
        var callerRepository = new PostgresDataRepository(callerContext);
        using var jim = new JimApplication(callerRepository, syncRepository: callerRepository.Sync);
        var connectedSystem = (await jim.ConnectedSystems.GetConnectedSystemAsync(systemId))!;

        ExportExecutionResult result;
        ActivityTraceId traceId;
        using (var root = TestSource.StartActivity("Test"))
        {
            traceId = root!.TraceId;
            result = await jim.ExportExecution.ExecuteExportsAsync(connectedSystem, connector.Object, SyncRunMode.PreviewAndSync,
                new ExportExecutionOptions { BatchSize = 100 }, CancellationToken.None);
        }

        return (result, _stopped.Where(span => span.TraceId == traceId).ToList());
    }

    /// <summary>
    /// The member modifications the LDAP Connector would send an OpenLDAP directory for the export, in order, each with
    /// its operation and values.
    /// </summary>
    private static List<string> Modifications(PendingExport export, string groupDn)
    {
        var settings = new List<ConnectedSystemSettingValue>
        {
            new() { Setting = new ConnectorDefinitionSetting { Name = "Delete Behaviour" }, StringValue = "Delete" }
        };
        var connector = new LdapConnectorExport(new Mock<ILdapOperationExecutor>().Object, settings, Log.Logger, 1,
            LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE, LdapDirectoryType.OpenLDAP, PlaceholderDn);

        return connector.BuildModifyRequests(ExportExecutionServer.ForConnector(export), groupDn)
            .SelectMany(request => Enumerable.Range(0, request.Modifications.Count).Select(i => request.Modifications[i]))
            .Select(modification => $"{modification.Operation} {modification.Name}: " +
                string.Join(";", Enumerable.Range(0, modification.Count).Select(i => modification[i]?.ToString() ?? string.Empty)))
            .ToList();
    }
}
