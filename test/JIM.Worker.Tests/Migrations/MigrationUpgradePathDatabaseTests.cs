// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace JIM.Worker.Tests.Migrations;

/// <summary>
/// The upgrade-path guard (#1581): proves the migrations at head apply cleanly over the schema the previous
/// release shipped, which is the path every upgrading customer takes and the one production cannot roll back
/// from (the service images carry no EF tooling; the only rollback is a backup restore).
/// <para>
/// Phase one migrates a fresh database to the newest id in <c>released-migrations.lock</c>; because released
/// migrations are frozen (<see cref="ReleasedMigrationImmutabilityTests"/>), that reproduces the last released
/// schema exactly, with no per-release snapshot needed. Phase two opens a fresh context (a new app process, as
/// an upgrade is) and applies the remaining migrations. While the manifest is empty the phases collapse into a
/// single from-scratch application, so the guard runs green today and arms itself at the first release.
/// </para>
/// <para>
/// Runs against its own scratch database, created and dropped per fixture: the shared <c>JIM_TEST_RESET_DB</c>
/// database is already fully migrated by other fixtures, and this test's subject is the journey to that state.
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c>
/// fixtures; the role needs <c>CREATEDB</c> (CI's postgres superuser has it).
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MigrationUpgradePathDatabaseTests
{
    private const string ScratchDatabaseName = "jim_upgrade_path_test";

    private string _adminConnectionString = null!;
    private string _scratchConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL migration upgrade-path tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";

        _adminConnectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";
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
    public async Task HeadMigrations_AppliedOverTheNewestReleasedSchema_SucceedAsync()
    {
        var manifestPath = ReleasedMigrationManifest.GetManifestPath(ReleasedMigrationManifest.FindRepositoryRoot());
        var entries = ReleasedMigrationManifest.Parse(await File.ReadAllLinesAsync(manifestPath));
        var boundary = ReleasedMigrationManifest.NewestReleasedId(entries);

        // Phase one: bring the fresh database to the schema the last release shipped. Dormant until the first
        // release writes the manifest; from then on this is the state every upgrading customer starts from.
        if (boundary != null)
        {
            await using var releasedContext = NewScratchContext();
            await releasedContext.GetService<IMigrator>().MigrateAsync(boundary);

            var appliedAtBoundary = await releasedContext.Database.GetAppliedMigrationsAsync();
            Assert.That(appliedAtBoundary.Last(), Is.EqualTo(boundary),
                "Phase one did not stop at the released boundary; the upgrade being tested is not the customer's.");
        }
        else
        {
            TestContext.Out.WriteLine("released-migrations.lock has no entries yet; phases collapse into a single from-scratch application.");
        }

        // Phase two: a fresh context, as the upgraded app process is, applies everything after the boundary.
        await using (var headContext = NewScratchContext())
        {
            await headContext.Database.MigrateAsync();
        }

        await using var verifyContext = NewScratchContext();
        var applied = (await verifyContext.Database.GetAppliedMigrationsAsync()).ToList();
        var expected = verifyContext.Database.GetMigrations().ToList();
        Assert.That(applied, Is.EqualTo(expected),
            "The scratch database's applied migrations do not match the assembly's; the upgrade path did not complete.");
    }

    private JimDbContext NewScratchContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_scratchConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    private async Task ExecuteAdminSqlAsync(string sql)
    {
        // CREATE/DROP DATABASE cannot be parameterised or run in a transaction; the name is a constant above,
        // never input.
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
