// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using JIM.TestSupport;
using Npgsql;

namespace JIM.Worker.Tests.Migrations;

/// <summary>
/// Proves that a PostgreSQL server prepared exactly as the deployment guide's Before You Install says lets JIM create
/// its schema (#1957). The SQL is read from <c>docs/administration/deployment.md</c> and run as written, as the
/// server's administrator, with only its names changed; then every migration is applied connected as the user it
/// creates. The guide once granted <c>ALL PRIVILEGES ON DATABASE</c>, which since PostgreSQL 15 does not allow
/// creating tables in the database's <c>public</c> schema, so the first migration failed with
/// <c>permission denied for schema public</c> and JIM never started.
/// <para>
/// Nothing else in CI applies migrations as an ordinary user: the bundled database and the <c>database-tests</c>
/// job both migrate as a superuser. This is also what fails if a migration ever needs more than the documented
/// user is given.
/// </para>
/// <para>
/// Runs against its own scratch database and role, created by the documented SQL and dropped per fixture. Opt-in
/// via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c> fixtures; the
/// role needs <c>CREATEDB</c> and <c>CREATEROLE</c> (CI's postgres superuser has both).
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ExternalDatabasePreparationDatabaseTests
{
    // The guide names both the database and its user jim. The test runs the SQL under a name of its own, so that it
    // never touches a jim database or role the server already has (the devcontainer's database runs as jim).
    private const string DocumentedName = "jim";
    private const string ScratchName = "jim_1957_external_test";

    private string _host = null!;
    private string _port = null!;
    private string _adminConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL documented external database test.");

        _host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        _port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";

        _adminConnectionString = $"Host={_host};Port={_port};Database={dbName};Username={user};Password={pass}";

        // Whatever a previous aborted run left behind.
        await DropScratchDatabaseAndRoleAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        if (_adminConnectionString != null)
            await DropScratchDatabaseAndRoleAsync();
    }

    [Test]
    public async Task Migrations_OnAnExternalDatabasePreparedAsDocumented_ApplyAsTheDocumentedUserAsync()
    {
        var documentedSql = ReadDocumentedPreparationSql();
        var password = Regex.Match(documentedSql, @"PASSWORD\s+'([^']*)'", RegexOptions.IgnoreCase);
        Assert.That(password.Success, Is.True,
            $"Before You Install no longer gives the user's password in its SQL, so this test cannot connect as that user:\n{documentedSql}");

        // One statement at a time, as psql sends them: CREATE DATABASE cannot run inside the transaction a
        // multi-statement command is.
        foreach (var statement in SplitStatements(documentedSql))
            await ExecuteAdminSqlAsync(Regex.Replace(statement, $@"\b{DocumentedName}\b", ScratchName));

        Assert.That(await IsSuperuserAsync(), Is.False,
            "The documented SQL makes JIM's user a superuser, so this test no longer proves an ordinary user can migrate JIM's schema.");

        var asDocumentedUser = $"Host={_host};Port={_port};Database={ScratchName};Username={ScratchName};Password={password.Groups[1].Value}";
        await using (var context = NewContext(asDocumentedUser))
        {
            await context.Database.MigrateAsync();
        }

        await using var verifyContext = NewContext(asDocumentedUser);
        var applied = (await verifyContext.Database.GetAppliedMigrationsAsync()).ToList();
        var expected = verifyContext.Database.GetMigrations().ToList();
        Assert.That(applied, Is.EqualTo(expected),
            "Not every migration was applied to the database prepared as documented.");
    }

    /// <summary>The first SQL block in the deployment guide's Before You Install section.</summary>
    private static string ReadDocumentedPreparationSql()
    {
        var page = Path.Join(ReleasedMigrationManifest.FindRepositoryRoot(), "docs", "administration", "deployment.md");
        var lines = File.ReadAllLines(page);

        var heading = Array.FindIndex(lines, line => line.Trim() == "## Before You Install");
        Assert.That(heading, Is.GreaterThanOrEqualTo(0), $"{page} has no Before You Install section.");

        for (var i = heading + 1; i < lines.Length && !Regex.IsMatch(lines[i], @"^#{1,2}\s"); i++)
        {
            var fence = Regex.Match(lines[i], @"^(\s*)```sql\s*$");
            if (!fence.Success)
                continue;

            var indent = fence.Groups[1].Value;
            var block = lines.Skip(i + 1)
                .TakeWhile(line => !Regex.IsMatch(line, @"^\s*```\s*$"))
                .Select(line => line.StartsWith(indent, StringComparison.Ordinal) ? line[indent.Length..] : line.TrimStart());
            return string.Join('\n', block);
        }

        Assert.Fail($"The Before You Install section of {page} has no SQL block.");
        return null!;
    }

    private static IEnumerable<string> SplitStatements(string sql) =>
        sql.Split(';').Select(statement => statement.Trim()).Where(statement => statement.Length > 0);

    private async Task<bool> IsSuperuserAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT rolsuper FROM pg_roles WHERE rolname = @name", connection);
        command.Parameters.AddWithValue("name", ScratchName);
        return (bool)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"The documented SQL created no role (looked for {ScratchName})."));
    }

    private async Task DropScratchDatabaseAndRoleAsync()
    {
        // WITH (FORCE) terminates the connections the migration's pool still holds. The role goes after the
        // database it owns.
        await ExecuteAdminSqlAsync($"DROP DATABASE IF EXISTS {ScratchName} WITH (FORCE)");
        await ExecuteAdminSqlAsync($"DROP ROLE IF EXISTS {ScratchName}");
    }

    // The statements come from the repository's own documentation and constants, never from input. CREATE and DROP
    // DATABASE force a checkpoint, hence the longer timeout.
    private async Task ExecuteAdminSqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = PostgresTestDatabase.DatabaseCreateDropTimeoutSeconds
        };
        await command.ExecuteNonQueryAsync();
    }

    private static JimDbContext NewContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }
}
