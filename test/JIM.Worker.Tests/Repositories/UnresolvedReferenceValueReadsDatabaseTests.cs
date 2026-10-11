// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL coverage for the reads behind a Connected System Object's Unresolved References (#959): which
/// values count as unresolved, how a window of them is ordered and searched, and how they group by parent.
/// </summary>
/// <remarks>
/// The grouping is a regular expression evaluated by PostgreSQL, and the window's ordering and search are SQL
/// translations; the in-memory provider would evaluate neither the way the database does. Opt-in via the same
/// <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class UnresolvedReferenceValueReadsDatabaseTests
{
    private const string BossDn = "CN=Boss,OU=Staff,DC=corp,DC=local";
    private const string BrownDn = "CN=Brown,ou=contractors,dc=corp,dc=local";
    private const string JonesDn = "CN=Jones,OU=Contractors,DC=corp,DC=local";
    private const string SmithDn = @"CN=Smith\, John,OU=Contractors,DC=corp,DC=local";

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL unresolved reference read tests.");

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
    public async Task GetConnectedSystemObjectTypeIdAsync_ObjectInSystem_ReturnsItsTypeAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await repository.ConnectedSystems.GetConnectedSystemObjectTypeIdAsync(seeded.SystemId, seeded.ObjectId), Is.EqualTo(seeded.TypeId));
            Assert.That(await repository.ConnectedSystems.GetConnectedSystemObjectTypeIdAsync(seeded.SystemId + 1000, seeded.ObjectId), Is.Null,
                "An object asked for under another Connected System must not be found.");
            Assert.That(await repository.ConnectedSystems.GetConnectedSystemObjectTypeIdAsync(seeded.SystemId, Guid.NewGuid()), Is.Null);
        }
    }

    [Test]
    public async Task GetUnresolvedReferenceValuesRangeAsync_MixedValues_ReturnsOnlyTheObjectsUnresolvedValuesInOrderAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var window = await repository.ConnectedSystems.GetUnresolvedReferenceValuesRangeAsync(seeded.ObjectId, 0, 50);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.TotalResults, Is.EqualTo(4));
            Assert.That(window.Results.Select(v => (v.AttributeName, v.Value)), Is.EqualTo(new[]
            {
                ("manager", BossDn),
                ("member", BrownDn),
                ("member", JonesDn),
                ("member", SmithDn)
            }), "Resolved values, values on other objects and non-reference values must be left out; the rest ordered by attribute, then value.");
            Assert.That(window.Results.Select(v => v.AttributeId), Is.EqualTo(new[] { seeded.ManagerAttributeId, seeded.MemberAttributeId, seeded.MemberAttributeId, seeded.MemberAttributeId }));
        }
    }

    [Test]
    public async Task GetUnresolvedReferenceValuesRangeAsync_Window_ReturnsTheRequestedSliceAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var window = await repository.ConnectedSystems.GetUnresolvedReferenceValuesRangeAsync(seeded.ObjectId, 1, 2, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.TotalResults, Is.Null, "The total must not be counted when the caller says it holds it already.");
            Assert.That(window.Results.Select(v => v.Value), Is.EqualTo(new[] { BrownDn, JonesDn }));
        }
    }

    [Test]
    public async Task GetUnresolvedReferenceValuesRangeAsync_Search_MatchesValueOrAttributeNameIgnoringCaseAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var byValue = await repository.ConnectedSystems.GetUnresolvedReferenceValuesRangeAsync(seeded.ObjectId, 0, 50, "CONTRACTORS");
        var byAttribute = await repository.ConnectedSystems.GetUnresolvedReferenceValuesRangeAsync(seeded.ObjectId, 0, 50, "Manag");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(byValue.TotalResults, Is.EqualTo(3));
            Assert.That(byValue.Results.Select(v => v.Value), Is.EqualTo(new[] { BrownDn, JonesDn, SmithDn }));
            Assert.That(byAttribute.Results.Select(v => v.Value), Is.EqualTo(new[] { BossDn }));
        }
    }

    [Test]
    public async Task GetUnresolvedReferenceValueCountAsync_MixedValues_CountsOnlyTheObjectsUnresolvedValuesAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        Assert.That(await repository.ConnectedSystems.GetUnresolvedReferenceValueCountAsync(seeded.ObjectId), Is.EqualTo(4));
    }

    [Test]
    public async Task GetUnresolvedReferenceValuesAsync_AttributeFilter_ReturnsOnlyThoseAttributesValuesAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var values = await repository.ConnectedSystems.GetUnresolvedReferenceValuesAsync(seeded.ObjectId, [seeded.ManagerAttributeId]);

        Assert.That(values.Select(v => (v.AttributeId, v.AttributeName, v.Value)), Is.EqualTo(new[] { (seeded.ManagerAttributeId, "manager", BossDn) }));
    }

    [Test]
    public async Task GetUnresolvedReferenceValueGroupsAsync_ValuesSharingAParent_GroupsThemIgnoringCaseAndEscapedCommasAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var groups = await repository.ConnectedSystems.GetUnresolvedReferenceValueGroupsAsync(seeded.ObjectId);

        Assert.That(groups, Has.Count.EqualTo(2), "The three Contractors values share a parent once the escaped comma and the case are accounted for.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups[0].Count, Is.EqualTo(3), "The largest group comes first.");
            Assert.That(groups[0].SampleValue, Is.AnyOf(BrownDn, JonesDn, SmithDn));
            Assert.That(groups[1].Count, Is.EqualTo(1));
            Assert.That(groups[1].SampleValue, Is.EqualTo(BossDn));
        }
    }

    private sealed record Seeded(int SystemId, int TypeId, Guid ObjectId, int ManagerAttributeId, int MemberAttributeId);

    /// <summary>
    /// One Connected System with a user type, an object holding unresolved, resolved and resolved-since values across
    /// two Reference attributes plus a text value, and a second object whose unresolved value must never leak into the
    /// first's reads. Seeded through one context, as the other fixtures here do.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Corp Directory", ConnectorDefinition = connectorDefinition };
        var userType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var anchor = new ConnectedSystemObjectTypeAttribute
        {
            Name = "objectGUID", ConnectedSystemObjectType = userType, Type = AttributeDataType.Guid,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var displayName = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = userType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var manager = new ConnectedSystemObjectTypeAttribute
        {
            Name = "manager", ConnectedSystemObjectType = userType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var member = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = userType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        userType.Attributes.AddRange([anchor, displayName, manager, member]);

        var referenced = NewObject(system, userType, anchor);
        var subject = NewObject(system, userType, anchor);
        var other = NewObject(system, userType, anchor);

        subject.AttributeValues.AddRange(
        [
            new ConnectedSystemObjectAttributeValue { Attribute = displayName, StringValue = "CN=Not a reference,OU=Staff,DC=corp,DC=local" },
            new ConnectedSystemObjectAttributeValue { Attribute = manager, UnresolvedReferenceValue = BossDn },
            new ConnectedSystemObjectAttributeValue { Attribute = member, UnresolvedReferenceValue = SmithDn },
            new ConnectedSystemObjectAttributeValue { Attribute = member, UnresolvedReferenceValue = JonesDn },
            new ConnectedSystemObjectAttributeValue { Attribute = member, UnresolvedReferenceValue = BrownDn },
            // Resolved since: the imported value is retained beside the resolved reference, and is no longer unresolved.
            new ConnectedSystemObjectAttributeValue { Attribute = member, UnresolvedReferenceValue = "CN=Resolved,OU=Contractors,DC=corp,DC=local", ReferenceValue = referenced },
            new ConnectedSystemObjectAttributeValue { Attribute = member, ReferenceValue = referenced }
        ]);
        other.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = member, UnresolvedReferenceValue = "CN=Elsewhere,OU=Contractors,DC=corp,DC=local" });

        seed.ConnectedSystemObjects.AddRange(referenced, subject, other);
        await seed.SaveChangesAsync();

        return new Seeded(system.Id, userType.Id, subject.Id, manager.Id, member.Id);
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
