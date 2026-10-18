// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification that editing a Trusted Certificate from the portal persists, including what it is
/// trusted for (#2363). The portal loads on one NoTracking context and saves on another, and a repository that only
/// copied the fields it knew about would silently drop a new one; the in-memory provider tracks by default and cannot
/// see that.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other real-database fixtures; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent (run them with <c>jim-test-db</c>).
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class TrustedCertificateUpdateDatabaseTests
{
    private string _connectionString = null!;

    // NoTracking, matching JIM.Web.
    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Trusted Certificate update tests.");

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

    [Test]
    public async Task UpdateAsync_CertificateLoadedOnAnotherContext_PersistsEveryEditableFieldAsync()
    {
        var id = await SeedCertificateAsync(CertificateTrustPurpose.Connectors);

        TrustedCertificate detached;
        await using (var loadContext = NewContext())
            detached = await loadContext.TrustedCertificates.SingleAsync(c => c.Id == id);

        detached.Name = "Contoso Issuing CA 01 (renamed)";
        detached.Notes = "Used by AD FS";
        detached.IsEnabled = false;
        detached.TrustedFor = CertificateTrustPurpose.Connectors | CertificateTrustPurpose.SignIn;
        detached.LastUpdated = DateTime.UtcNow;
        detached.LastUpdatedByName = "Jo Bloggs";

        await using (var saveContext = NewContext())
            await new PostgresDataRepository(saveContext).TrustedCertificates.UpdateAsync(detached);

        await using var verify = NewContext();
        var reloaded = await verify.TrustedCertificates.SingleAsync(c => c.Id == id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reloaded.Name, Is.EqualTo("Contoso Issuing CA 01 (renamed)"));
            Assert.That(reloaded.Notes, Is.EqualTo("Used by AD FS"));
            Assert.That(reloaded.IsEnabled, Is.False);
            Assert.That(reloaded.TrustedFor, Is.EqualTo(CertificateTrustPurpose.Connectors | CertificateTrustPurpose.SignIn));
            Assert.That(reloaded.LastUpdatedByName, Is.EqualTo("Jo Bloggs"));
        }
    }

    [Test]
    public async Task Migration_RowWrittenWithoutTrustedFor_DefaultsToConnectorsOnlyAsync()
    {
        // A row as it stood before the column existed: nothing names a purpose, so the column's default decides it.
        var id = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO "TrustedCertificates" ("Id", "Name", "Thumbprint", "Subject", "Issuer", "SerialNumber", "ValidFrom", "ValidTo",
                    "SourceType", "IsEnabled", "Created", "CreatedByType", "LastUpdatedByType")
                VALUES (@id, 'Legacy', 'ABCDEF', 'CN=Legacy', 'CN=Legacy', '01', now(), now() + interval '1 year', 0, true, now(), 0, 0)
                """, connection);
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync();
        }

        await using var verify = NewContext();
        var reloaded = await verify.TrustedCertificates.SingleAsync(c => c.Id == id);
        Assert.That(reloaded.TrustedFor, Is.EqualTo(CertificateTrustPurpose.Connectors));
    }

    private async Task<Guid> SeedCertificateAsync(CertificateTrustPurpose trustedFor)
    {
        using var x509 = TestPki.PublicOnly(TestPki.CreateRoot("Contoso Issuing CA 01"));
        await using var context = NewContext();
        var certificate = new TrustedCertificate
        {
            Id = Guid.NewGuid(),
            Name = "Contoso Issuing CA 01",
            Thumbprint = x509.Thumbprint,
            Subject = x509.Subject,
            Issuer = x509.Issuer,
            SerialNumber = x509.SerialNumber,
            ValidFrom = x509.NotBefore.ToUniversalTime(),
            ValidTo = x509.NotAfter.ToUniversalTime(),
            SourceType = CertificateSourceType.Uploaded,
            CertificateData = x509.RawData,
            TrustedFor = trustedFor
        };
        context.TrustedCertificates.Add(certificate);
        await context.SaveChangesAsync();
        return certificate.Id;
    }
}
