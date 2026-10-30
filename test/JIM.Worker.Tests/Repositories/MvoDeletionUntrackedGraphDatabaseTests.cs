// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL coverage of deleting Metaverse Objects on a synchronisation page now that the bulk create leaves
/// the objects it writes untracked (#2598). A deletion candidate reaches the delete detached (its deletion marker was
/// written by the bulk update, which detaches what it writes), and its values can reference an object created on
/// the same page, which can reference it back. Deleting through EF's <c>RemoveRange</c> attached that whole graph,
/// new object included, and then saved the new object's reference to the deleted one, a row the reference cleanup
/// had already removed. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MvoDeletionUntrackedGraphDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Metaverse Object deletion tests.");

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

    [Test]
    public async Task DeleteMetaverseObjectsAsync_DetachedObjectReachingUntrackedNewObjectThatReferencesItBack_DeletesOnlyTheDeletedObjectAsync()
    {
        int typeId, displayNameId, managerId, directReportsId;
        var departingManagerId = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            var type = new MetaverseObjectType { Name = "Person", PluralName = "People" };
            var displayName = new MetaverseAttribute { Name = "displayName", Type = AttributeDataType.Text };
            var manager = new MetaverseAttribute { Name = "manager", Type = AttributeDataType.Reference };
            var directReports = new MetaverseAttribute
            {
                Name = "directReports", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued
            };
            var departingManager = new MetaverseObject { Id = departingManagerId, Type = type, Created = DateTime.UtcNow };
            departingManager.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(), MetaverseObject = departingManager, Attribute = displayName, StringValue = "Departing Manager"
            });
            seed.AddRange(type, displayName, manager, directReports, departingManager);
            await seed.SaveChangesAsync();
            (typeId, displayNameId, managerId, directReportsId) = (type.Id, displayName.Id, manager.Id, directReports.Id);
        }

        // The synchronisation page: one context for the page, change detection off for the flush.
        await using var pageContext = NewContext();
        pageContext.ChangeTracker.AutoDetectChangesEnabled = false;
        var repository = new PostgresDataRepository(pageContext);

        // The deletion candidate was loaded with the page, then detached by the bulk update that wrote its marker.
        var deletionCandidate = await pageContext.MetaverseObjects
            .Include(m => m.AttributeValues)
            .SingleAsync(m => m.Id == departingManagerId);
        foreach (var value in deletionCandidate.AttributeValues)
            pageContext.Entry(value).State = EntityState.Detached;
        pageContext.Entry(deletionCandidate).State = EntityState.Detached;

        // A new object projected on the same page, whose manager is the deletion candidate.
        var type2 = await pageContext.MetaverseObjectTypes.AsNoTracking().SingleAsync(t => t.Id == typeId);
        var newReport = new MetaverseObject { Id = Guid.NewGuid(), Type = type2, Created = DateTime.UtcNow };
        newReport.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), MetaverseObject = newReport, AttributeId = managerId,
            ReferenceValue = deletionCandidate, ReferenceValueId = deletionCandidate.Id
        });
        await repository.Sync.CreateMetaverseObjectsAsync([newReport]);

        // The candidate's own reference to the new object, as the page persisted it.
        var directReportValue = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), MetaverseObject = deletionCandidate, AttributeId = directReportsId,
            ReferenceValue = newReport, ReferenceValueId = newReport.Id
        };
        await pageContext.Database.ExecuteSqlRawAsync(
            @"INSERT INTO ""MetaverseObjectAttributeValues"" (""Id"", ""AttributeId"", ""MetaverseObjectId"", ""ReferenceValueId"", ""NullValue"") VALUES ({0}, {1}, {2}, {3}, false)",
            directReportValue.Id, directReportsId, deletionCandidate.Id, newReport.Id);
        deletionCandidate.AttributeValues.Add(directReportValue);

        Assert.That(async () => await repository.Sync.DeleteMetaverseObjectsAsync([deletionCandidate]), Throws.Nothing);

        await using var verify = NewContext();
        var remaining = await verify.MetaverseObjects.AsNoTracking().Include(m => m.AttributeValues)
            .Where(m => m.Id == departingManagerId || m.Id == newReport.Id).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(remaining.Select(m => m.Id), Is.EquivalentTo(new[] { newReport.Id }),
                "Only the deletion candidate is deleted; the new object stays, written once.");
            Assert.That(remaining.Single().AttributeValues.Where(v => v.ReferenceValueId == departingManagerId), Is.Empty,
                "The new object's reference to the deleted object is cleaned up.");
            Assert.That(await verify.MetaverseObjectAttributeValues.AsNoTracking()
                .CountAsync(v => v.Attribute.Id == displayNameId || v.Attribute.Id == directReportsId), Is.Zero,
                "The deleted object's own values go with it.");
        }
    }

    /// <summary>
    /// When a deletion fails, the per-object fallback records the time so housekeeping retries it. It did so with
    /// EF's <c>Update</c> on the detached object, which walks its graph: through a reference value that reaches an
    /// object created untracked on the same page, which EF then updated as Modified with an unknown xmin (#2598). The
    /// mark is now a one-column raw update.
    /// </summary>
    [Test]
    public async Task SetMetaverseObjectLastConnectorDisconnectedDateAsync_DetachedObjectReachingUntrackedNewObject_SetsOnlyTheDateAsync()
    {
        int typeId, managerId;
        var failedDeletionId = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            var type = new MetaverseObjectType { Name = "Person", PluralName = "People" };
            var manager = new MetaverseAttribute { Name = "manager", Type = AttributeDataType.Reference };
            seed.AddRange(type, manager, new MetaverseObject { Id = failedDeletionId, Type = type, Created = DateTime.UtcNow });
            await seed.SaveChangesAsync();
            (typeId, managerId) = (type.Id, manager.Id);
        }

        await using var pageContext = NewContext();
        pageContext.ChangeTracker.AutoDetectChangesEnabled = false;
        var repository = new PostgresDataRepository(pageContext);

        var failedDeletion = await pageContext.MetaverseObjects.AsNoTracking().SingleAsync(m => m.Id == failedDeletionId);
        var type2 = await pageContext.MetaverseObjectTypes.AsNoTracking().SingleAsync(t => t.Id == typeId);
        var newReport = new MetaverseObject { Id = Guid.NewGuid(), Type = type2, Created = DateTime.UtcNow };
        newReport.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), MetaverseObject = newReport, AttributeId = managerId,
            ReferenceValue = failedDeletion, ReferenceValueId = failedDeletion.Id
        });
        await repository.Sync.CreateMetaverseObjectsAsync([newReport]);
        failedDeletion.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), MetaverseObject = failedDeletion, AttributeId = managerId, ReferenceValue = newReport
        });

        var disconnected = new DateTime(2026, 10, 29, 9, 30, 0, DateTimeKind.Utc);
        Assert.That(async () => await repository.Sync.SetMetaverseObjectLastConnectorDisconnectedDateAsync(failedDeletion.Id, disconnected),
            Throws.Nothing);

        await using var verify = NewContext();
        var stored = await verify.MetaverseObjects.AsNoTracking().Include(m => m.AttributeValues)
            .Where(m => m.Id == failedDeletionId || m.Id == newReport.Id).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Single(m => m.Id == failedDeletionId).LastConnectorDisconnectedDate, Is.EqualTo(disconnected));
            Assert.That(stored.Single(m => m.Id == failedDeletionId).AttributeValues, Is.Empty,
                "Only the date is written: the in-memory value is not saved.");
            Assert.That(stored.Single(m => m.Id == newReport.Id).AttributeValues, Has.Count.EqualTo(1));
            Assert.That(pageContext.ChangeTracker.Entries(), Is.Empty);
        }
    }
}
