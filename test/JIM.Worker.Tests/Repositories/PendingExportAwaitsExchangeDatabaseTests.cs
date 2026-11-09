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
/// Real-PostgreSQL coverage for the Exchange hold (#2021): the <c>AwaitsExchange</c> flag on a Pending Export attribute
/// change through every raw-SQL path that writes it (staging, the export result, the confirming import), and the two
/// lookups the hold and its release rely on: which objects have a change on the Exchange queue, and which hold a change
/// awaiting Exchange with no queued change left to wait for. The in-memory provider cannot see a writer that leaves the
/// column out or writes it in the wrong position, nor a query against the wrong table.
/// Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent. <c>SetUp</c> TRUNCATEs every table.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportAwaitsExchangeDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Exchange hold tests.");

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
    public async Task CreatePendingExportsAsync_ChangesWithAndWithoutTheFlag_RoundTripTheFlagAsync()
    {
        var s = await SeedAsync();
        var pendingExport = NewPendingExport(s.SystemId, s.CsoIds[0], s.AttributeId);
        pendingExport.AttributeValueChanges[0].AwaitsExchange = true;
        pendingExport.AttributeValueChanges.Add(NewChange(s.AttributeId));

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.CreatePendingExportsAsync([pendingExport]);

        var stored = await ReadFlagsAsync(pendingExport.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored[pendingExport.AttributeValueChanges[0].Id], Is.EqualTo((true, false)));
            Assert.That(stored[pendingExport.AttributeValueChanges[1].Id], Is.EqualTo((false, false)));
        }
    }

    /// <summary>
    /// Export staging marks the flag on the changes it hands to the Exchange queue, and the export run persists it
    /// through the export-result bulk update, on a context that already tracks the rows (as the worker's does). The
    /// Initial Password flag beside it is written from its own value, not from this one.
    /// </summary>
    [Test]
    public async Task UpdatePendingExportsAsync_FlagSetAfterTheExport_PersistsItAsync()
    {
        var s = await SeedAsync();
        var pendingExport = NewPendingExport(s.SystemId, s.CsoIds[0], s.AttributeId);
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.CreatePendingExportsAsync([pendingExport]);

        await using (var update = NewContext())
        {
            var tracked = await update.PendingExports.Include(pe => pe.AttributeValueChanges).SingleAsync(pe => pe.Id == pendingExport.Id);
            var change = tracked.AttributeValueChanges.Single();
            change.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
            change.ExportAttemptCount = 1;
            change.AwaitsExchange = true;
            tracked.Status = PendingExportStatus.Exported;
            await new PostgresDataRepository(update).Sync.UpdatePendingExportsAsync([tracked]);
        }

        var stored = await ReadFlagsAsync(pendingExport.Id);
        Assert.That(stored.Values.Single(), Is.EqualTo((true, false)));
    }

    /// <summary>
    /// The confirming import clears the flag on a release through its own bulk update.
    /// </summary>
    [Test]
    public async Task UpdateUntrackedPendingExportsAsync_FlagClearedOnRelease_PersistsItAsync()
    {
        var s = await SeedAsync();
        var pendingExport = NewPendingExport(s.SystemId, s.CsoIds[0], s.AttributeId);
        pendingExport.Status = PendingExportStatus.Exported;
        pendingExport.AttributeValueChanges[0] = HeldChange(s.AttributeId);
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.CreatePendingExportsAsync([pendingExport]);
        Assert.That((await ReadFlagsAsync(pendingExport.Id)).Values.Single().AwaitsExchange, Is.True, "precondition: the change is stored held");

        await using (var read = NewContext())
        {
            var untracked = await read.PendingExports.AsNoTracking().Include(pe => pe.AttributeValueChanges).SingleAsync(pe => pe.Id == pendingExport.Id);
            untracked.AttributeValueChanges.Single().Status = PendingExportAttributeChangeStatus.ExportedNotConfirmed;
            untracked.AttributeValueChanges.Single().AwaitsExchange = false;
            untracked.Status = PendingExportStatus.ExportNotConfirmed;
            await new PostgresDataRepository(read).Sync.UpdateUntrackedPendingExportsAsync([untracked]);
        }

        await using var verify = NewContext();
        var change = await verify.PendingExportAttributeValueChanges.AsNoTracking().SingleAsync(c => c.PendingExportId == pendingExport.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.AwaitsExchange, Is.False);
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed));
        }
    }

    /// <summary>
    /// Every status counts: a change on its way, one waiting for a person, and one held by a cancellation all mean the
    /// hold has something to wait for. An object with no queued change, or one not asked about, is not returned, nor is a
    /// queued change on another Connected System.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectIdsWithExchangeChangeAsync_QueuedChangesInEveryStatus_ReturnsTheAskedObjectsThatHaveOneAsync()
    {
        var s = await SeedAsync(connectedSystemObjectCount: 6);
        var other = await SeedAsync(systemName: "Other Directory");
        var statuses = Enum.GetValues<PendingExchangeChangeStatus>();
        await using (var write = NewContext())
        {
            var sync = new PostgresDataRepository(write).Sync;
            await sync.StageExchangeChangesAsync(statuses.Select((status, i) => Queued(s.SystemId, s.CsoIds[i], status)).ToList());
            await sync.StageExchangeChangesAsync([Queued(other.SystemId, other.CsoIds[0], PendingExchangeChangeStatus.Pending)]);
        }

        await using var read = NewContext();
        var asked = s.CsoIds.Concat([other.CsoIds[0]]).ToList();
        asked.Remove(s.CsoIds[0]);
        var found = await new PostgresDataRepository(read).Sync.GetConnectedSystemObjectIdsWithExchangeChangeAsync(s.SystemId, asked);

        Assert.That(found, Is.EquivalentTo(s.CsoIds.Skip(1).Take(statuses.Length - 1)),
            "every status but the one not asked about, nothing with no queued change, and nothing on another Connected System");
    }

    [Test]
    public async Task GetConnectedSystemObjectIdsWithExchangeChangeAsync_NothingAsked_ReturnsNothingAsync()
    {
        var s = await SeedAsync();
        await using var read = NewContext();

        var found = await new PostgresDataRepository(read).Sync.GetConnectedSystemObjectIdsWithExchangeChangeAsync(s.SystemId, []);

        Assert.That(found, Is.Empty);
    }

    /// <summary>
    /// The import's sweep of holds with nothing left to wait for: a change awaiting Exchange whose object has no queued
    /// change. Not one whose object still has a queued change, not a flagged change that has left
    /// ExportedPendingConfirmation, not an unflagged change, and nothing on another Connected System.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectIdsWithReleasedExchangeHoldsAsync_MixedObjects_ReturnsOnlyHoldsWithNoQueuedChangeAsync()
    {
        var s = await SeedAsync(connectedSystemObjectCount: 4);
        var other = await SeedAsync(systemName: "Other Directory");
        var stillQueued = WithHeldChange(NewPendingExport(s.SystemId, s.CsoIds[0], s.AttributeId), s.AttributeId);
        var nothingLeftToWaitFor = WithHeldChange(NewPendingExport(s.SystemId, s.CsoIds[1], s.AttributeId), s.AttributeId);
        var noLongerAwaitingConfirmation = NewPendingExport(s.SystemId, s.CsoIds[2], s.AttributeId);
        noLongerAwaitingConfirmation.AttributeValueChanges[0].AwaitsExchange = true;
        noLongerAwaitingConfirmation.AttributeValueChanges[0].Status = PendingExportAttributeChangeStatus.ExportedNotConfirmed;
        var unflagged = NewPendingExport(s.SystemId, s.CsoIds[3], s.AttributeId);
        var onAnotherSystem = WithHeldChange(NewPendingExport(other.SystemId, other.CsoIds[0], other.AttributeId), other.AttributeId);
        await using (var write = NewContext())
        {
            var sync = new PostgresDataRepository(write).Sync;
            await sync.CreatePendingExportsAsync([stillQueued, nothingLeftToWaitFor, noLongerAwaitingConfirmation, unflagged, onAnotherSystem]);
            await sync.StageExchangeChangesAsync([Queued(s.SystemId, s.CsoIds[0], PendingExchangeChangeStatus.Parked)]);
        }

        await using var read = NewContext();
        var released = await new PostgresDataRepository(read).Sync.GetConnectedSystemObjectIdsWithReleasedExchangeHoldsAsync(s.SystemId);

        Assert.That(released, Is.EquivalentTo(new[] { s.CsoIds[1] }));
    }

    /// <summary>
    /// The complement, read before an import's Connector reads anything: only holds whose object still has a change on the
    /// Exchange queue, in any status; not a hold with nothing left to wait for, a change no longer awaiting confirmation,
    /// an unflagged change, or a hold on another Connected System.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectIdsWithQueuedExchangeHoldsAsync_MixedObjects_ReturnsOnlyHoldsWithAQueuedChangeAsync()
    {
        var s = await SeedAsync(connectedSystemObjectCount: 4);
        var other = await SeedAsync(systemName: "Other Directory");
        var stillQueued = WithHeldChange(NewPendingExport(s.SystemId, s.CsoIds[0], s.AttributeId), s.AttributeId);
        var nothingLeftToWaitFor = WithHeldChange(NewPendingExport(s.SystemId, s.CsoIds[1], s.AttributeId), s.AttributeId);
        var noLongerAwaitingConfirmation = NewPendingExport(s.SystemId, s.CsoIds[2], s.AttributeId);
        noLongerAwaitingConfirmation.AttributeValueChanges[0].AwaitsExchange = true;
        noLongerAwaitingConfirmation.AttributeValueChanges[0].Status = PendingExportAttributeChangeStatus.ExportedNotConfirmed;
        var unflagged = NewPendingExport(s.SystemId, s.CsoIds[3], s.AttributeId);
        var onAnotherSystem = WithHeldChange(NewPendingExport(other.SystemId, other.CsoIds[0], other.AttributeId), other.AttributeId);
        await using (var write = NewContext())
        {
            var sync = new PostgresDataRepository(write).Sync;
            await sync.CreatePendingExportsAsync([stillQueued, nothingLeftToWaitFor, noLongerAwaitingConfirmation, unflagged, onAnotherSystem]);
            await sync.StageExchangeChangesAsync([
                Queued(s.SystemId, s.CsoIds[0], PendingExchangeChangeStatus.Parked),
                Queued(s.SystemId, s.CsoIds[2], PendingExchangeChangeStatus.Pending),
                Queued(s.SystemId, s.CsoIds[3], PendingExchangeChangeStatus.Pending),
                Queued(other.SystemId, other.CsoIds[0], PendingExchangeChangeStatus.Pending)]);
        }

        await using var read = NewContext();
        var held = await new PostgresDataRepository(read).Sync.GetConnectedSystemObjectIdsWithQueuedExchangeHoldsAsync(s.SystemId);

        Assert.That(held, Is.EquivalentTo(new[] { s.CsoIds[0] }));
    }

    /// <summary>
    /// Deleting the object sets its queued change's account to null rather than deleting the row (the lane withdraws
    /// it). That must not strand a hold elsewhere, and the row, naming no account, holds nothing.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectIdsWithExchangeChangeAsync_QueuedChangeWhoseObjectWasDeleted_HoldsNothingAsync()
    {
        var s = await SeedAsync(connectedSystemObjectCount: 2);
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.StageExchangeChangesAsync([Queued(s.SystemId, s.CsoIds[0], PendingExchangeChangeStatus.Pending)]);
        await using (var delete = NewContext())
            await delete.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "ConnectedSystemObjects" WHERE "Id" = {s.CsoIds[0]}""");

        await using var read = NewContext();
        var found = await new PostgresDataRepository(read).Sync.GetConnectedSystemObjectIdsWithExchangeChangeAsync(s.SystemId, s.CsoIds);

        Assert.That(found, Is.Empty);
    }

    /// <summary>
    /// Drift Detection's prefetch: only the asked-for objects whose Pending Export holds a change awaiting Exchange
    /// still awaiting its confirming import; not a change awaiting an Initial Password, not one that has left that
    /// state, and not one on an object not asked about.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectIdsAwaitingExchangeAsync_MixedObjects_ReturnsOnlyThoseWithAHeldChangeAsync()
    {
        var s = await SeedAsync(connectedSystemObjectCount: 5);
        var held = WithHeldChange(NewPendingExport(s.SystemId, s.CsoIds[0], s.AttributeId), s.AttributeId);
        var awaitingAPassword = NewPendingExport(s.SystemId, s.CsoIds[1], s.AttributeId);
        awaitingAPassword.AttributeValueChanges[0].AwaitsInitialPassword = true;
        awaitingAPassword.AttributeValueChanges[0].Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var noLongerAwaitingConfirmation = NewPendingExport(s.SystemId, s.CsoIds[2], s.AttributeId);
        noLongerAwaitingConfirmation.AttributeValueChanges[0].AwaitsExchange = true;
        noLongerAwaitingConfirmation.AttributeValueChanges[0].Status = PendingExportAttributeChangeStatus.ExportedNotConfirmed;
        var heldButNotAsked = WithHeldChange(NewPendingExport(s.SystemId, s.CsoIds[4], s.AttributeId), s.AttributeId);
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.CreatePendingExportsAsync([held, awaitingAPassword, noLongerAwaitingConfirmation, heldButNotAsked]);

        await using var read = NewContext();
        var found = await new PostgresDataRepository(read).Sync.GetConnectedSystemObjectIdsAwaitingExchangeAsync(
            s.SystemId, [s.CsoIds[0], s.CsoIds[1], s.CsoIds[2], s.CsoIds[3]]);

        Assert.That(found, Is.EquivalentTo(new[] { s.CsoIds[0] }));
    }

    private static PendingExchangeChange Queued(int systemId, Guid csoId, PendingExchangeChangeStatus status) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = systemId,
        ConnectedSystemObjectId = csoId,
        RecipientType = "Mailbox",
        Status = status,
        FailureMessage = status == PendingExchangeChangeStatus.Pending ? null : "held",
        CreatedAt = DateTime.UtcNow,
        ActivityId = Guid.NewGuid()
    };

    private static PendingExport WithHeldChange(PendingExport pendingExport, int attributeId)
    {
        pendingExport.Status = PendingExportStatus.Exported;
        pendingExport.AttributeValueChanges[0] = HeldChange(attributeId);
        return pendingExport;
    }

    private static PendingExportAttributeValueChange HeldChange(int attributeId)
    {
        var change = NewChange(attributeId);
        change.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        change.ExportAttemptCount = 1;
        change.AwaitsExchange = true;
        return change;
    }

    private async Task<Dictionary<Guid, (bool AwaitsExchange, bool AwaitsInitialPassword)>> ReadFlagsAsync(Guid pendingExportId)
    {
        await using var verify = NewContext();
        return await verify.PendingExportAttributeValueChanges.AsNoTracking()
            .Where(c => c.PendingExportId == pendingExportId)
            .ToDictionaryAsync(c => c.Id, c => (c.AwaitsExchange, c.AwaitsInitialPassword));
    }

    private static PendingExport NewPendingExport(int systemId, Guid csoId, int attributeId) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = systemId,
        ConnectedSystemObjectId = csoId,
        ChangeType = PendingExportChangeType.Update,
        Status = PendingExportStatus.Pending,
        CreatedAt = DateTime.UtcNow,
        AttributeValueChanges = [NewChange(attributeId)]
    };

    private static PendingExportAttributeValueChange NewChange(int attributeId) => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = attributeId,
        ChangeType = PendingExportAttributeChangeType.Update,
        StringValue = "Mailbox",
        Status = PendingExportAttributeChangeStatus.Pending
    };

    private sealed record Seeded(int SystemId, int AttributeId, List<Guid> CsoIds);

    private async Task<Seeded> SeedAsync(int connectedSystemObjectCount = 1, string systemName = "Panoply AD")
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = $"{systemName} Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = systemName, ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "Exchange Recipient Type", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsConnectorDefined = true
        };
        csType.Attributes.Add(attribute);
        var csos = Enumerable.Range(0, connectedSystemObjectCount)
            .Select(_ => new ConnectedSystemObject { Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal })
            .ToList();

        seed.AddRange(connectorDefinition, system, csType);
        seed.AddRange(csos);
        await seed.SaveChangesAsync();

        return new Seeded(system.Id, attribute.Id, csos.Select(c => c.Id).ToList());
    }
}
