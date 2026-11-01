// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL proof that mirroring the SSO configuration into the database at startup leaves no copy of the OIDC
/// client secret in clear (#2684).
/// </summary>
/// <remarks>
/// <c>InitialiseSsoAsync</c> used to copy the secret into the legacy <c>ServiceSettings."SSOSecret"</c> column in plain
/// text, on every start, while the secret JIM actually uses is the encrypted <c>SSO.Secret</c> service setting. The
/// assertion reads the whole row rather than naming a column, so a secret written to any column of it is caught.
/// Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables like the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SsoSecretAtRestDatabaseTests
{
    private const string SsoSecret = "oidc-client-secret-7f3a91";

    private string _connectionString = null!;

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL SSO secret tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    private string? _previousSsoSecret;

    [SetUp]
    public async Task SetUpAsync()
    {
        TestUtilities.SetEnvironmentVariables();

        // where JIM reads the client secret from: seeding stores it, encrypted, as the SSO.Secret service setting
        _previousSsoSecret = Environment.GetEnvironmentVariable(Constants.Config.SsoSecret);
        Environment.SetEnvironmentVariable(Constants.Config.SsoSecret, SsoSecret);

        await PostgresTestDatabase.ResetAsync(_connectionString);

        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        await jim.Seeding.ApplyBuiltInConfigurationAsync();
    }

    [TearDown]
    public void TearDown() => Environment.SetEnvironmentVariable(Constants.Config.SsoSecret, _previousSsoSecret);

    [Test]
    public async Task InitialiseSsoAsync_LeavesNoCopyOfTheSecretInClearAsync()
    {
        await using (var ctx = NewContext())
        {
            using var jim = new JimApplication(new PostgresDataRepository(ctx));
            await jim.InitialiseSsoAsync("https://idp.example.org/realms/jim", "jim-web", "sub",
                Constants.BuiltInAttributes.SubjectIdentifier);
        }

        await using var verify = NewContext();
        var row = await verify.Database
            .SqlQueryRaw<string>("SELECT row_to_json(s)::text AS \"Value\" FROM \"ServiceSettings\" s")
            .SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            // the SSO configuration was mirrored, so the test exercised the write rather than an early return
            Assert.That(row, Does.Contain("jim-web"));
            Assert.That(row, Does.Not.Contain(SsoSecret), "the OIDC client secret is stored in clear");
        }
    }
}
