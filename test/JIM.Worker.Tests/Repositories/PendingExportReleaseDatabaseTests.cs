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
using System.Data.Common;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL proof of <c>ReleaseExecutingPendingExportsAsync</c> (#2083), the guarded write that puts an interrupted
/// batch back or records its failed attempt. The guard (only rows still Executing are written) can only be proven here:
/// the in-memory store shares instances with its caller, so a result set in memory reads there as already recorded.
/// Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportReleaseDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext(QueryTrackingBehavior tracking = QueryTrackingBehavior.NoTracking, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(tracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        if (interceptor != null)
            builder.AddInterceptors(interceptor);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Pending Export release tests.");

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

    /// <summary>
    /// Seeds one Pending Export per status given, each against a Connected System Object of its own.
    /// </summary>
    private async Task<List<Guid>> SeedPendingExportsAsync(params PendingExportStatus[] statuses)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband LDAP", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        seed.AddRange(connectorDefinition, system, csType);

        var exports = new List<PendingExport>();
        foreach (var status in statuses)
        {
            var cso = new ConnectedSystemObject { Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal };
            var export = new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystem = system,
                ConnectedSystemObject = cso,
                ChangeType = PendingExportChangeType.Update,
                Status = status,
                ErrorCount = 4,
                LastAttemptedAt = TruncateToMicroseconds(DateTime.UtcNow),
                NextRetryAt = TruncateToMicroseconds(DateTime.UtcNow.AddMinutes(30)),
                LastErrorMessage = "written when the batch was marked",
                LastErrorStackTrace = "at Marked()",
                CreatedAt = TruncateToMicroseconds(DateTime.UtcNow.AddHours(-1))
            };
            seed.AddRange(cso, export);
            exports.Add(export);
        }

        await seed.SaveChangesAsync();
        return exports.Select(pe => pe.Id).ToList();
    }

    [Test]
    public async Task ReleaseExecutingPendingExportsAsync_RowsStillExecuting_WritesEveryReleaseColumnAndReturnsThemAsync()
    {
        var ids = await SeedPendingExportsAsync(PendingExportStatus.Executing, PendingExportStatus.Executing);
        var lastAttemptedAt = TruncateToMicroseconds(DateTime.UtcNow.AddMinutes(-10));
        var nextRetryAt = TruncateToMicroseconds(DateTime.UtcNow.AddMinutes(4));

        // One state with every nullable column set, one with every nullable column null, so the write is proven not to
        // confuse "no value" with a default.
        var populated = new PendingExportRetryState(ids[0], PendingExportStatus.Pending, 2, lastAttemptedAt, nextRetryAt, "an earlier failure", "at Earlier()");
        var empty = new PendingExportRetryState(ids[1], PendingExportStatus.ExportNotConfirmed, 0, null, null, null, null);

        IReadOnlySet<Guid> released;
        await using (var context = NewContext())
            released = await new PostgresDataRepository(context).Sync.ReleaseExecutingPendingExportsAsync([populated, empty]);

        await using var read = NewContext();
        var stored = await read.PendingExports.AsNoTracking().Where(pe => ids.Contains(pe.Id)).ToDictionaryAsync(pe => pe.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(released, Is.EquivalentTo(ids));
            Assert.That(PendingExportRetryState.Of(stored[ids[0]]), Is.EqualTo(populated));
            Assert.That(PendingExportRetryState.Of(stored[ids[1]]), Is.EqualTo(empty));
        }
    }

    [Test]
    public async Task ReleaseExecutingPendingExportsAsync_RowAlreadyRecorded_IsLeftAsRecordedAndNotReturnedAsync()
    {
        // A batch that failed after part of its results were persisted: the recorded row has left Executing, and writing
        // the failed attempt over it would put a Create already written back to be sent again.
        var ids = await SeedPendingExportsAsync(PendingExportStatus.Exported, PendingExportStatus.Executing);
        Dictionary<Guid, PendingExport> before;
        await using (var read = NewContext())
            before = await read.PendingExports.AsNoTracking().Where(pe => ids.Contains(pe.Id)).ToDictionaryAsync(pe => pe.Id);

        var states = ids.Select(id => new PendingExportRetryState(id, PendingExportStatus.Pending, 5, null, null, "failed", null)).ToList();

        IReadOnlySet<Guid> released;
        await using (var context = NewContext())
            released = await new PostgresDataRepository(context).Sync.ReleaseExecutingPendingExportsAsync(states);

        await using var after = NewContext();
        var recorded = await after.PendingExports.AsNoTracking().SingleAsync(pe => pe.Id == ids[0]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(released, Is.EquivalentTo(new[] { ids[1] }));
            Assert.That(PendingExportRetryState.Of(recorded), Is.EqualTo(PendingExportRetryState.Of(before[ids[0]])));
        }
    }

    [Test]
    public async Task ReleaseExecutingPendingExportsAsync_OnAContextTrackingTheRows_DetachesThemAsync()
    {
        // The worker's context tracks what it loads for the whole run; a tracked copy still reading Executing would be
        // written back by the next save on that context.
        var ids = await SeedPendingExportsAsync(PendingExportStatus.Executing);
        await using var context = NewContext(QueryTrackingBehavior.TrackAll);
        _ = await context.PendingExports.SingleAsync(pe => pe.Id == ids[0]);

        await new PostgresDataRepository(context).Sync.ReleaseExecutingPendingExportsAsync(
            [new PendingExportRetryState(ids[0], PendingExportStatus.Pending, 0, null, null, null, null)]);

        Assert.That(context.ChangeTracker.Entries<PendingExport>().Any(e => e.Entity.Id == ids[0]), Is.False);
    }

    /// <summary>
    /// The guard reads "still Executing" as "not yet recorded", so recording a batch's results must be all or nothing: a
    /// Pending Export written as Exported while its changes were left unsent would read as recorded, and the confirming
    /// import could never reconcile it.
    /// </summary>
    [Test]
    public async Task UpdatePendingExportsAsync_WhenItsChangesFailToWrite_LeavesThePendingExportAsItWasAsync()
    {
        var ids = await SeedPendingExportsAsync(PendingExportStatus.Executing);
        PendingExport export;
        await using (var read = NewContext())
            export = await read.PendingExports.AsNoTracking().Include(pe => pe.AttributeValueChanges).SingleAsync(pe => pe.Id == ids[0]);
        export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), PendingExportId = export.Id, AttributeId = await SeedAttributeAsync(),
            Status = PendingExportAttributeChangeStatus.Pending, ChangeType = PendingExportAttributeChangeType.Update
        });
        await using (var seed = NewContext())
        {
            seed.Add(export.AttributeValueChanges.Single());
            await seed.SaveChangesAsync();
        }

        export.Status = PendingExportStatus.Exported;
        foreach (var change in export.AttributeValueChanges)
            change.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;

        await using (var failing = NewContext(interceptor: new FailOnCommandInterceptor("UPDATE \"PendingExportAttributeValueChanges\"")))
        {
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await new PostgresDataRepository(failing).ConnectedSystems.UpdatePendingExportsAsync([export]));
        }

        await using var after = NewContext();
        var stored = await after.PendingExports.AsNoTracking().SingleAsync(pe => pe.Id == ids[0]);
        Assert.That(stored.Status, Is.EqualTo(PendingExportStatus.Executing));
    }

    private async Task<int> SeedAttributeAsync()
    {
        await using var seed = NewContext();
        var csType = await seed.ConnectedSystemObjectTypes.AsTracking().FirstAsync();
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        seed.Add(attribute);
        await seed.SaveChangesAsync();
        return attribute.Id;
    }

    /// <summary>
    /// Fails any command whose text contains a given fragment, standing in for the database refusing that one statement.
    /// </summary>
    private sealed class FailOnCommandInterceptor(string commandTextFragment) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            command.CommandText.Contains(commandTextFragment, StringComparison.Ordinal)
                ? throw new InvalidOperationException("The database refused the statement.")
                : base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    private static DateTime TruncateToMicroseconds(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, value.Kind);
}
