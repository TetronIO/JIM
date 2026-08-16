// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the two offset/count attribute value range reads that back a virtualised
/// (infinite-scroll) multi-valued attribute on the Connected System Object and Metaverse Object detail pages
/// (<see cref="JIM.Application.Servers.ConnectedSystemServer.GetAttributeValuesRangeAsync"/> and
/// <see cref="JIM.Application.Servers.MetaverseServer.GetAttributeValuesRangeAsync"/>). Both queries search
/// through a reference value's own attribute values and eager-load an Include path that cycles back to the
/// value's owner, which is why they run tracked and split; the in-memory provider resolves those navigations
/// for free and so cannot show whether the query really loads them.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class AttributeValueRangeDatabaseTests
{
    private const string MemberAttributeName = "member";
    private const string ManagerAttributeName = "Manager";

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL attribute value range tests.");

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

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    // -----------------------------------------------------------------------------------------------------------------
    // Connected System Object attribute values
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task CsoRange_MidWindow_ReturnsCorrectSliceAndFullTotalAsync()
    {
        var csoId = await SeedCsoValuesAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: 3, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(av => av.StringValue),
                Is.EqualTo(new[] { "CN=Member 004", "CN=Member 005", "CN=Member 006" }));
        }
    }

    [Test]
    public async Task CsoRange_TotalCountNotRequested_ReturnsWindowWithNullTotalAsync()
    {
        var csoId = await SeedCsoValuesAsync(10);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: 3, count: 3, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches".
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results, Has.Count.EqualTo(3));
        }
    }

    [Test]
    public async Task CsoRange_ConsecutiveWindows_PartitionTheValuesExactlyAsync()
    {
        var csoId = await SeedCsoValuesAsync(20);
        var jim = NewJim();

        var first = await jim.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: 0, count: 10);
        var second = await jim.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: 10, count: 10);

        var seen = first.Results.Select(av => av.Id).Concat(second.Results.Select(av => av.Id)).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(seen, Has.Count.EqualTo(20));
            Assert.That(seen.Distinct().Count(), Is.EqualTo(20));
        }
    }

    [Test]
    public async Task CsoRange_SearchThroughTheReferencedObjectsName_MatchesAndRestrictsTotalAsync()
    {
        // The reference branch of the search reaches through ReferenceValue into the referenced object's own
        // attribute values; the window then eager-loads that same path, which cycles back to a Connected System
        // Object. Only a real database exercises either.
        var csoId = await SeedCsoReferenceValuesAsync(["Alan Alpha", "Mia Mu", "Zoe Zeta"]);
        var jim = NewJim();

        var result = await jim.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: 0, count: 10, searchText: "mia");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Single().ReferenceValue, Is.Not.Null);
            Assert.That(result.Results.Single().ReferenceValue!.AttributeValues.Select(av => av.StringValue),
                Does.Contain("Mia Mu"));
        }
    }

    [Test]
    public async Task CsoRange_FullWindow_MatchesPagedReaderAsync()
    {
        var csoId = await SeedCsoValuesAsync(10);
        var jim = NewJim();

        var range = await jim.ConnectedSystems.GetAttributeValuesRangeAsync(
            csoId, MemberAttributeName, offset: 0, count: 10);
        var paged = await jim.ConnectedSystems.GetAttributeValuesPagedAsync(
            csoId, MemberAttributeName, page: 1, pageSize: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(av => av.Id), Is.EqualTo(paged.Results.Select(av => av.Id)));
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Metaverse Object attribute values
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task MvoRange_MidWindow_ReturnsCorrectSliceAndFullTotalAsync()
    {
        var mvoId = await SeedMvoValuesAsync(10);
        var jim = NewJim();

        var result = await jim.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ManagerAttributeName, offset: 3, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(av => av.StringValue),
                Is.EqualTo(new[] { "value 004", "value 005", "value 006" }));
        }
    }

    [Test]
    public async Task MvoRange_TotalCountNotRequested_ReturnsWindowWithNullTotalAsync()
    {
        var mvoId = await SeedMvoValuesAsync(10);
        var jim = NewJim();

        var result = await jim.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ManagerAttributeName, offset: 3, count: 3, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches".
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results, Has.Count.EqualTo(3));
        }
    }

    [Test]
    public async Task MvoRange_ConsecutiveWindows_PartitionTheValuesExactlyAsync()
    {
        var mvoId = await SeedMvoValuesAsync(20);
        var jim = NewJim();

        var first = await jim.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ManagerAttributeName, offset: 0, count: 10);
        var second = await jim.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ManagerAttributeName, offset: 10, count: 10);

        var seen = first.Results.Select(av => av.Id).Concat(second.Results.Select(av => av.Id)).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(seen, Has.Count.EqualTo(20));
            Assert.That(seen.Distinct().Count(), Is.EqualTo(20));
        }
    }

    [Test]
    public async Task MvoRange_SearchThroughTheReferencedObjectsDisplayName_MatchesAndRestrictsTotalAsync()
    {
        // As on the Connected System Object side, the reference branch of the search reaches through
        // ReferenceValue into the referenced Metaverse Object's own attribute values.
        var mvoId = await SeedMvoReferenceValuesAsync(["Alan Alpha", "Mia Mu", "Zoe Zeta"]);
        var jim = NewJim();

        var result = await jim.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ManagerAttributeName, offset: 0, count: 10, searchText: "mia");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Single().ReferenceValue, Is.Not.Null);
            Assert.That(result.Results.Single().ReferenceValue!.AttributeValues.Select(av => av.StringValue),
                Does.Contain("Mia Mu"));
        }
    }

    [Test]
    public async Task MvoRange_FullWindow_MatchesPagedReaderAsync()
    {
        var mvoId = await SeedMvoValuesAsync(10);
        var jim = NewJim();

        var range = await jim.Metaverse.GetAttributeValuesRangeAsync(
            mvoId, ManagerAttributeName, offset: 0, count: 10);
        var paged = await jim.Metaverse.GetAttributeValuesPagedAsync(
            mvoId, ManagerAttributeName, page: 1, pageSize: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(av => av.Id), Is.EqualTo(paged.Results.Select(av => av.Id)));
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Seeding
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Deterministic id for the nth seeded value, varying only the GUID's last group so .NET and PostgreSQL
    /// order the values identically and the read's id order is the seeding order.
    /// </summary>
    private static Guid IdFor(int ordinal) => new($"00000000-0000-0000-0000-{ordinal:D12}");

    /// <summary>
    /// Seeds a Connected System Object with <paramref name="count"/> plain string values of a multi-valued
    /// "member" attribute. Returns the object's id.
    /// </summary>
    private async Task<Guid> SeedCsoValuesAsync(int count)
    {
        await using var ctx = NewContext();
        var (connectedSystem, objectType, memberAttribute, _) = BuildCsoSchema(ctx);
        await ctx.SaveChangesAsync();

        var cso = NewCso(connectedSystem, objectType);
        ctx.Add(cso);
        for (var i = 1; i <= count; i++)
            ctx.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = IdFor(i),
                ConnectedSystemObject = cso,
                Attribute = memberAttribute,
                StringValue = $"CN=Member {i:D3}"
            });

        await ctx.SaveChangesAsync();
        return cso.Id;
    }

    /// <summary>
    /// Seeds a Connected System Object whose "member" values are resolved references to other Connected System
    /// Objects, each carrying one of <paramref name="referencedDisplayNames"/>. Returns the owning object's id.
    /// </summary>
    private async Task<Guid> SeedCsoReferenceValuesAsync(IReadOnlyList<string> referencedDisplayNames)
    {
        await using var ctx = NewContext();
        var (connectedSystem, objectType, memberAttribute, displayNameAttribute) = BuildCsoSchema(ctx);
        await ctx.SaveChangesAsync();

        var owner = NewCso(connectedSystem, objectType);
        ctx.Add(owner);

        for (var i = 0; i < referencedDisplayNames.Count; i++)
        {
            var referenced = NewCso(connectedSystem, objectType);
            referenced.AttributeValues =
            [
                new ConnectedSystemObjectAttributeValue { Attribute = displayNameAttribute, StringValue = referencedDisplayNames[i] }
            ];
            ctx.Add(referenced);
            ctx.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = IdFor(i + 1),
                ConnectedSystemObject = owner,
                Attribute = memberAttribute,
                ReferenceValue = referenced
            });
        }

        await ctx.SaveChangesAsync();
        return owner.Id;
    }

    private static (ConnectedSystem ConnectedSystem, ConnectedSystemObjectType ObjectType, ConnectedSystemObjectTypeAttribute MemberAttribute, ConnectedSystemObjectTypeAttribute DisplayNameAttribute) BuildCsoSchema(JimDbContext ctx)
    {
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = connectedSystem, Selected = true };
        var memberAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = MemberAttributeName,
            ConnectedSystemObjectType = objectType,
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued,
            Selected = true
        };
        var displayNameAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName",
            ConnectedSystemObjectType = objectType,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true
        };
        objectType.Attributes.Add(memberAttribute);
        objectType.Attributes.Add(displayNameAttribute);
        ctx.AddRange(connectorDefinition, connectedSystem, objectType);
        return (connectedSystem, objectType, memberAttribute, displayNameAttribute);
    }

    private static ConnectedSystemObject NewCso(ConnectedSystem connectedSystem, ConnectedSystemObjectType objectType) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystem = connectedSystem,
        Type = objectType,
        Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    /// <summary>
    /// Seeds a Metaverse Object with <paramref name="count"/> plain string values of a multi-valued "Manager"
    /// attribute. Returns the object's id.
    /// </summary>
    private async Task<Guid> SeedMvoValuesAsync(int count)
    {
        await using var ctx = NewContext();
        var (type, managerAttribute, _) = BuildMvoSchema(ctx);
        await ctx.SaveChangesAsync();

        var mvo = NewMvo(type);
        ctx.MetaverseObjects.Add(mvo);
        for (var i = 1; i <= count; i++)
            ctx.Add(new MetaverseObjectAttributeValue
            {
                Id = IdFor(i),
                MetaverseObject = mvo,
                Attribute = managerAttribute,
                StringValue = $"value {i:D3}"
            });

        await ctx.SaveChangesAsync();
        return mvo.Id;
    }

    /// <summary>
    /// Seeds a Metaverse Object whose "Manager" values are references to other Metaverse Objects, each carrying
    /// one of <paramref name="referencedDisplayNames"/> as its Display Name. Returns the owning object's id.
    /// </summary>
    private async Task<Guid> SeedMvoReferenceValuesAsync(IReadOnlyList<string> referencedDisplayNames)
    {
        await using var ctx = NewContext();
        var (type, managerAttribute, displayNameAttribute) = BuildMvoSchema(ctx);
        await ctx.SaveChangesAsync();

        var owner = NewMvo(type);
        ctx.MetaverseObjects.Add(owner);

        for (var i = 0; i < referencedDisplayNames.Count; i++)
        {
            var referenced = NewMvo(type);
            ctx.MetaverseObjects.Add(referenced);
            ctx.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = referenced,
                Attribute = displayNameAttribute,
                StringValue = referencedDisplayNames[i]
            });
            ctx.Add(new MetaverseObjectAttributeValue
            {
                Id = IdFor(i + 1),
                MetaverseObject = owner,
                Attribute = managerAttribute,
                ReferenceValue = referenced
            });
        }

        await ctx.SaveChangesAsync();
        return owner.Id;
    }

    private static (MetaverseObjectType Type, MetaverseAttribute ManagerAttribute, MetaverseAttribute DisplayNameAttribute) BuildMvoSchema(JimDbContext ctx)
    {
        var type = new MetaverseObjectType { Name = "User", PluralName = "Users" };
        var managerAttribute = new MetaverseAttribute
        {
            Name = ManagerAttributeName,
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued
        };
        // The Metaverse read's Include filters the referenced object's values down to its Display Name, so the
        // referenced objects must carry the built-in attribute by its exact name for the assertion to see it.
        var displayNameAttribute = new MetaverseAttribute
        {
            Name = Constants.BuiltInAttributes.DisplayName,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        ctx.MetaverseObjectTypes.Add(type);
        ctx.MetaverseAttributes.AddRange(managerAttribute, displayNameAttribute);
        return (type, managerAttribute, displayNameAttribute);
    }

    private static MetaverseObject NewMvo(MetaverseObjectType type) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        Origin = MetaverseObjectOrigin.Projected
    };
}
