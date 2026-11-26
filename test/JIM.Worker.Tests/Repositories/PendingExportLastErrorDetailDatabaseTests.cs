// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
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
/// Real-PostgreSQL proof that a Pending Export's last error, stored as a message beside its English (#3094), persists
/// through both raw update paths: the export-result update (<c>UpdatePendingExportAsync</c>) and the retry update
/// (<c>UpdateUntrackedPendingExportsAsync</c>). Each writer names its columns from <c>PendingExportBulkColumns</c>; this
/// proves the values land in the right columns, and that replacing or clearing the error clears the message with it.
/// The create and release paths are covered by their own round-trip fixtures. Opt-in via <c>JIM_TEST_RESET_*</c>.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportLastErrorDetailDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Pending Export last error message tests.");

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
    /// Seeds one Pending Export and returns a detached copy of it, as the export run hands over the batch it loaded.
    /// </summary>
    private async Task<PendingExport> SeedPendingExportAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband LDAP", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var cso = new ConnectedSystemObject { Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal };
        var export = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = system,
            ConnectedSystemObject = cso,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        seed.AddRange(connectorDefinition, system, csType, cso, export);
        await seed.SaveChangesAsync();

        return new PendingExport
        {
            Id = export.Id,
            ConnectedSystemId = system.Id,
            ConnectedSystemObjectId = cso.Id,
            ChangeType = export.ChangeType,
            Status = export.Status,
            CreatedAt = export.CreatedAt
        };
    }

    private async Task<PendingExport> ReadAsync(Guid id)
    {
        await using var read = NewContext();
        return await read.PendingExports.AsNoTracking().SingleAsync(pe => pe.Id == id);
    }

    [Test]
    public async Task UpdatePendingExportAsync_ErrorStoredAsAMessage_PersistsTheMessageAndItsEnglishAsync()
    {
        var export = await SeedPendingExportAsync();
        PendingExportRetryState.Of(export).AfterFailedAttempt(ActivityMessage.ExportFailedNoReason(), null, DateTime.UtcNow).ApplyTo(export);

        await using (var context = NewContext())
            await new PostgresDataRepository(context).ConnectedSystems.UpdatePendingExportAsync(export);

        var stored = await ReadAsync(export.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.LastErrorMessage, Is.EqualTo("Export failed"));
            Assert.That(stored.LastErrorMessageDetailJson, Is.EqualTo(export.LastErrorMessageDetailJson).And.Not.Null);
            Assert.That(stored.LastErrorMessageDetail?.Key, Is.EqualTo("ExportFailedNoReason"));
        }
    }

    [Test]
    public async Task UpdatePendingExportAsync_ErrorReplacedWithPlainText_ClearsTheStoredMessageAsync()
    {
        var export = await SeedPendingExportAsync();
        PendingExportRetryState.Of(export).AfterFailedAttempt(ActivityMessage.ExportFailedNoReason(), null, DateTime.UtcNow).ApplyTo(export);
        await using (var context = NewContext())
            await new PostgresDataRepository(context).ConnectedSystems.UpdatePendingExportAsync(export);

        PendingExportRetryState.Of(export).AfterFailedAttempt("The directory went away", null, DateTime.UtcNow).ApplyTo(export);
        await using (var context = NewContext())
            await new PostgresDataRepository(context).ConnectedSystems.UpdatePendingExportAsync(export);

        var stored = await ReadAsync(export.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.LastErrorMessage, Is.EqualTo("The directory went away"));
            Assert.That(stored.LastErrorMessageDetailJson, Is.Null);
        }
    }

    [Test]
    public async Task UpdateUntrackedPendingExportsAsync_ErrorStoredAsAMessage_PersistsTheMessageAndItsEnglishAsync()
    {
        var export = await SeedPendingExportAsync();
        PendingExportRetryState.Of(export).AfterFailedAttempt(ActivityMessage.ExportFailedNoReason(), "at Somewhere()", DateTime.UtcNow).ApplyTo(export);

        await using (var context = NewContext())
            await new PostgresDataRepository(context).Sync.UpdateUntrackedPendingExportsAsync([export]);

        var stored = await ReadAsync(export.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.LastErrorMessage, Is.EqualTo("Export failed"));
            Assert.That(stored.LastErrorStackTrace, Is.EqualTo("at Somewhere()"));
            Assert.That(stored.LastErrorMessageDetailJson, Is.EqualTo(export.LastErrorMessageDetailJson).And.Not.Null);
        }
    }

    [Test]
    public async Task UpdateUntrackedPendingExportsAsync_FailureCleared_ClearsTheStoredMessageAsync()
    {
        var export = await SeedPendingExportAsync();
        PendingExportRetryState.Of(export).AfterFailedAttempt(ActivityMessage.ExportFailedNoReason(), null, DateTime.UtcNow).ApplyTo(export);
        await using (var context = NewContext())
            await new PostgresDataRepository(context).Sync.UpdateUntrackedPendingExportsAsync([export]);

        export.ClearFailure();
        await using (var context = NewContext())
            await new PostgresDataRepository(context).Sync.UpdateUntrackedPendingExportsAsync([export]);

        var stored = await ReadAsync(export.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.LastErrorMessage, Is.Null);
            Assert.That(stored.LastErrorMessageDetailJson, Is.Null);
        }
    }
}
