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
/// <c>GetConnectedSystemObjectsByMetaverseObjectIdsAsync</c> (#3059), the bulk read behind a page's contributor
/// re-elections, returns for each Metaverse Object exactly what the single read
/// <c>GetConnectedSystemObjectsByMetaverseObjectIdAsync</c> returns: every joined object, with its type and every value.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class JoinedObjectsBulkReadDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL joined object bulk read tests.");
        _connectionString = connectionString;

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    [Test]
    public async Task GetConnectedSystemObjectsByMetaverseObjectIdsAsync_SeveralObjects_ReturnsWhatTheSingleReadReturnsForEachAsync()
    {
        var (mvoIds, unjoined) = await SeedAsync();

        Dictionary<Guid, List<ConnectedSystemObject>> bulk;
        await using (var ctx = NewContext())
            bulk = await new PostgresDataRepository(ctx).Sync.GetConnectedSystemObjectsByMetaverseObjectIdsAsync([.. mvoIds, unjoined], -1, 1_000);

        foreach (var mvoId in mvoIds)
        {
            List<ConnectedSystemObject> single;
            await using (var ctx = NewContext())
                single = await new PostgresDataRepository(ctx).Sync.GetConnectedSystemObjectsByMetaverseObjectIdAsync(mvoId);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(bulk[mvoId].Select(Describe).OrderBy(d => d), Is.EqualTo(single.Select(Describe).OrderBy(d => d)),
                    "the same objects, types and values as the single read");
                Assert.That(bulk[mvoId], Has.Count.EqualTo(2), "both joined objects");
                Assert.That(bulk[mvoId].All(cso => cso.Type != null && cso.AttributeValues.All(av => av.Attribute != null)), Is.True,
                    "types and value attributes loaded, so re-election needs no further read");
            }
        }

        Assert.That(bulk[unjoined], Is.Empty, "an object with nothing joined is present, with no objects");
    }

    [Test]
    public async Task GetConnectedSystemObjectsByMetaverseObjectIdsAsync_ExcludedSystemAndValueBudget_LeavesThoseOutAsync()
    {
        var (mvoIds, _) = await SeedAsync();
        int systemA;
        await using (var ctx = NewContext())
            systemA = await ctx.ConnectedSystems.Where(cs => cs.Name == "A").Select(cs => cs.Id).SingleAsync();

        Dictionary<Guid, List<ConnectedSystemObject>> withoutA, overBudget;
        await using (var ctx = NewContext())
            withoutA = await new PostgresDataRepository(ctx).Sync.GetConnectedSystemObjectsByMetaverseObjectIdsAsync(mvoIds, systemA, 1_000);
        await using (var ctx = NewContext())
            overBudget = await new PostgresDataRepository(ctx).Sync.GetConnectedSystemObjectsByMetaverseObjectIdsAsync(mvoIds, systemA, 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withoutA.Values.SelectMany(list => list).Select(cso => cso.ConnectedSystemId), Has.None.EqualTo(systemA),
                "the excluded system's objects are left out");
            Assert.That(withoutA.Values.All(list => list.Count == 1), Is.True, "each object keeps its other system's object");
            Assert.That(overBudget, Is.Empty, "an object whose other objects hold more values than the budget (three here) is left to the single read");
        }
    }

    private static string Describe(ConnectedSystemObject cso) =>
        $"{cso.Id}|{cso.Type.Name}|" + string.Join(",", cso.AttributeValues.OrderBy(av => av.StringValue).Select(av => $"{av.Attribute.Name}={av.StringValue}"));

    /// <summary>Three Metaverse Objects each joined to one object in each of two Connected Systems, and one joined to none.</summary>
    private async Task<(List<Guid> MvoIds, Guid Unjoined)> SeedAsync()
    {
        await using var seed = NewContext();
        var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People", DeletionRule = MetaverseObjectDeletionRule.Manual };
        var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
        seed.AddRange(mvType, connectorDefinition);

        var mvos = Enumerable.Range(0, 4).Select(_ => new MetaverseObject { Id = Guid.NewGuid(), Type = mvType }).ToList();
        seed.MetaverseObjects.AddRange(mvos);

        foreach (var name in new[] { "A", "B" })
        {
            var system = new ConnectedSystem { Name = name, ConnectorDefinition = connectorDefinition };
            var type = new ConnectedSystemObjectType { Name = $"{name}-person", ConnectedSystem = system, Selected = true };
            var id = new ConnectedSystemObjectTypeAttribute { Name = "id", ConnectedSystemObjectType = type, Type = AttributeDataType.Text, Selected = true, IsExternalId = true };
            var mail = new ConnectedSystemObjectTypeAttribute
                { Name = "mail", ConnectedSystemObjectType = type, Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued, Selected = true };
            type.Attributes.AddRange([id, mail]);
            seed.AddRange(system, type);
            await seed.SaveChangesAsync();

            foreach (var mvo in mvos.Take(3))
            {
                var cso = new ConnectedSystemObject
                {
                    Id = Guid.NewGuid(), ConnectedSystem = system, Type = type, ExternalIdAttributeId = id.Id, MetaverseObject = mvo,
                    Status = ConnectedSystemObjectStatus.Normal, Created = DateTime.UtcNow
                };
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = id, StringValue = $"{name}-{mvo.Id}" });
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = mail, StringValue = $"{name}1@{mvo.Id}" });
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = mail, StringValue = $"{name}2@{mvo.Id}" });
                seed.ConnectedSystemObjects.Add(cso);
            }
        }

        await seed.SaveChangesAsync();
        return (mvos.Take(3).Select(m => m.Id).ToList(), mvos[3].Id);
    }
}
