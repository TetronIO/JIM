// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;
using JimActivity = JIM.Models.Activities.Activity;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// The Connected System Object counterpart of <see cref="MvoChangeValuePersistenceDatabaseTests"/>' subject id round trip
/// (#2669): a change record names its object by plain id, with no foreign key, so a deletion record written after its
/// object has gone keeps the object's id as its subject, on both ways change records are written: the bulk writer
/// synchronisation uses, and the execution item writer housekeeping and withdrawals use. The foreign key once refused
/// both, so deletion records were written with the subject id null.
/// <para>
/// Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class CsoChangeSubjectPersistenceDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL change record subject tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp() => await PostgresTestDatabase.ResetAsync(_connectionString);

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    [TestCase(true)]
    [TestCase(false)]
    public async Task ChangeRecordWriters_DeletionRecordOfAnObjectThatHasGone_PersistItsSubjectIdAsync(bool bulkPath)
    {
        Guid activityId;
        await using (var seed = NewContext())
        {
            var activity = new JimActivity { TargetName = "Housekeeping", InitiatedByType = ActivityInitiatorType.System };
            seed.Activities.Add(activity);
            await seed.SaveChangesAsync();
            activityId = activity.Id;
        }

        var deletedId = Guid.NewGuid();
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = 1,
            ConnectedSystemObjectId = deletedId,
            DeletedConnectedSystemObjectId = deletedId,
            ChangeType = ObjectChangeType.Deleted,
            ChangeTime = DateTime.UtcNow,
            InitiatedByType = ActivityInitiatorType.System,
            DeletedObjectDisplayName = "Tina Adams"
        };
        var item = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(), ActivityId = activityId, ObjectChangeType = ObjectChangeType.Deleted, ConnectedSystemObjectId = deletedId,
            ConnectedSystemObjectChange = change
        };
        change.ActivityRunProfileExecutionItemId = item.Id;

        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            if (bulkPath)
            {
                await repository.Sync.BulkInsertRpeisAsync([item]);
                await repository.Sync.PersistCsoChangesAsync([change]);
            }
            else
            {
                await repository.Activity.CreateActivityRunProfileExecutionItemsAsync([item]);
            }
        }

        await using var readContext = NewContext();
        var persisted = await readContext.ConnectedSystemObjectChanges.AsNoTracking().SingleAsync(c => c.Id == change.Id);
        var persistedItem = await readContext.ActivityRunProfileExecutionItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.ConnectedSystemObjectId, Is.EqualTo(deletedId), "the deletion record names its object by its subject id");
            Assert.That(persisted.DeletedConnectedSystemObjectId, Is.EqualTo(deletedId));
            Assert.That(persistedItem.ConnectedSystemObjectId, Is.EqualTo(deletedId), "and the item that deleted it keeps its id");
        }
    }
}
