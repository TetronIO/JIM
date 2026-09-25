// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of <see cref="PostgresDataRepository.TryConnectAsync"/>, the check every service's
/// start-up wait repeats. What it must tell apart only a real server can show: a missing database (connected, because
/// JIM.Worker's migration creates it) from rejected credentials (thrown, because waiting cannot fix them). Opt-in via
/// JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class RepositoryTryConnectDatabaseTests
{
    private NpgsqlConnectionStringBuilder _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL connection check tests.");

        _connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost",
            Port = int.Parse(Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432"),
            Database = dbName,
            Username = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres",
            Password = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres"
        };
    }

    private static PostgresDataRepository NewRepository(NpgsqlConnectionStringBuilder connectionString)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>().UseNpgsql(connectionString.ConnectionString).Options;
        return new PostgresDataRepository(new JimDbContext(options));
    }

    [Test]
    public async Task TryConnectAsync_ServerUp_IsConnectedAsync()
    {
        using var repository = NewRepository(_connectionString);

        var result = await repository.TryConnectAsync(CancellationToken.None);

        Assert.That(result.IsConnected, Is.True);
    }

    [Test]
    public async Task TryConnectAsync_ServerUpButDatabaseMissing_IsConnectedAsync()
    {
        // A new external server with no JIM database yet: JIM.Worker's migration creates it, so the wait must not
        // hold JIM.Worker back from doing so.
        var missing = new NpgsqlConnectionStringBuilder(_connectionString.ConnectionString) { Database = $"jim_missing_{Guid.NewGuid():N}" };
        using var repository = NewRepository(missing);

        var result = await repository.TryConnectAsync(CancellationToken.None);

        Assert.That(result.IsConnected, Is.True);
    }

    [Test]
    public void TryConnectAsync_WrongPassword_Throws()
    {
        // Rejected credentials will not start working however long the service waits; the service must stop with
        // the real error instead.
        var wrong = new NpgsqlConnectionStringBuilder(_connectionString.ConnectionString) { Password = "not-the-password" };
        using var repository = NewRepository(wrong);

        var ex = Assert.ThrowsAsync<PostgresException>(() => repository.TryConnectAsync(CancellationToken.None));

        Assert.That(ex!.SqlState, Is.EqualTo(PostgresErrorCodes.InvalidPassword));
    }
}
