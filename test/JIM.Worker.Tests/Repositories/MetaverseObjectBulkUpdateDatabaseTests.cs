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
/// Real-PostgreSQL verification of the synchronisation Metaverse Object update path
/// (<c>SyncRepository.UpdateMetaverseObjectsAsync</c>).
///
/// Reproduces the pre-release Full Regression failure (Scenario14-AttributePriority, activity
/// 019f4501-...): a Metaverse Object bulk-created via the raw COPY path is attached to the change
/// tracker without its real <c>xmin</c> (the concurrency token defaults to 0), so the next EF
/// <c>SaveChangesAsync</c> update of that object in the same flush issues <c>... WHERE xmin = 0</c>,
/// matches 0 rows, and throws <see cref="DbUpdateConcurrencyException"/> - an unhandled failure that
/// aborts the whole sync run. This is exactly the hazard the design note in
/// <c>SyncRepository.MvoOperations.cs</c> predicted. The EF Core in-memory provider does not enforce
/// xmin optimistic concurrency, so this can only be verified against a real database.
///
/// Opt-in via <c>JIM_TEST_RESET_DB</c>; ignored when it is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseObjectBulkUpdateDatabaseTests
{
    private string _connectionString = null!;

    // Tracking is left at the default (TrackAll): the failure depends on the freshly-created MVO
    // remaining tracked with xmin = 0 between the create and the update, which is how the sync engine
    // reuses a single context across a page flush.
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Metaverse Object bulk-update tests.");

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
    /// Seeds a Person Metaverse Object Type with three single-valued text attributes and returns their ids.
    /// </summary>
    private async Task<(int PersonTypeId, int DisplayNameId, int DepartmentId, int JobTitleId)> SeedTypeAsync()
    {
        await using var seed = NewContext();

        var personType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
        var displayName = new MetaverseAttribute { Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        var department = new MetaverseAttribute { Name = "Department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        var jobTitle = new MetaverseAttribute { Name = "Job Title", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        personType.Attributes.Add(displayName);
        personType.Attributes.Add(department);
        personType.Attributes.Add(jobTitle);

        seed.MetaverseObjectTypes.Add(personType);
        await seed.SaveChangesAsync();

        return (personType.Id, displayName.Id, department.Id, jobTitle.Id);
    }

    private static MetaverseObjectAttributeValue TextValue(int attributeId, string value) =>
        new() { AttributeId = attributeId, StringValue = value, NullValue = false };

    /// <summary>
    /// The core regression: after a raw bulk-create, updating the same Metaverse Object in the same
    /// context must persist the change rather than throw <see cref="DbUpdateConcurrencyException"/>.
    /// </summary>
    [Test]
    public async Task UpdateMetaverseObjectsAsync_AfterBulkCreateInSameContext_PersistsChangeAsync()
    {
        var ids = await SeedTypeAsync();

        await using var ctx = NewContext();
        var repo = new PostgresDataRepository(ctx);
        var personType = await ctx.MetaverseObjectTypes.FindAsync(ids.PersonTypeId);

        var mvo = new MetaverseObject
        {
            Type = personType!,
            AttributeValues = { TextValue(ids.DepartmentId, "Sales") }
        };

        // Raw COPY/INSERT create - attaches the MVO to the tracker with xmin defaulted to 0.
        await repo.Sync.CreateMetaverseObjectsAsync(new[] { mvo });

        // Attribute Flow adds a new value; the sync engine now queues the MVO for update.
        mvo.AttributeValues.Add(TextValue(ids.DisplayNameId, "Alice Example"));

        // Must not throw: on the pre-fix EF path this throws DbUpdateConcurrencyException (WHERE xmin = 0).
        await repo.Sync.UpdateMetaverseObjectsAsync(new[] { mvo });

        // Verify the change round-tripped through PostgreSQL.
        await using var verifyCtx = NewContext();
        var persisted = await verifyCtx.MetaverseObjects
            .Include(o => o.AttributeValues)
            .SingleAsync(o => o.Id == mvo.Id);

        Assert.Multiple(() =>
        {
            Assert.That(persisted.AttributeValues, Has.Count.EqualTo(2), "both attribute values should be persisted");
            Assert.That(persisted.AttributeValues.Any(av => av.AttributeId == ids.DepartmentId && av.StringValue == "Sales"), Is.True, "original Department value");
            Assert.That(persisted.AttributeValues.Any(av => av.AttributeId == ids.DisplayNameId && av.StringValue == "Alice Example"), Is.True, "added Display Name value");
        });
    }

    /// <summary>
    /// The update path must apply the full attribute-value delta: insert the added value and delete the
    /// removed one (the sync engine models a change of value as a remove + add), leaving unchanged values intact.
    /// </summary>
    [Test]
    public async Task UpdateMetaverseObjectsAsync_AppliesAttributeValueAddAndRemoveAsync()
    {
        var ids = await SeedTypeAsync();

        await using var ctx = NewContext();
        var repo = new PostgresDataRepository(ctx);
        var personType = await ctx.MetaverseObjectTypes.FindAsync(ids.PersonTypeId);

        var displayNameValue = TextValue(ids.DisplayNameId, "Alice Example");
        var departmentValue = TextValue(ids.DepartmentId, "Sales");
        var mvo = new MetaverseObject
        {
            Type = personType!,
            AttributeValues = { displayNameValue, departmentValue }
        };

        await repo.Sync.CreateMetaverseObjectsAsync(new[] { mvo });

        // Remove Department, add Job Title, leave Display Name unchanged.
        mvo.AttributeValues.Remove(departmentValue);
        mvo.AttributeValues.Add(TextValue(ids.JobTitleId, "Engineer"));

        await repo.Sync.UpdateMetaverseObjectsAsync(new[] { mvo });

        await using var verifyCtx = NewContext();
        var persisted = await verifyCtx.MetaverseObjects
            .Include(o => o.AttributeValues)
            .SingleAsync(o => o.Id == mvo.Id);

        Assert.Multiple(() =>
        {
            Assert.That(persisted.AttributeValues, Has.Count.EqualTo(2), "one value removed, one added, one unchanged");
            Assert.That(persisted.AttributeValues.Any(av => av.AttributeId == ids.DisplayNameId && av.StringValue == "Alice Example"), Is.True, "unchanged Display Name retained");
            Assert.That(persisted.AttributeValues.Any(av => av.AttributeId == ids.JobTitleId && av.StringValue == "Engineer"), Is.True, "added Job Title persisted");
            Assert.That(persisted.AttributeValues.Any(av => av.AttributeId == ids.DepartmentId), Is.False, "removed Department deleted");
        });
    }
}
