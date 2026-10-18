// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.TestSupport;
using Npgsql;

namespace JIM.Worker.Tests.Support;

/// <summary>
/// <see cref="PostgresTestDatabase.CreateDatabaseIfMissing"/> and the per-assembly connection string: every test assembly
/// but the Worker suite runs against a database of its own, created on first use, because the database-tests job runs
/// them all at once and the Worker suite empties its database before each test.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PostgresTestDatabaseCreationTests
{
    private const string Suffix = "_creation_test";
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment(Suffix);
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL database creation tests.");
        _connectionString = connectionString!;
    }

    [SetUp]
    [TearDown]
    public void DropDatabase()
    {
        var admin = new NpgsqlConnectionStringBuilder(_connectionString) { Database = "postgres", Pooling = false };
        using var connection = new NpgsqlConnection(admin.ConnectionString);
        connection.Open();
        using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Database}\" WITH (FORCE)", connection);
        drop.ExecuteNonQuery();
    }

    private string Database => new NpgsqlConnectionStringBuilder(_connectionString).Database!;

    [Test]
    public void ConnectionStringFromEnvironment_WithSuffix_NamesTheSharedDatabaseFollowedByIt()
    {
        Assert.That(Database, Is.EqualTo(Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB") + Suffix));
    }

    [Test]
    public void CreateDatabaseIfMissing_Missing_CreatesItAndAgainChangesNothing()
    {
        PostgresTestDatabase.CreateDatabaseIfMissing(_connectionString);
        PostgresTestDatabase.CreateDatabaseIfMissing(_connectionString);

        using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_connectionString) { Pooling = false }.ConnectionString);
        Assert.That(connection.Open, Throws.Nothing);
    }
}
