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
/// Real-PostgreSQL verification of <see cref="JIM.PostgresData.Repositories.SyncRepository.GetConnectedSystemObjectsForMvoDeletionAsync"/>,
/// the lean bulk CSO fetch used by the set-based MVO deletion flush and reference recall capture
/// (issue #993).
/// </summary>
/// <remarks>
/// This fetch must load only each CSO's external ID and secondary external ID attribute values,
/// matched EITHER by the CSO's <c>ExternalIdAttributeId</c>/<c>SecondaryExternalIdAttributeId</c>
/// columns OR by the schema attributes' <c>IsExternalId</c>/<c>IsSecondaryExternalId</c> flags,
/// without materialising the rest of the attribute graph (a deleted group's full membership).
/// A filtered Include cannot express the column match (EF Core cannot translate a filtered
/// Include that references the parent entity; it throws InvalidOperationException at query
/// translation time), and EF Core's in-memory provider masks both the translation failure and
/// the include shape, so only a real database run can verify this. Opt-in via the same
/// <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c> fixtures;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class MvoDeletionCsoFetchDatabaseTests
{
    private const int MembershipValueCount = 300;

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL MVO deletion CSO fetch tests.");

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
    /// Seeds two group-shaped MVO/CSO pairs. Each CSO carries an external ID value (attribute
    /// flagged <c>IsExternalId</c>), a DN value referenced only by the CSO's
    /// <c>SecondaryExternalIdAttributeId</c> column (the attribute is deliberately NOT flagged
    /// <c>IsSecondaryExternalId</c>, pinning the column-based match), and a large membership
    /// value set that the lean fetch must not load.
    /// </summary>
    private async Task<(Guid MvoIdA, Guid MvoIdB, string DnA, string DnB)> SeedTwoGroupsAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "GROUP", ConnectedSystem = system, Selected = true };
        var externalIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "entryUUID", ConnectedSystemObjectType = csType, Type = AttributeDataType.Guid,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var dnAttr = new ConnectedSystemObjectTypeAttribute
        {
            // Deliberately not flagged IsSecondaryExternalId: the CSO's SecondaryExternalIdAttributeId
            // column points at it, and the fetch must honour the column even if the flag diverges.
            Name = "dn", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        csType.Attributes.Add(externalIdAttr);
        csType.Attributes.Add(dnAttr);
        csType.Attributes.Add(memberAttr);

        var mvType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };

        seed.AddRange(connectorDefinition, system, csType, mvType);
        await seed.SaveChangesAsync();

        var mvoIds = new List<Guid>();
        var dns = new List<string>();
        for (var g = 0; g < 2; g++)
        {
            var mvo = new MetaverseObject { Type = mvType };
            var dn = $"CN=Group{g},OU=Groups,DC=test";
            var cso = new ConnectedSystemObject
            {
                Type = csType,
                ConnectedSystem = system,
                Status = ConnectedSystemObjectStatus.Normal,
                MetaverseObject = mvo,
                JoinType = ConnectedSystemObjectJoinType.Provisioned,
                DateJoined = DateTime.UtcNow,
                ExternalIdAttributeId = externalIdAttr.Id,
                SecondaryExternalIdAttributeId = dnAttr.Id
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Attribute = externalIdAttr, ConnectedSystemObject = cso, GuidValue = Guid.NewGuid()
            });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Attribute = dnAttr, ConnectedSystemObject = cso, StringValue = dn
            });
            for (var i = 0; i < MembershipValueCount; i++)
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                {
                    Attribute = memberAttr, ConnectedSystemObject = cso, StringValue = $"CN=User{i:D4},DC=test"
                });

            seed.Add(mvo);
            seed.Add(cso);
            await seed.SaveChangesAsync();
            mvoIds.Add(mvo.Id);
            dns.Add(dn);
        }

        return (mvoIds[0], mvoIds[1], dns[0], dns[1]);
    }

    /// <summary>
    /// The lean bulk fetch must execute against real PostgreSQL (a filtered Include referencing
    /// the parent CSO does not translate), return every CSO grouped by MVO ID, load the external
    /// ID and column-referenced secondary external ID values with their Attribute, and NOT load
    /// the group's membership values.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsForMvoDeletionAsync_LoadsOnlyExternalIdValuesGroupedByMvoAsync()
    {
        var (mvoIdA, mvoIdB, dnA, dnB) = await SeedTwoGroupsAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.Sync.GetConnectedSystemObjectsForMvoDeletionAsync([mvoIdA, mvoIdB]);

        Assert.That(result.Keys, Is.EquivalentTo(new[] { mvoIdA, mvoIdB }),
            "Every MVO with joined CSOs must be present in the result.");

        foreach (var (mvoId, expectedDn) in new[] { (mvoIdA, dnA), (mvoIdB, dnB) })
        {
            var csos = result[mvoId];
            Assert.That(csos, Has.Count.EqualTo(1), "Each seeded MVO has exactly one joined CSO.");
            var cso = csos[0];

            Assert.That(cso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Provisioned));
            Assert.That(cso.AttributeValues.Any(av => av.StringValue != null && av.StringValue.StartsWith("CN=User")), Is.False,
                "The lean fetch must not materialise the group's membership values.");
            Assert.That(cso.AttributeValues.Count, Is.EqualTo(2),
                "Only the external ID and secondary external ID values may be loaded.");
            Assert.That(cso.SecondaryExternalIdAttributeValue?.StringValue, Is.EqualTo(expectedDn),
                "The column-referenced secondary external ID value (the DN a delete Pending Export needs) must be loaded even when the schema attribute is not flagged.");
            Assert.That(cso.AttributeValues.All(av => av.Attribute != null), Is.True,
                "Each loaded value's Attribute must be loaded; delete Pending Export stamping reads it.");
        }
    }

    /// <summary>
    /// The fetch runs on the worker's long-lived, tracking DbContext mid-page-flush. Identity
    /// resolution then returns already-tracked CSO instances, and earlier passes of the same page
    /// may have disconnected one in memory (MetaverseObjectId = null) ahead of persistence. The
    /// grouping must therefore key on the database row's MVO ID, not the materialised entity's
    /// in-memory value, or the flush throws for every page that mixes disconnects with deletions.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsForMvoDeletionAsync_ToleratesTrackedInMemoryDisconnectsAsync()
    {
        var (mvoIdA, mvoIdB, _, _) = await SeedTwoGroupsAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // Simulate page processing having disconnected MVO A's CSO in memory, not yet persisted.
        var trackedCso = await ctx.ConnectedSystemObjects.SingleAsync(c => c.MetaverseObjectId == mvoIdA);
        trackedCso.MetaverseObjectId = null;

        var result = await repository.Sync.GetConnectedSystemObjectsForMvoDeletionAsync([mvoIdA, mvoIdB]);

        Assert.That(result.Keys, Is.EquivalentTo(new[] { mvoIdA, mvoIdB }),
            "Grouping must key on the database row's MVO ID; the in-memory disconnect must not break or reassign the grouping.");
        Assert.That(result[mvoIdA].Single().Id, Is.EqualTo(trackedCso.Id),
            "The tracked (in-memory disconnected) instance must still be returned under its database MVO ID.");
    }
}
