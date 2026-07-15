// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that Metaverse Object deletion removes valueless "ghost" reference
/// rows from surviving referencing objects instead of nulling their FK (#1019), on both the plural
/// and singular deletion forms, without breaking the worker's long-lived tracking context.
/// </summary>
/// <remarks>
/// The raw SQL DELETE bypasses EF change tracking, so tracked instances of the deleted rows must be
/// surgically removed from their parents' AttributeValues collections and detached BEFORE
/// RemoveRange runs: EF's ClientSetNull cascade fix-up otherwise marks them Modified (UPDATE against
/// a deleted row, zero rows affected, DbUpdateConcurrencyException), and a detached-but-reachable
/// instance is re-attached as Added by the next DetectChanges and silently re-INSERTed. The
/// in-memory provider can catch neither failure mode. Opt-in via the JIM_TEST_RESET_* environment
/// variables; ignored when JIM_TEST_RESET_DB is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class MvoDeletionGhostReferenceRowDatabaseTests
{
    private string _connectionString = null!;

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL ghost reference row tests.");

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
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(@"
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory') LOOP
                    EXECUTE 'TRUNCATE TABLE ""' || r.tablename || '"" RESTART IDENTITY CASCADE';
                END LOOP;
            END $$;");
    }

    /// <summary>
    /// Seeds a Member reference attribute, a group-shaped MVO referencing a member MVO and a
    /// survivor MVO (one pure reference row each), plus a payload-carrying row and an asserted-null
    /// marker row on the group.
    /// </summary>
    private async Task<(Guid MemberId, Guid SurvivorId, Guid GroupId, int MemberAttrId)> SeedGroupWithReferenceRowsAsync()
    {
        await using var seed = NewContext();

        var memberAttr = new MetaverseAttribute
        {
            Name = "Test Static Members",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued,
            BuiltIn = false
        };
        var mvUserType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        var mvGroupType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
        seed.AddRange(memberAttr, mvUserType, mvGroupType);
        await seed.SaveChangesAsync();

        var member = new MetaverseObject { Type = mvUserType };
        var survivor = new MetaverseObject { Type = mvUserType };
        var group = new MetaverseObject { Type = mvGroupType };
        group.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = memberAttr.Id, Attribute = memberAttr, ReferenceValue = member
        });
        group.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = memberAttr.Id, Attribute = memberAttr, ReferenceValue = survivor
        });
        group.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = memberAttr.Id, Attribute = memberAttr, ReferenceValue = member,
            StringValue = "payload"
        });
        group.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = memberAttr.Id, Attribute = memberAttr, NullValue = true
        });
        seed.AddRange(member, survivor, group);
        await seed.SaveChangesAsync();

        return (member.Id, survivor.Id, group.Id, memberAttr.Id);
    }

    private static async Task<List<MetaverseObjectAttributeValue>> LoadGroupRowsAsync(JimDbContext ctx, Guid groupId)
    {
        return await ctx.MetaverseObjects
            .Where(m => m.Id == groupId)
            .SelectMany(m => m.AttributeValues)
            .AsNoTracking()
            .ToListAsync();
    }

    /// <summary>
    /// The core #1019 behaviour: deleting a referenced MVO removes the pure reference row from the
    /// surviving group, nulls the payload-carrying row's FK (legacy behaviour preserved), and leaves
    /// the survivor's row and the asserted-null marker untouched.
    /// </summary>
    [Test]
    public async Task DeleteMetaverseObjectsAsync_UntrackedSurvivingGroup_GhostRowDeletedNotNulledAsync()
    {
        var (memberId, survivorId, groupId, _) = await SeedGroupWithReferenceRowsAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var member = await ctx.MetaverseObjects.SingleAsync(m => m.Id == memberId);

        await repository.Sync.DeleteMetaverseObjectsAsync([member]);

        await using var verify = NewContext();
        var rows = await LoadGroupRowsAsync(verify, groupId);
        Assert.That(rows, Has.Count.EqualTo(3),
            "The pure reference row pointing at the deleted member must be deleted, not left as an all-null ghost");
        Assert.That(rows.Count(r => r.ReferenceValueId == survivorId), Is.EqualTo(1),
            "The row referencing the surviving object must be untouched");
        Assert.That(rows.Count(r => r.StringValue == "payload" && r.ReferenceValueId == null), Is.EqualTo(1),
            "The payload-carrying row must survive with its reference nulled");
        Assert.That(rows.Count(r => r.NullValue), Is.EqualTo(1),
            "The asserted-null marker row must survive");
        Assert.That(rows.Count(r => r.ReferenceValueId == null && !r.NullValue && r.StringValue == null), Is.EqualTo(0),
            "No informationless ghost row may remain");
    }

    /// <summary>
    /// The worker's long-lived context tracks the surviving group and its attribute values when the
    /// deletion flush runs. Without tracker surgery, EF's ClientSetNull cascade fix-up marks the
    /// raw-deleted row Modified and the delete throws DbUpdateConcurrencyException; without removal
    /// from the parent collection, the next SaveChangesAsync re-attaches the detached row as Added
    /// and silently re-inserts it (resurrection).
    /// </summary>
    [Test]
    public async Task DeleteMetaverseObjectsAsync_TrackedSurvivingGroup_NoConcurrencyExceptionAndNoResurrectionAsync()
    {
        var (memberId, _, groupId, _) = await SeedGroupWithReferenceRowsAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var member = await ctx.MetaverseObjects.SingleAsync(m => m.Id == memberId);
        var trackedGroup = await ctx.MetaverseObjects
            .Include(m => m.AttributeValues)
            .SingleAsync(m => m.Id == groupId);

        Assert.DoesNotThrowAsync(() => repository.Sync.DeleteMetaverseObjectsAsync([member]),
            "Tracked ghost rows must be surgically detached before the MVO delete saves");

        Assert.That(trackedGroup.AttributeValues.Count(av => av.ReferenceValueId == null && !av.NullValue && av.StringValue == null),
            Is.EqualTo(0), "The tracked group's collection must no longer contain the deleted ghost row");

        // A later save on the same context (any subsequent page work) must not resurrect the row.
        Assert.DoesNotThrowAsync(() => ctx.SaveChangesAsync());

        await using var verify = NewContext();
        var rows = await LoadGroupRowsAsync(verify, groupId);
        Assert.That(rows, Has.Count.EqualTo(3),
            "The deleted ghost row must not be re-inserted by a later SaveChangesAsync on the same context");
    }

    /// <summary>
    /// Two cross-referencing MVOs deleted in the same batch, both tracked with attribute values: the
    /// raw DELETE must exclude rows owned by co-deleted MVOs, otherwise EF's cascade marks the
    /// already-deleted row Deleted and the save throws DbUpdateConcurrencyException.
    /// </summary>
    [Test]
    public async Task DeleteMetaverseObjectsAsync_CoDeletedCrossReferencingMvos_TrackedAttributeValues_SucceedsAsync()
    {
        int managerAttrId;
        Guid leaverAId, leaverBId;
        await using (var seed = NewContext())
        {
            var managerAttr = new MetaverseAttribute
            {
                Name = "Test Manager",
                Type = AttributeDataType.Reference,
                AttributePlurality = AttributePlurality.SingleValued,
                BuiltIn = false
            };
            var mvUserType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
            seed.AddRange(managerAttr, mvUserType);
            await seed.SaveChangesAsync();

            var leaverB = new MetaverseObject { Type = mvUserType };
            var leaverA = new MetaverseObject { Type = mvUserType };
            leaverA.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(), AttributeId = managerAttr.Id, Attribute = managerAttr, ReferenceValue = leaverB
            });
            seed.AddRange(leaverA, leaverB);
            await seed.SaveChangesAsync();
            managerAttrId = managerAttr.Id;
            leaverAId = leaverA.Id;
            leaverBId = leaverB.Id;
        }

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var leavers = await ctx.MetaverseObjects
            .Include(m => m.AttributeValues)
            .Where(m => m.Id == leaverAId || m.Id == leaverBId)
            .ToListAsync();

        Assert.DoesNotThrowAsync(() => repository.Sync.DeleteMetaverseObjectsAsync(leavers),
            "Rows owned by co-deleted MVOs must be left to the database cascade, not raw-deleted from under the tracker");

        await using var verify = NewContext();
        Assert.That(await verify.MetaverseObjects.AnyAsync(m => m.Id == leaverAId || m.Id == leaverBId), Is.False,
            "Both MVOs must be deleted");
        Assert.That(await verify.Set<MetaverseObjectAttributeValue>().AnyAsync(av => av.AttributeId == managerAttrId), Is.False,
            "The cross-reference row must be gone with its owner");
    }

    /// <summary>
    /// The singular deletion form runs on the same long-lived context as the per-MVO fallback of the
    /// deletion flush; it must remove ghost rows and perform identical tracker surgery.
    /// </summary>
    [Test]
    public async Task DeleteMetaverseObjectAsync_Singular_TrackedSurvivingReferencer_GhostRowDeletedAsync()
    {
        var (memberId, survivorId, groupId, _) = await SeedGroupWithReferenceRowsAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var member = await ctx.MetaverseObjects.SingleAsync(m => m.Id == memberId);
        var trackedGroup = await ctx.MetaverseObjects
            .Include(m => m.AttributeValues)
            .SingleAsync(m => m.Id == groupId);

        Assert.DoesNotThrowAsync(() => repository.Sync.DeleteMetaverseObjectAsync(member));

        Assert.That(trackedGroup.AttributeValues.Count(av => av.ReferenceValueId == null && !av.NullValue && av.StringValue == null),
            Is.EqualTo(0), "The singular form must surgically remove the tracked ghost row too");

        await using var verify = NewContext();
        var rows = await LoadGroupRowsAsync(verify, groupId);
        Assert.That(rows, Has.Count.EqualTo(3), "The ghost row must be deleted by the singular form");
        Assert.That(rows.Count(r => r.ReferenceValueId == survivorId), Is.EqualTo(1));
    }

    /// <summary>
    /// Parity pin: the SQL predicate deciding which rows are deleted must agree with
    /// <see cref="MetaverseObjectAttributeValue.IsValuelessReferenceRow"/> across a matrix of one
    /// row per payload column plus a pure row and an asserted-null marker.
    /// </summary>
    [Test]
    public async Task DeleteMetaverseObjectsAsync_PayloadMatrix_SqlPredicateMatchesModelHelperAsync()
    {
        Guid memberId, groupId;
        List<MetaverseObjectAttributeValue> seededRows;
        await using (var seed = NewContext())
        {
            var memberAttr = new MetaverseAttribute
            {
                Name = "Test Static Members",
                Type = AttributeDataType.Reference,
                AttributePlurality = AttributePlurality.MultiValued,
                BuiltIn = false
            };
            var mvUserType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
            var mvGroupType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Test System", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
            var cso = new ConnectedSystemObject
            {
                Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal
            };
            seed.AddRange(memberAttr, mvUserType, mvGroupType, connectorDefinition, system, csType, cso);
            await seed.SaveChangesAsync();

            var member = new MetaverseObject { Type = mvUserType };
            var group = new MetaverseObject { Type = mvGroupType };
            seededRows =
            [
                new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = memberAttr.Id, Attribute = memberAttr, ReferenceValue = member },
                new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = memberAttr.Id, Attribute = memberAttr, ReferenceValue = member, StringValue = "s" },
                new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = memberAttr.Id, Attribute = memberAttr, ReferenceValue = member, DateTimeValue = DateTime.UtcNow },
                new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = memberAttr.Id, Attribute = memberAttr, ReferenceValue = member, IntValue = 1 },
                new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = memberAttr.Id, Attribute = memberAttr, ReferenceValue = member, LongValue = 1L },
                new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = memberAttr.Id, Attribute = memberAttr, ReferenceValue = member, ByteValue = [0x01] },
                new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = memberAttr.Id, Attribute = memberAttr, ReferenceValue = member, GuidValue = Guid.NewGuid() },
                new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = memberAttr.Id, Attribute = memberAttr, ReferenceValue = member, BoolValue = false },
                new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = memberAttr.Id, Attribute = memberAttr, ReferenceValue = member, UnresolvedReferenceValue = cso },
                new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = memberAttr.Id, Attribute = memberAttr, NullValue = true }
            ];
            foreach (var row in seededRows)
                group.AttributeValues.Add(row);
            seed.AddRange(member, group);
            await seed.SaveChangesAsync();
            memberId = member.Id;
            groupId = group.Id;
        }

        // The model helper's verdict per seeded row, taken before deletion. Every valueless row
        // here references the deleted member (the marker row is not valueless by definition), so
        // survival must equal "not valueless".
        var expectedSurvivorIds = seededRows
            .Where(r => !r.IsValuelessReferenceRow())
            .Select(r => r.Id)
            .OrderBy(id => id)
            .ToList();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var member2 = await ctx.MetaverseObjects.SingleAsync(m => m.Id == memberId);
        await repository.Sync.DeleteMetaverseObjectsAsync([member2]);

        await using var verify = NewContext();
        var survivorIds = (await LoadGroupRowsAsync(verify, groupId)).Select(r => r.Id).OrderBy(id => id).ToList();
        Assert.That(survivorIds, Is.EqualTo(expectedSurvivorIds),
            "The SQL delete predicate must agree with MetaverseObjectAttributeValue.IsValuelessReferenceRow");
    }
}
