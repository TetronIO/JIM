// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using JIM.Application.Utilities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL coverage for the display identifiers a Pending Export's change snapshot shows for the objects its
/// references point at (#2598, Phase 3). The snapshot needs one string per referenced object, so the read loads only
/// the values a name can come from, where loading the whole object brought every member value of a referenced group
/// (14.8 s for one group at Scale750k40kGroups). Each identifier must be exactly what
/// <see cref="ExportChangeHistoryBuilder.GetCsoDisplayIdentifier"/> gives for the fully loaded object, so these tests use
/// that as the oracle across every shape a name can take. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ReferenceDisplayIdentifierDatabaseTests
{
    private static readonly ActivitySource TestSource = new("JIM.Tests.ReferenceDisplayIdentifiers");

    private string _connectionString = null!;
    private ActivityListener _listener = null!;
    private readonly ConcurrentQueue<Activity> _stopped = new();

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL reference display identifier tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();

        // JIM's own spans, and Npgsql's span per command, so a test can count the statements a lookup sends.
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("JIM.", StringComparison.Ordinal) || source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _stopped.Enqueue
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _listener?.Dispose();

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    [Test]
    public async Task GetReferenceDisplayIdentifiersByMetaverseObjectIdsAsync_EveryNamingShape_MatchesTheFullyLoadedObjectsIdentifierAsync()
    {
        var s = await SeedSchemaAsync();
        var anchor = Guid.NewGuid();
        var shapes = new[]
        {
            // A display name wins over everything else.
            await SeedAsync(s, s.SystemId, (s.DisplayName, v => v.StringValue = "Platform Engineers"), (s.Dn, v => v.StringValue = "CN=Platform Engineers,OU=Groups,DC=glitterband,DC=local"), (s.ObjectGuid, v => v.GuidValue = Guid.NewGuid())),
            // A name attribute matched whatever its case.
            await SeedAsync(s, s.SystemId, (s.CommonName, v => v.StringValue = "Platform Leads"), (s.ObjectGuid, v => v.GuidValue = Guid.NewGuid())),
            // A blank display name is not a name; the next candidate is.
            await SeedAsync(s, s.SystemId, (s.DisplayName, v => v.StringValue = " "), (s.CommonName, v => v.StringValue = "Release Managers")),
            // No name: the external id, rendered from its typed value.
            await SeedAsync(s, s.SystemId, (s.ObjectGuid, v => v.GuidValue = anchor)),
            // No name and no primary value: the secondary external id (the DN).
            await SeedAsync(s, s.SystemId, (s.Dn, v => v.StringValue = "CN=Auditors,OU=Groups,DC=glitterband,DC=local")),
            // Nothing to name it by: its own id.
            await SeedAsync(s, s.SystemId),
            // A large group: its members are not part of its name.
            await SeedAsync(s, s.SystemId, [(s.DisplayName, v => v.StringValue = "All Staff"),
                .. Enumerable.Range(0, 200).Select(i => (s.Member, (Action<ConnectedSystemObjectAttributeValue>)(v => v.UnresolvedReferenceValue = $"CN=Person {i}")))])
        };

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var identifiers = await repository.Sync.GetReferenceDisplayIdentifiersByMetaverseObjectIdsAsync(shapes, s.SystemId);
        var fullyLoaded = await ctx.ConnectedSystemObjects.AsNoTracking()
            .Include(cso => cso.AttributeValues).ThenInclude(av => av.Attribute)
            .Where(cso => cso.ConnectedSystemId == s.SystemId && cso.MetaverseObjectId.HasValue && shapes.Contains(cso.MetaverseObjectId.Value))
            .ToDictionaryAsync(cso => cso.MetaverseObjectId!.Value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(identifiers.Keys, Is.EquivalentTo(shapes));
            foreach (var mvoId in shapes)
                Assert.That(identifiers[mvoId], Is.EqualTo(ExportChangeHistoryBuilder.GetCsoDisplayIdentifier(fullyLoaded[mvoId])), $"Metaverse Object {mvoId}");
            Assert.That(identifiers[shapes[0]], Is.EqualTo("Platform Engineers"), "the oracle itself names the first shape by its display name");
            Assert.That(identifiers[shapes[3]], Is.EqualTo(anchor.ToString()));
        }
    }

    [Test]
    public async Task GetReferenceDisplayIdentifiersByMetaverseObjectIdsAsync_ObjectsInAnotherSystemOrNotAsked_AreNotReturnedAsync()
    {
        var s = await SeedSchemaAsync();
        var askedInOtherSystem = await SeedAsync(s, s.OtherSystemId, (s.DisplayName, v => v.StringValue = "Elsewhere"));
        var notAsked = await SeedAsync(s, s.SystemId, (s.DisplayName, v => v.StringValue = "Not asked"));

        await using var ctx = NewContext();
        var identifiers = await new PostgresDataRepository(ctx).Sync
            .GetReferenceDisplayIdentifiersByMetaverseObjectIdsAsync([askedInOtherSystem, Guid.NewGuid()], s.SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(identifiers, Is.Empty);
            Assert.That(notAsked, Is.Not.EqualTo(Guid.Empty));
        }
    }

    [Test]
    public async Task GetReferenceDisplayIdentifiersByMetaverseObjectIdsAsync_TrackingContext_LeavesNothingTrackedAsync()
    {
        var s = await SeedSchemaAsync();
        var mvoId = await SeedAsync(s, s.SystemId, (s.DisplayName, v => v.StringValue = "Platform Engineers"));

        await using var ctx = NewContext();
        ctx.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        await new PostgresDataRepository(ctx).Sync.GetReferenceDisplayIdentifiersByMetaverseObjectIdsAsync([mvoId], s.SystemId);

        Assert.That(ctx.ChangeTracker.Entries(), Is.Empty);
    }

    [Test]
    public async Task GetReferenceDisplayIdentifiersByMetaverseObjectIdsAsync_MoreIdsThanTwoChunks_ReturnsEveryFoundObjectOnceWithItsIdentifierAsync()
    {
        // Two chunks and one id over (#2939), with the objects that exist placed either side of each chunk boundary and
        // every other id naming nothing in the system, as most of a large group's members do on a first export.
        const int chunkSize = SyncRepository.ReferenceDisplayIdentifierChunkSize;
        var s = await SeedSchemaAsync();
        var ids = Enumerable.Range(0, (chunkSize * 2) + 1).Select(_ => Guid.NewGuid()).ToList();

        var named = 0;
        var placed = new List<Guid>();
        foreach (var position in new[] { 0, 1, chunkSize - 1, chunkSize, chunkSize + 1, (chunkSize * 2) - 1, chunkSize * 2 })
        {
            // Alternate the naming shapes: a display name, only an external id (no name attribute), and nothing at all.
            ids[position] = (named++ % 3) switch
            {
                0 => await SeedAsync(s, s.SystemId, (s.DisplayName, v => v.StringValue = $"Group at {position}"), (s.ObjectGuid, v => v.GuidValue = Guid.NewGuid())),
                1 => await SeedAsync(s, s.SystemId, (s.ObjectGuid, v => v.GuidValue = Guid.NewGuid())),
                _ => await SeedAsync(s, s.SystemId)
            };
            placed.Add(ids[position]);
        }

        // Asked for, but joined in another system, at a chunk boundary.
        var inOtherSystem = await SeedAsync(s, s.OtherSystemId, (s.DisplayName, v => v.StringValue = "Elsewhere"));
        ids[chunkSize - 2] = inOtherSystem;
        // The first id asked for again at the end, so it falls in another chunk as well.
        ids.Add(ids[0]);

        await using var ctx = NewContext();
        var identifiers = await new PostgresDataRepository(ctx).Sync.GetReferenceDisplayIdentifiersByMetaverseObjectIdsAsync(ids, s.SystemId);
        var fullyLoaded = await ctx.ConnectedSystemObjects.AsNoTracking()
            .Include(cso => cso.AttributeValues).ThenInclude(av => av.Attribute)
            .Where(cso => cso.ConnectedSystemId == s.SystemId && cso.MetaverseObjectId.HasValue && placed.Contains(cso.MetaverseObjectId.Value))
            .ToDictionaryAsync(cso => cso.MetaverseObjectId!.Value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(identifiers.Keys, Is.EquivalentTo(placed), "every object found, none lost at a boundary, nothing else returned");
            foreach (var mvoId in placed)
                Assert.That(identifiers[mvoId], Is.EqualTo(ExportChangeHistoryBuilder.GetCsoDisplayIdentifier(fullyLoaded[mvoId])), $"Metaverse Object {mvoId}");
            Assert.That(identifiers[placed[0]], Is.EqualTo("Group at 0"), "the oracle itself names the first object by its display name");
        }
    }

    [Test]
    public async Task GetReferenceDisplayIdentifiersByMetaverseObjectIdsAsync_MoreIdsThanTwoChunks_SendsOneBoundedStatementPerChunkAsync()
    {
        // A statement given every id at once was planned as parallel sequential scans of the attribute values once the
        // array was large (500,000 ids in the lab); each statement must stay within one chunk's worth (#2939).
        const int chunkSize = SyncRepository.ReferenceDisplayIdentifierChunkSize;
        var s = await SeedSchemaAsync();
        var ids = Enumerable.Range(0, (chunkSize * 2) + 1).Select(_ => Guid.NewGuid()).ToList();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var spans = await RecordAsync(() => repository.Sync.GetReferenceDisplayIdentifiersByMetaverseObjectIdsAsync(ids, s.SystemId));

        var lookups = spans.Where(span => span.Source.Name == "Npgsql"
            && span.TagObjects.Any(t => t.Value is string text && text.Contains("LEFT JOIN LATERAL", StringComparison.Ordinal))).ToList();
        var load = spans.SingleOrDefault(span => span.DisplayName == "Cso.LoadReferenceDisplayIdentifiers");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lookups, Has.Count.EqualTo(3), "one statement per chunk of ids");
            Assert.That(load, Is.Not.Null, "the lookup reports a span of its own");
            Assert.That(load?.GetTagItem("idCount"), Is.EqualTo(ids.Count));
            Assert.That(load?.GetTagItem("chunkCount"), Is.EqualTo(3));
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> under a span of the test's own and returns every span completed in its trace.
    /// </summary>
    private async Task<List<Activity>> RecordAsync(Func<Task> action)
    {
        ActivityTraceId traceId;
        using (var root = TestSource.StartActivity("Test"))
        {
            Assert.That(root, Is.Not.Null, "the test's listener samples its own source");
            traceId = root!.TraceId;
            await action();
        }
        return [.. _stopped.Where(span => span.TraceId == traceId)];
    }

    private sealed record Schema(
        int SystemId, int OtherSystemId, int TypeId, int MvTypeId,
        int ObjectGuid, int Dn, int DisplayName, int CommonName, int Member);

    private async Task<Schema> SeedSchemaAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        var other = new ConnectedSystem { Name = "Glitterband APAC", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };

        ConnectedSystemObjectTypeAttribute Attribute(string name, AttributeDataType dataType,
            AttributePlurality plurality = AttributePlurality.SingleValued)
        {
            var attribute = new ConnectedSystemObjectTypeAttribute
            {
                Name = name, ConnectedSystemObjectType = type, Type = dataType, AttributePlurality = plurality, Selected = true
            };
            type.Attributes.Add(attribute);
            return attribute;
        }

        var objectGuid = Attribute("objectGUID", AttributeDataType.Guid);
        var dn = Attribute("distinguishedName", AttributeDataType.Text);
        var displayName = Attribute("displayName", AttributeDataType.Text);
        var commonName = Attribute("CN", AttributeDataType.Text);
        var member = Attribute("member", AttributeDataType.Reference, AttributePlurality.MultiValued);
        var mvType = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };

        seed.AddRange(connectorDefinition, system, other, type, mvType);
        await seed.SaveChangesAsync();
        return new Schema(system.Id, other.Id, type.Id, mvType.Id, objectGuid.Id, dn.Id, displayName.Id, commonName.Id, member.Id);
    }

    /// <summary>
    /// A Metaverse Object joined to a new object in <paramref name="systemId"/> holding <paramref name="values"/>; returns
    /// the Metaverse Object's id. The object's external ids are objectGUID and distinguishedName, as for an LDAP group.
    /// </summary>
    private async Task<Guid> SeedAsync(Schema s, int systemId, params (int AttributeId, Action<ConnectedSystemObjectAttributeValue> Set)[] values)
    {
        await using var seed = NewContext();
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(), Created = DateTime.UtcNow,
            Type = await seed.MetaverseObjectTypes.AsTracking().SingleAsync(t => t.Id == s.MvTypeId)
        };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = systemId, TypeId = s.TypeId, MetaverseObject = mvo,
            ExternalIdAttributeId = s.ObjectGuid, SecondaryExternalIdAttributeId = s.Dn,
            Status = ConnectedSystemObjectStatus.Normal, JoinType = ConnectedSystemObjectJoinType.Joined
        };
        foreach (var (attributeId, set) in values)
        {
            var value = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId };
            set(value);
            cso.AttributeValues.Add(value);
        }
        seed.AddRange(mvo, cso);
        await seed.SaveChangesAsync();
        return mvo.Id;
    }
}
