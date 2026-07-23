// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that the bulk COPY path for Metaverse Object change history
/// (PersistPendingMvoChangesAsync) persists every typed value carrier. The COPY column list is
/// hand-maintained and invisible to the in-memory suite, so a carrier missing from it silently
/// writes NULL into the audit record: exactly how DecimalValue was lost when the Decimal type
/// landed, and why LongNumber history (#871) needs pinning at this layer.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MvoChangeValuePersistenceDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL MVO change value persistence tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [Test]
    public async Task PersistPendingMvoChangesAsync_LongNumberAndDecimalValues_PersistAtFullFidelityAsync()
    {
        // Arrange: a persisted MVO and attributes so the change graph's FKs resolve
        const long longValue = 9999999999L;
        const decimal decimalValue = 0.75m;

        await using var seedContext = NewContext();
        var mvoType = new MetaverseObjectType { Name = $"TestType-{Guid.NewGuid():N}", PluralName = "TestTypes" };
        var longAttr = new MetaverseAttribute { Name = $"accountExpires-{Guid.NewGuid():N}", Type = AttributeDataType.LongNumber };
        var decimalAttr = new MetaverseAttribute { Name = $"fte-{Guid.NewGuid():N}", Type = AttributeDataType.Decimal };
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow };
        seedContext.MetaverseAttributes.AddRange(longAttr, decimalAttr);
        seedContext.MetaverseObjects.Add(mvo);
        await seedContext.SaveChangesAsync();

        var change = new MetaverseObjectChange
        {
            MetaverseObject = mvo,
            ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Updated,
            InitiatedByType = ActivityInitiatorType.System
        };
        change.AddAttributeValueChange(
            new MetaverseObjectAttributeValue { Attribute = longAttr, AttributeId = longAttr.Id, LongValue = longValue },
            ValueChangeType.Add);
        change.AddAttributeValueChange(
            new MetaverseObjectAttributeValue { Attribute = decimalAttr, AttributeId = decimalAttr.Id, DecimalValue = decimalValue },
            ValueChangeType.Add);

        // Act: persist through the bulk COPY path the sync engine uses
        await using (var writeContext = NewContext())
        {
            var syncRepository = new SyncRepository(new PostgresDataRepository(writeContext));
            await syncRepository.PersistPendingMvoChangesAsync(
                new List<MetaverseObjectChange> { change },
                new List<MetaverseObjectChange>());
        }

        // Assert: read back with a fresh context; the audit record must carry the exact values
        await using var readContext = NewContext();
        var persisted = await readContext.MetaverseObjectChangeAttributeValues
            .Include(vc => vc.MetaverseObjectChangeAttribute)
            .Where(vc => vc.MetaverseObjectChangeAttribute.MetaverseObjectChange.Id == change.Id)
            .ToListAsync();

        Assert.That(persisted, Has.Count.EqualTo(2));
        var longChange = persisted.Single(vc => vc.MetaverseObjectChangeAttribute.AttributeName == longAttr.Name);
        var decimalChange = persisted.Single(vc => vc.MetaverseObjectChangeAttribute.AttributeName == decimalAttr.Name);
        Assert.That(longChange.LongValue, Is.EqualTo(longValue),
            "The Long Number change value must survive the bulk COPY path at full 64-bit fidelity.");
        Assert.That(decimalChange.DecimalValue, Is.EqualTo(decimalValue),
            "The Decimal change value must survive the bulk COPY path; a missing COPY column writes NULL.");
    }
}
