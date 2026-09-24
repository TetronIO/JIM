// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of <c>ISyncRepository.AppendAttributeChangesToPendingExportAsync</c>
/// (the stage-to-execute Update-follows-Create-confirmation fix): appending onto a Create Pending Export
/// that is already sent (Status Exported, awaiting confirmation) must supersede the superseded attribute
/// change, add the new one as Pending, and leave the row's id, ChangeType and Status untouched, on a
/// context that already holds a tracked instance of the same row (pattern:
/// <c>MvoDeletionPendingExportReplaceDatabaseTests</c>). The in-memory provider cannot see a raw-SQL
/// writer's column order, wrong <c>NpgsqlDbType</c>, or a tracked-instance mismatch, so this exercises the
/// real writer against real PostgreSQL.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class AppendAttributeChangesToPendingExportDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL append-attribute-changes tests.");

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

    /// <summary>
    /// Seeds a PendingProvisioning CSO whose Create has already been exported and is awaiting
    /// confirmation, carrying one attribute change still awaiting confirmation (the one the append below
    /// supersedes) and one unrelated attribute change also still awaiting confirmation (which must survive
    /// untouched).
    /// </summary>
    private async Task<(Guid PendingExportId, Guid SupersededChangeId, Guid UnrelatedChangeId, int DisplayNameAttrId)>
        SeedExportedCreateWithAwaitingConfirmationChangesAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Test Target System", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var mailAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "mail", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        csType.Attributes.Add(displayNameAttr);
        csType.Attributes.Add(mailAttr);

        var mvType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        seed.AddRange(connectorDefinition, system, csType, mvType);
        await seed.SaveChangesAsync();

        var mvo = new MetaverseObject { Type = mvType };
        var cso = new ConnectedSystemObject
        {
            Type = csType,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            MetaverseObject = mvo,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            DateJoined = DateTime.UtcNow,
            ExternalIdAttributeId = mailAttr.Id
        };
        seed.Add(mvo);
        seed.Add(cso);
        await seed.SaveChangesAsync();

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            ConnectedSystemObjectId = cso.Id,
            SourceMetaverseObjectId = mvo.Id,
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Exported,
            CreatedAt = DateTime.UtcNow
        };
        var supersededChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = displayNameAttr.Id,
            StringValue = "Old Name",
            ChangeType = PendingExportAttributeChangeType.Update,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation
        };
        var unrelatedChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = mailAttr.Id,
            StringValue = "user@example.com",
            ChangeType = PendingExportAttributeChangeType.Update,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation
        };
        pendingExport.AttributeValueChanges.Add(supersededChange);
        pendingExport.AttributeValueChanges.Add(unrelatedChange);
        seed.Add(pendingExport);
        await seed.SaveChangesAsync();

        return (pendingExport.Id, supersededChange.Id, unrelatedChange.Id, displayNameAttr.Id);
    }

    /// <summary>
    /// Appending onto an already-tracked instance of the same Pending Export (loaded moments earlier by
    /// <c>GetPendingExportLightweightByConnectedSystemObjectIdAsync</c>, exactly as
    /// <c>ExportEvaluationServer</c> does) must not throw <c>DbUpdateConcurrencyException</c> from the
    /// tracker acting on a raw-SQL-deleted row, and must leave the row's identity, ChangeType and Status
    /// untouched.
    /// </summary>
    [Test]
    public async Task AppendAttributeChangesToPendingExportAsync_ContextHoldsTrackedInstance_AppendsWithoutConcurrencyConflictAsync()
    {
        var (pendingExportId, supersededChangeId, unrelatedChangeId, displayNameAttrId) =
            await SeedExportedCreateWithAwaitingConfirmationChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // Mirrors ExportEvaluationServer.CreateOrUpdatePendingExportWithNoNetChangeAsync: the Pending
        // Export is loaded (tracked) ahead of the staging decision, then appended to on the same context.
        var trackedPe = await ctx.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .SingleAsync(pe => pe.Id == pendingExportId);
        Assert.That(trackedPe.AttributeValueChanges, Has.Count.EqualTo(2), "arrange: both seeded changes must be tracked");

        var newChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = displayNameAttrId,
            StringValue = "New Name",
            ChangeType = PendingExportAttributeChangeType.Update,
            Status = PendingExportAttributeChangeStatus.Pending
        };

        await repository.Sync.AppendAttributeChangesToPendingExportAsync(
            pendingExportId, [newChange], [supersededChangeId]);

        // A later SaveChangesAsync on the SAME context (the worker's long-lived context keeps running
        // after this call) must not fail: if the tracked superseded-change instance were left attached,
        // EF's UPDATE against the now-deleted row would affect zero rows and throw
        // DbUpdateConcurrencyException.
        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.Nothing,
            "a later save on the same context must not act on the raw-deleted tracked instance");

        await using var verify = NewContext();
        var persisted = await verify.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .SingleAsync(pe => pe.Id == pendingExportId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.ChangeType, Is.EqualTo(PendingExportChangeType.Create), "ChangeType untouched: never a second Create");
            Assert.That(persisted.Status, Is.EqualTo(PendingExportStatus.Exported), "Status untouched: still awaiting confirmation");
            Assert.That(persisted.AttributeValueChanges.Any(avc => avc.Id == supersededChangeId), Is.False,
                "the superseded change must be gone");
            Assert.That(persisted.AttributeValueChanges.Any(avc => avc.Id == unrelatedChangeId), Is.True,
                "the unrelated still-awaiting-confirmation change must survive untouched");

            var persistedNewChange = persisted.AttributeValueChanges.SingleOrDefault(avc => avc.Id == newChange.Id);
            Assert.That(persistedNewChange, Is.Not.Null, "the new change must be persisted");
            Assert.That(persistedNewChange!.StringValue, Is.EqualTo("New Name"));
            Assert.That(persistedNewChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending));
            Assert.That(persistedNewChange.PendingExportId, Is.EqualTo(pendingExportId));
        }
    }

    /// <summary>
    /// An append with nothing to remove (the auto-confirm-deleted-the-Create shape never reaches this
    /// method; this covers a genuine same-row append with no superseded attribute) must add only the new
    /// change.
    /// </summary>
    [Test]
    public async Task AppendAttributeChangesToPendingExportAsync_NothingToRemove_OnlyAddsTheNewChangeAsync()
    {
        var (pendingExportId, _, unrelatedChangeId, displayNameAttrId) =
            await SeedExportedCreateWithAwaitingConfirmationChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // A second, distinct Add for the same (multi-valued in spirit, but here just re-used) attribute:
        // nothing about this path depends on which attribute the new change targets, only that nothing is
        // removed. Reusing the seeded attribute id avoids needing a further FK-satisfying row.
        var newChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = displayNameAttrId,
            StringValue = "Another Name",
            ChangeType = PendingExportAttributeChangeType.Update,
            Status = PendingExportAttributeChangeStatus.Pending
        };

        await repository.Sync.AppendAttributeChangesToPendingExportAsync(
            pendingExportId, [newChange], []);

        await using var verify = NewContext();
        var persisted = await verify.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .SingleAsync(pe => pe.Id == pendingExportId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.AttributeValueChanges, Has.Count.EqualTo(3), "the two seeded changes plus the new one");
            Assert.That(persisted.AttributeValueChanges.Any(avc => avc.Id == unrelatedChangeId), Is.True);
            Assert.That(persisted.AttributeValueChanges.Any(avc => avc.Id == newChange.Id), Is.True);
        }
    }
}
