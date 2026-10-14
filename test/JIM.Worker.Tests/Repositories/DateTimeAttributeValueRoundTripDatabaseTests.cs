// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL proof of the precision rule (#2153): a date written by any of the bulk writers reads back as
/// <see cref="DateTimeAttributeValue.ToCanonical"/> of what was written, and so compares equal to its source value by
/// <see cref="DateTimeAttributeValue.Key"/>. PostgreSQL keeps a <c>timestamptz</c> to the microsecond, and Npgsql's own
/// conversion truncates toward 2000-01-01, which rounds a pre-2000 value with a sub-microsecond part UP into the next
/// microsecond (into the next year for 1999-12-31T23:59:59.9999995). JIM floors before writing, so the stored value is
/// exact for every year. The inputs are deliberately untruncated: the in-memory repository keeps 100ns ticks, so only
/// this fixture can show the fault. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class DateTimeAttributeValueRoundTripDatabaseTests
{
    private string _connectionString = null!;

    /// <summary>A seventh-digit value, the last instant of 1999, and values near the ends of the range.</summary>
    private static readonly DateTime[] Dates =
    [
        new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc).AddTicks(1234567),
        new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(-5),
        new DateTime(1, 1, 2, 0, 0, 0, DateTimeKind.Utc).AddTicks(5),
        new DateTime(9999, 12, 30, 23, 59, 59, DateTimeKind.Utc).AddTicks(9999995)
    ];

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL date round-trip tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    private sealed record Seeded(int SystemId, int TypeId, int CsDateAttributeId, MetaverseObjectType MvoType, int MvDateAttributeId);

    private async Task<Seeded> SeedGraphAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Yellowstone HR", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var csDate = new ConnectedSystemObjectTypeAttribute
        {
            Name = "leaveDates", ConnectedSystemObjectType = csType, Type = AttributeDataType.DateTime,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        csType.Attributes.Add(csDate);
        var mvoType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var mvDate = new MetaverseAttribute { Name = "Leave Dates", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.MultiValued };

        seed.AddRange(connectorDefinition, system, csType, mvoType, mvDate);
        await seed.SaveChangesAsync();

        return new Seeded(system.Id, csType.Id, csDate.Id, mvoType, mvDate.Id);
    }

    private static void AssertEachReadsBackCanonical(IEnumerable<DateTime?> storedValues)
    {
        var storedKeys = storedValues.Select(v => DateTimeAttributeValue.Key(v!.Value)).OrderBy(k => k).ToList();
        var expectedKeys = Dates.Select(DateTimeAttributeValue.ToCanonical).Select(d => d.Ticks).OrderBy(k => k).ToList();

        Assert.That(storedKeys, Is.EqualTo(expectedKeys), "every date must read back as the microsecond JIM holds, floored, never rounded up");
    }

    [Test]
    public async Task CreateConnectedSystemObjectsAsync_SubMicrosecondDates_ReadBackCanonicalAsync()
    {
        var s = await SeedGraphAsync();
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = s.SystemId, TypeId = s.TypeId,
            Status = ConnectedSystemObjectStatus.Normal, JoinType = ConnectedSystemObjectJoinType.NotJoined, Created = DateTime.UtcNow
        };
        foreach (var date in Dates)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.CsDateAttributeId, DateTimeValue = date });

        await using (var writeContext = NewContext())
            await new PostgresDataRepository(writeContext).Sync.CreateConnectedSystemObjectsAsync([cso]);

        await using var readContext = NewContext();
        var stored = await readContext.ConnectedSystemObjectAttributeValues.AsNoTracking()
            .Where(av => EF.Property<Guid>(av, "ConnectedSystemObjectId") == cso.Id)
            .Select(av => av.DateTimeValue)
            .ToListAsync();

        AssertEachReadsBackCanonical(stored);
    }

    /// <summary>
    /// The import diff compares a re-imported value with the stored one: read back through the raw reader the
    /// synchronisation engine uses, a stored value and its source compare equal.
    /// </summary>
    [Test]
    public async Task StreamUnjoinedConnectedSystemObjectValues_SubMicrosecondDates_CompareEqualToTheirSourceAsync()
    {
        var s = await SeedGraphAsync();
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = s.SystemId, TypeId = s.TypeId,
            Status = ConnectedSystemObjectStatus.Normal, JoinType = ConnectedSystemObjectJoinType.NotJoined, Created = DateTime.UtcNow
        };
        foreach (var date in Dates)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.CsDateAttributeId, DateTimeValue = date });

        await using (var writeContext = NewContext())
            await new PostgresDataRepository(writeContext).Sync.CreateConnectedSystemObjectsAsync([cso]);

        await using var readContext = NewContext();
        var stored = new List<DateTime?>();
        await foreach (var (_, values) in new PostgresDataRepository(readContext).ConnectedSystems
                           .StreamUnjoinedConnectedSystemObjectValues(s.SystemId, s.TypeId, [s.CsDateAttributeId]))
            stored.AddRange(values.Select(v => v.DateTimeValue));

        using (Assert.EnterMultipleScope())
        {
            AssertEachReadsBackCanonical(stored);
            Assert.That(stored.Select(v => v!.Value.Kind), Is.All.EqualTo(DateTimeKind.Utc), "the raw reader hands back canonical values");
            foreach (var date in Dates)
                Assert.That(stored.Any(v => DateTimeAttributeValue.AreEqual(v, date)), Is.True, $"{date:O} must compare equal to its stored self");
        }
    }

    [Test]
    public async Task CreateMetaverseObjectsAsync_SubMicrosecondDates_ReadBackCanonicalAsync()
    {
        var s = await SeedGraphAsync();
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = s.MvoType, Created = DateTime.UtcNow, Status = MetaverseObjectStatus.Normal };
        foreach (var date in Dates)
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = s.MvDateAttributeId, DateTimeValue = date });

        await using (var writeContext = NewContext())
            await new PostgresDataRepository(writeContext).Sync.CreateMetaverseObjectsAsync([mvo]);

        await using var readContext = NewContext();
        var stored = await readContext.MetaverseObjectAttributeValues.AsNoTracking()
            .Where(av => av.MetaverseObject.Id == mvo.Id)
            .Select(av => av.DateTimeValue)
            .ToListAsync();

        AssertEachReadsBackCanonical(stored);
    }

    [Test]
    public async Task CreatePendingExportsAsync_SubMicrosecondDates_ReadBackCanonicalAsync()
    {
        var s = await SeedGraphAsync();
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = s.SystemId,
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        foreach (var date in Dates)
        {
            pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(), AttributeId = s.CsDateAttributeId, DateTimeValue = date,
                ChangeType = PendingExportAttributeChangeType.Add, Status = PendingExportAttributeChangeStatus.Pending
            });
        }

        await using (var writeContext = NewContext())
            await new PostgresDataRepository(writeContext).Sync.CreatePendingExportsAsync([pendingExport]);

        await using var readContext = NewContext();
        var stored = await readContext.PendingExportAttributeValueChanges.AsNoTracking()
            .Where(c => EF.Property<Guid>(c, "PendingExportId") == pendingExport.Id)
            .Select(c => c.DateTimeValue)
            .ToListAsync();

        AssertEachReadsBackCanonical(stored);
    }
}
