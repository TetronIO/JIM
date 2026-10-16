// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification that a deleted Metaverse Object's change history
/// (<see cref="JIM.Application.Servers.MetaverseServer.GetDeletedMvoChangeHistoryAsync"/>) loads the Metaverse
/// Object Type of every Metaverse Object a reference value points at. The Deleted Objects page builds each
/// reference's link (/t/{plural}/v/{id}) and its type chip from that Type (#2146), so a query that stops short of
/// it leaves the link with nothing to build from.
/// </summary>
/// <remarks>
/// The context is <see cref="QueryTrackingBehavior.NoTracking"/>, matching JIM.Web, where a navigation is only
/// ever present if the query Included it. The in-memory provider fixes navigations up regardless, so it cannot see
/// a missing Include; only a real database can. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables
/// as the other database-backed tests; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class DeletedMvoChangeHistoryDatabaseTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL deleted Metaverse Object change history tests.");

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

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    /// <summary>
    /// A live Metaverse Object of a type whose plural name is not the built-in "Users", to be referenced from a
    /// deleted object's history as its manager.
    /// </summary>
    private static MetaverseObject NewManager() => new()
    {
        Id = Guid.NewGuid(),
        Type = new MetaverseObjectType { Name = "Person", PluralName = "People" },
        Created = BaseTime,
        CachedDisplayName = "Ada Lovelace"
    };

    /// <summary>
    /// Adds a "Manager" reference value change pointing at <paramref name="manager"/> to <paramref name="change"/>.
    /// </summary>
    private static void AddManagerChange(MetaverseObjectChange change, MetaverseObject manager, ValueChangeType valueChangeType)
    {
        var attributeChange = new MetaverseObjectChangeAttribute
        {
            AttributeName = "Manager",
            AttributeType = AttributeDataType.Reference,
            MetaverseObjectChange = change
        };
        attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(attributeChange, valueChangeType, manager));
        change.AttributeChanges.Add(attributeChange);
    }

    /// <summary>
    /// The usual shape: the deletion record carries the deleted object's id, so the read takes the main branch
    /// and returns the object's earlier changes alongside it.
    /// </summary>
    [Test]
    public async Task GetDeletedMvoChangeHistoryAsync_ReferenceValueChange_LoadsTheReferencedObjectsTypeAsync()
    {
        // Arrange: no Metaverse Object row for the deleted object, exactly as after a real deletion.
        var deletedMvoId = Guid.NewGuid();
        var manager = NewManager();
        var update = new MetaverseObjectChange
        {
            Id = Guid.NewGuid(),
            ChangeType = ObjectChangeType.Updated,
            ChangeTime = BaseTime,
            DeletedMetaverseObjectId = deletedMvoId
        };
        AddManagerChange(update, manager, ValueChangeType.Add);
        var deletion = new MetaverseObjectChange
        {
            Id = Guid.NewGuid(),
            ChangeType = ObjectChangeType.Deleted,
            ChangeTime = BaseTime.AddMinutes(1),
            DeletedMetaverseObjectId = deletedMvoId,
            DeletedObjectDisplayName = "Jane Smith"
        };
        await using (var seed = NewContext())
        {
            seed.AddRange(update, deletion);
            await seed.SaveChangesAsync();
        }

        // Act
        using var jim = NewJim();
        var history = await jim.Metaverse.GetDeletedMvoChangeHistoryAsync(deletion.Id);

        // Assert
        var referenceValue = history
            .SelectMany(c => c.AttributeChanges)
            .SelectMany(ac => ac.ValueChanges)
            .Single(vc => vc.ReferenceValue != null)
            .ReferenceValue!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(referenceValue.Id, Is.EqualTo(manager.Id));
            Assert.That(referenceValue.Type, Is.Not.Null,
                "the referenced Metaverse Object's Type must be loaded: the Deleted Objects page builds its link from it (#2146)");
        }
        Assert.That(referenceValue.Type!.PluralName, Is.EqualTo("People"));
    }

    /// <summary>
    /// The fallback branch, which returns the deletion record on its own when the deleted object's id cannot be
    /// resolved from it. It needs the same Include as the main branch.
    /// </summary>
    [Test]
    public async Task GetDeletedMvoChangeHistoryAsync_DeletionRecordWithNoObjectId_StillLoadsTheReferencedObjectsTypeAsync()
    {
        // Arrange. The seed is what routes the read to the fallback: the deletion record has no
        // DeletedMetaverseObjectId and no MetaverseObject, and on a NoTracking context the shadow foreign key probe
        // finds nothing either, so no object id resolves. (A count of returned changes would not prove it: the main
        // branch also returns just this record when it is the only one.)
        var manager = NewManager();
        var deletion = new MetaverseObjectChange
        {
            Id = Guid.NewGuid(),
            ChangeType = ObjectChangeType.Deleted,
            ChangeTime = BaseTime,
            DeletedMetaverseObjectId = null,
            DeletedObjectDisplayName = "Jane Smith"
        };
        AddManagerChange(deletion, manager, ValueChangeType.Remove);
        await using (var seed = NewContext())
        {
            seed.Add(deletion);
            await seed.SaveChangesAsync();
        }

        // Act
        using var jim = NewJim();
        var history = await jim.Metaverse.GetDeletedMvoChangeHistoryAsync(deletion.Id);

        // Assert
        var referenceValue = history
            .SelectMany(c => c.AttributeChanges)
            .SelectMany(ac => ac.ValueChanges)
            .Single(vc => vc.ReferenceValue != null)
            .ReferenceValue!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(referenceValue.Id, Is.EqualTo(manager.Id));
            Assert.That(referenceValue.Type, Is.Not.Null,
                "the referenced Metaverse Object's Type must be loaded on the fallback branch too (#2146)");
        }
        Assert.That(referenceValue.Type!.PluralName, Is.EqualTo("People"));
    }
}
