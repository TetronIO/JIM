// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using JIM.TestSupport;

namespace JIM.Worker.Tests.Migrations;

/// <summary>
/// Proves the <c>CascadePendingExportAttributeValueChangeDeletes</c> migration's data cleanup (#1818): a
/// <see cref="PendingExportAttributeValueChange"/> row orphaned by the pre-fix
/// <c>ConnectedSystemRepository.DeletePendingExportAsync</c> (its <c>PendingExportId</c> left null when the
/// parent Pending Export was deleted, because the relationship carried no explicit <c>OnDelete</c>
/// configuration and EF's default client-side cascade only nulls tracked children rather than deleting
/// them) must be removed by the migration, while a normal, still-parented row must survive it untouched.
/// <para>
/// Runs against its own scratch database, created and dropped per fixture, migrated in two phases exactly
/// as <see cref="MigrationUpgradePathDatabaseTests"/> does: first to the previous migration (so the schema
/// matches what a customer's database looks like right before upgrading), then to head (so the migration
/// under test actually runs). This migration changes no columns, only the foreign key's referential action
/// plus a data cleanup step, so ordinary EF inserts against the "previous" phase are exactly as valid as
/// they are against any other <c>RequiresPostgres</c> fixture; no raw SQL seeding is needed for schema-shape
/// reasons the way <see cref="ReplaceStrandedValueSweepPendingWithArmedAtMigrationDatabaseTests"/> requires. Opt-in
/// via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c> fixtures.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class CascadePendingExportAttributeValueChangeDeletesMigrationDatabaseTests
{
    private const string ScratchDatabaseName = "jim_1818_migration_test";
    private const string PreviousMigrationId = "20260924071946_AddScheduleFailureHandling";

    private string _adminConnectionString = null!;
    private string _scratchConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL migration backfill tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";

        // The admin connection only issues CREATE/DROP DATABASE, so it uses the server's built-in maintenance
        // database rather than JIM_TEST_RESET_DB, which may not exist yet when this fixture runs first.
        _adminConnectionString = $"Host={host};Port={port};Database=postgres;Username={user};Password={pass}";
        _scratchConnectionString = $"Host={host};Port={port};Database={ScratchDatabaseName};Username={user};Password={pass}";

        // WITH (FORCE) terminates any connection a previous aborted run left behind.
        await ExecuteAdminSqlAsync($"DROP DATABASE IF EXISTS {ScratchDatabaseName} WITH (FORCE)");
        await ExecuteAdminSqlAsync($"CREATE DATABASE {ScratchDatabaseName}");
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        if (_adminConnectionString != null)
            await ExecuteAdminSqlAsync($"DROP DATABASE IF EXISTS {ScratchDatabaseName} WITH (FORCE)");
    }

    [Test]
    public async Task Migration_RemovesOrphanedChildRowsAndLeavesParentedRowsUntouchedAsync()
    {
        // Phase one: bring the scratch database to just before the migration under test, matching the shape
        // of a customer's database prior to upgrading.
        Guid orphanChangeId;
        Guid parentedChangeId;
        Guid keptPendingExportId;
        await using (var priorContext = NewScratchContext())
        {
            await priorContext.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);

            var connectorDefinition = new ConnectorDefinition { Name = "1818-migration-connector" };
            var system = new ConnectedSystem { Name = "1818-migration-system", ConnectorDefinition = connectorDefinition };
            var type = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
            var attribute = new ConnectedSystemObjectTypeAttribute
            {
                Name = "employeeId",
                ConnectedSystemObjectType = type,
                Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued,
                Selected = true
            };
            type.Attributes.Add(attribute);
            var cso = new ConnectedSystemObject
            {
                Type = type,
                ConnectedSystem = system,
                Status = ConnectedSystemObjectStatus.Normal,
                JoinType = ConnectedSystemObjectJoinType.NotJoined
            };
            priorContext.AddRange(connectorDefinition, system, type, cso);
            await priorContext.SaveChangesAsync();

            var keptPendingExport = new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystem = system,
                ConnectedSystemId = system.Id,
                ConnectedSystemObject = cso,
                ConnectedSystemObjectId = cso.Id,
                ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };
            keptPendingExportId = keptPendingExport.Id;
            priorContext.Add(keptPendingExport);
            await priorContext.SaveChangesAsync();

            // A normal, still-parented attribute value change: the migration must leave this alone.
            var parentedChange = new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                PendingExportId = keptPendingExport.Id,
                Attribute = attribute,
                AttributeId = attribute.Id,
                StringValue = "still-parented",
                ChangeType = PendingExportAttributeChangeType.Update
            };
            parentedChangeId = parentedChange.Id;

            // An orphan exactly as the pre-#1818 bug produced: a real row with a null PendingExportId,
            // left behind by deleting a Pending Export via EF Remove()+SaveChangesAsync() under the
            // unconfigured default ClientSetNull cascade.
            var orphanChange = new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                PendingExportId = null,
                Attribute = attribute,
                AttributeId = attribute.Id,
                StringValue = "orphaned-by-1818",
                ChangeType = PendingExportAttributeChangeType.Update
            };
            orphanChangeId = orphanChange.Id;

            priorContext.AddRange(parentedChange, orphanChange);
            await priorContext.SaveChangesAsync();
        }

        // Phase two: a fresh context (a new app process, as an upgrade is) applies the migration under test.
        await using (var headContext = NewScratchContext())
        {
            await headContext.Database.MigrateAsync();
        }

        await using var verifyContext = NewScratchContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verifyContext.PendingExportAttributeValueChanges.AnyAsync(avc => avc.Id == orphanChangeId), Is.False,
                "an attribute value change orphaned by the pre-#1818 bug (null PendingExportId) must be removed by the migration");
            Assert.That(await verifyContext.PendingExportAttributeValueChanges.AnyAsync(avc => avc.Id == parentedChangeId), Is.True,
                "an attribute value change that still belongs to a live Pending Export must survive the migration untouched");
            Assert.That(await verifyContext.PendingExports.AnyAsync(pe => pe.Id == keptPendingExportId), Is.True,
                "the migration's cleanup must not touch Pending Export rows themselves, only orphaned children");
        }
    }

    private JimDbContext NewScratchContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_scratchConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    // CREATE/DROP DATABASE cannot be parameterised or run in a transaction; the name is a constant above, never input.
    private Task ExecuteAdminSqlAsync(string sql) => PostgresTestDatabase.ExecuteDatabaseCreateDropAsync(_adminConnectionString, sql);
}
