// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL coverage for the Connected System-wide reads behind the Unresolved References signpost, the
/// Connector Space filter and "Where they point" (#959): how many values and objects a Connected System holds, how its
/// values group by parent with the objects behind each group, and which objects the Connector Space shows when
/// filtered.
/// </summary>
/// <remarks>
/// The grouping is a regular expression evaluated by PostgreSQL and the counts are SQL aggregates over a partial index,
/// neither of which the in-memory provider evaluates the way the database does. Opt-in via the same
/// <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemUnresolvedReferenceReadsDatabaseTests
{
    private const string BossDn = "CN=Boss,OU=Staff,DC=corp,DC=local";
    private const string BrownDn = "CN=Brown,ou=contractors,dc=corp,dc=local";
    private const string JonesDn = "CN=Jones,OU=Contractors,DC=corp,DC=local";
    private const string SmithDn = @"CN=Smith\, John,OU=Contractors,DC=corp,DC=local";
    private const string EmeaDn = "CN=Kowalski,OU=Staff,DC=emea,DC=corp,DC=local";

    private const string ContractorsKey = "ou=contractors,dc=corp,dc=local";
    private const string StaffKey = "ou=staff,dc=corp,dc=local";
    private const string EmeaStaffKey = "ou=staff,dc=emea,dc=corp,dc=local";

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Connected System unresolved reference read tests.");

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

    [Test]
    public async Task Migration_CreatesThePartialIndexOnCurrentlyUnresolvedValuesAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT indexdef FROM pg_indexes
            WHERE tablename = 'ConnectedSystemObjectAttributeValues'
              AND indexname = 'IX_ConnectedSystemObjectAttributeValues_CsoId_Unresolved'
            """, connection);
        var definition = (string?)await command.ExecuteScalarAsync();

        Assert.That(definition, Is.Not.Null, "The signpost, the filter and the per-row count all read through this index.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(definition!, Does.Contain("(\"ConnectedSystemObjectId\")"));
            Assert.That(definition!, Does.Contain("\"UnresolvedReferenceValue\" IS NOT NULL"));
            Assert.That(definition!, Does.Contain("\"ReferenceValueId\" IS NULL"),
                "A resolved value keeps its imported text, so only the missing reference marks a value as still unresolved.");
        }
    }

    [Test]
    public async Task GetUnresolvedReferenceTotalsAsync_MixedValues_CountsTheSystemsUnresolvedValuesAndTheirObjectsAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var totals = await repository.ConnectedSystems.GetUnresolvedReferenceTotalsAsync(seeded.SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(totals.ReferenceCount, Is.EqualTo(6), "Resolved values and another Connected System's values must be left out.");
            Assert.That(totals.ObjectCount, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task GetUnresolvedReferenceTotalsAsync_NothingUnresolved_ReturnsZerosAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var totals = await repository.ConnectedSystems.GetUnresolvedReferenceTotalsAsync(seeded.SystemId + 1000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(totals.ReferenceCount, Is.Zero);
            Assert.That(totals.ObjectCount, Is.Zero);
        }
    }

    [Test]
    public async Task GetConnectedSystemUnresolvedReferenceValueGroupsAsync_ValuesAcrossObjects_GroupsByParentWithTheObjectsBehindEachAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var groups = await repository.ConnectedSystems.GetConnectedSystemUnresolvedReferenceValueGroupsAsync(seeded.SystemId);

        Assert.That(groups.Select(g => (g.ParentKey, g.Count, g.ObjectCount)), Is.EqualTo(new[]
        {
            (ContractorsKey, 3, 2),
            (StaffKey, 2, 2),
            (EmeaStaffKey, 1, 1)
        }), "Grouped across every object, ignoring case and escaped commas; the largest group first.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups[0].SampleValue, Is.AnyOf(BrownDn, JonesDn, SmithDn));
            Assert.That(groups[1].SampleValue, Is.EqualTo(BossDn));
            Assert.That(groups[2].SampleValue, Is.EqualTo(EmeaDn));
        }
    }

    [Test]
    public async Task GetUnresolvedReferenceObjectCountAsync_SeveralParents_CountsEachObjectOnceAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await repository.ConnectedSystems.GetUnresolvedReferenceObjectCountAsync(seeded.SystemId, [ContractorsKey, StaffKey]), Is.EqualTo(3),
                "The group with values under both parents is one object, not two.");
            Assert.That(await repository.ConnectedSystems.GetUnresolvedReferenceObjectCountAsync(seeded.SystemId, [EmeaStaffKey]), Is.EqualTo(1));
            Assert.That(await repository.ConnectedSystems.GetUnresolvedReferenceObjectCountAsync(seeded.SystemId, []), Is.Zero);
        }
    }

    [Test]
    public async Task GetDistinctUnresolvedReferenceValuesAsync_AttributeFilter_ReturnsEachOfThoseAttributesValuesOnceAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var managerValues = await repository.ConnectedSystems.GetDistinctUnresolvedReferenceValuesAsync(seeded.SystemId, [seeded.ManagerAttributeId]);
        var none = await repository.ConnectedSystems.GetDistinctUnresolvedReferenceValuesAsync(seeded.SystemId, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(managerValues, Is.EqualTo(new[] { BossDn }), "The manager value appears once, on one object; the member values on the same parent are another attribute's.");
            Assert.That(none, Is.Empty);
        }
    }

    [Test]
    public async Task GetUnresolvedReferenceValueSplitAsync_SomeValuesSingledOut_CountsThemAndTheRestSeparatelyAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var split = await repository.ConnectedSystems.GetUnresolvedReferenceValueSplitAsync(seeded.SystemId, [seeded.MemberAttributeId], [JonesDn, BossDn]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(split.MatchingReferenceCount, Is.EqualTo(2), "Jones and Boss on member; the manager value Boss is another attribute's and stays in the rest.");
            Assert.That(split.MatchingObjectCount, Is.EqualTo(1));
            Assert.That(split.OtherReferenceCount, Is.EqualTo(4));
            Assert.That(split.OtherObjectCount, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectHeadersRangeAsync_FilteredToUnresolvedReferences_ReturnsOnlyThoseObjectsWithTheirCountsAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var window = await repository.ConnectedSystems.GetConnectedSystemObjectHeadersRangeAsync(
            seeded.SystemId, 0, 50, hasUnresolvedReferences: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.TotalResults, Is.EqualTo(3));
            Assert.That(window.Results.Select(h => (h.Id, h.UnresolvedReferenceCount)), Is.EquivalentTo(new[]
            {
                (seeded.GroupAId, 3),
                (seeded.GroupBId, 2),
                (seeded.UserCId, 1)
            }));
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectHeadersRangeAsync_FilteredToNoUnresolvedReferences_ReturnsTheRestAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var window = await repository.ConnectedSystems.GetConnectedSystemObjectHeadersRangeAsync(
            seeded.SystemId, 0, 50, hasUnresolvedReferences: false);

        Assert.That(window.Results.Select(h => (h.Id, h.UnresolvedReferenceCount)), Is.EquivalentTo(new[]
        {
            (seeded.ReferencedId, 0),
            (seeded.UserDId, 0)
        }), "The object whose only reference resolved has nothing unresolved.");
    }

    [Test]
    public async Task GetConnectedSystemObjectHeadersAsync_Unfiltered_CarriesEachObjectsCountAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var page = await repository.ConnectedSystems.GetConnectedSystemObjectHeadersAsync(seeded.SystemId, 1, 50);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.TotalResults, Is.EqualTo(5));
            Assert.That(page.Results.Single(h => h.Id == seeded.GroupAId).UnresolvedReferenceCount, Is.EqualTo(3));
            Assert.That(page.Results.Single(h => h.Id == seeded.UserDId).UnresolvedReferenceCount, Is.Zero);
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectHeadersAsync_FilteredToUnresolvedReferences_PagesOnlyThoseObjectsAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var page = await repository.ConnectedSystems.GetConnectedSystemObjectHeadersAsync(seeded.SystemId, 1, 50, hasUnresolvedReferences: true);

        Assert.That(page.Results.Select(h => h.Id), Is.EquivalentTo(new[] { seeded.GroupAId, seeded.GroupBId, seeded.UserCId }));
    }

    private sealed record Seeded(
        int SystemId,
        int ManagerAttributeId,
        int MemberAttributeId,
        Guid ReferencedId,
        Guid GroupAId,
        Guid GroupBId,
        Guid UserCId,
        Guid UserDId);

    /// <summary>
    /// One Connected System with five objects: two groups whose members are partly unresolved (one of them across two
    /// parents), a user whose manager is unresolved, a user with nothing unresolved, and the object one resolved
    /// reference points at. A second Connected System holds an unresolved value that must never be counted.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Corp Directory", ConnectorDefinition = connectorDefinition };
        var otherSystem = new ConnectedSystem { Name = "Lab Directory", ConnectorDefinition = connectorDefinition };
        var (type, anchor, manager, member) = NewType(system);
        var (otherType, otherAnchor, _, otherMember) = NewType(otherSystem);

        var referenced = NewObject(system, type, anchor);
        var groupA = NewObject(system, type, anchor);
        var groupB = NewObject(system, type, anchor);
        var userC = NewObject(system, type, anchor);
        var userD = NewObject(system, type, anchor);
        var other = NewObject(otherSystem, otherType, otherAnchor);

        groupA.AttributeValues.AddRange(
        [
            new ConnectedSystemObjectAttributeValue { Attribute = member, UnresolvedReferenceValue = JonesDn },
            new ConnectedSystemObjectAttributeValue { Attribute = member, UnresolvedReferenceValue = SmithDn },
            new ConnectedSystemObjectAttributeValue { Attribute = member, UnresolvedReferenceValue = BossDn },
            // Resolved since: the imported value is retained beside the resolved reference, and no longer counts.
            new ConnectedSystemObjectAttributeValue { Attribute = member, UnresolvedReferenceValue = "CN=Resolved,OU=Contractors,DC=corp,DC=local", ReferenceValue = referenced }
        ]);
        groupB.AttributeValues.AddRange(
        [
            new ConnectedSystemObjectAttributeValue { Attribute = member, UnresolvedReferenceValue = BrownDn },
            new ConnectedSystemObjectAttributeValue { Attribute = member, UnresolvedReferenceValue = EmeaDn }
        ]);
        userC.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = manager, UnresolvedReferenceValue = BossDn });
        userD.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = manager, ReferenceValue = referenced });
        other.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = otherMember, UnresolvedReferenceValue = JonesDn });

        seed.ConnectedSystemObjects.AddRange(referenced, groupA, groupB, userC, userD, other);
        await seed.SaveChangesAsync();

        return new Seeded(system.Id, manager.Id, member.Id, referenced.Id, groupA.Id, groupB.Id, userC.Id, userD.Id);
    }

    private static (ConnectedSystemObjectType Type, ConnectedSystemObjectTypeAttribute Anchor, ConnectedSystemObjectTypeAttribute Manager, ConnectedSystemObjectTypeAttribute Member)
        NewType(ConnectedSystem system)
    {
        var type = new ConnectedSystemObjectType { Name = "entry", ConnectedSystem = system, Selected = true };
        var anchor = new ConnectedSystemObjectTypeAttribute
        {
            Name = "objectGUID", ConnectedSystemObjectType = type, Type = AttributeDataType.Guid,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var manager = new ConnectedSystemObjectTypeAttribute
        {
            Name = "manager", ConnectedSystemObjectType = type, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var member = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = type, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        type.Attributes.AddRange([anchor, manager, member]);
        return (type, anchor, manager, member);
    }

    private static ConnectedSystemObject NewObject(ConnectedSystem system, ConnectedSystemObjectType type, ConnectedSystemObjectTypeAttribute anchor)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = system,
            Type = type,
            ExternalIdAttributeId = 0,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined,
            Created = DateTime.UtcNow
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = anchor, GuidValue = Guid.NewGuid() });
        return cso;
    }
}
