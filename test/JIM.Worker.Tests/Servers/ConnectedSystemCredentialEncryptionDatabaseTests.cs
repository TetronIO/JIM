// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Services;
using JIM.Connectors;
using JIM.Connectors.SCIM;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL proof that a Connected System credential reaches the database encrypted (#2680): saved the way the
/// portal saves it, and converged at startup when an earlier build stored it in clear.
/// </summary>
/// <remarks>
/// The portal loads a Connected System in one scope and saves it, detached, in another, on a no-tracking context; the
/// fixture does the same. Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables like the other RequiresPostgres
/// fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemCredentialEncryptionDatabaseTests
{
    private const string PlainPassword = "Portal-Typed-Secret-1!";

    private string _connectionString = null!;
    private ApiKey _key = null!;

    // One provider for the whole fixture, so every context's JimApplication shares its key ring, as JIM.Web and
    // JIM.Worker share theirs.
    private readonly CredentialProtectionService _credentialProtection = new(new EphemeralDataProtectionProvider());

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL credential encryption tests.");

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
        await WithJimAsync(jim => jim.Seeding.ApplyBuiltInConfigurationAsync());

        await using var ctx = NewContext();
        _key = new ApiKey { Id = Guid.NewGuid(), Name = "Administrator", KeyHash = "hash", KeyPrefix = "jim_ak_test" };
        ctx.ApiKeys.Add(_key);
        await ctx.SaveChangesAsync();
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_DetachedSaveWithPlainCredential_StoresCiphertextAsync()
    {
        var connectedSystemId = await CreateScimConnectedSystemAsync();

        // Load in one scope and let it go, then type the password into the encrypted field, as the portal does.
        var detached = (await WithJimAsync(jim => jim.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId)))!;
        PasswordOf(detached).StringEncryptedValue = PlainPassword;

        await WithJimAsync(jim => jim.ConnectedSystems.UpdateConnectedSystemAsync(detached, _key));

        var stored = await StoredPasswordAsync(connectedSystemId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored, Is.Not.EqualTo(PlainPassword), "the credential was stored in clear");
            Assert.That(_credentialProtection.IsProtected(stored), Is.True);
            Assert.That(_credentialProtection.Unprotect(stored), Is.EqualTo(PlainPassword));
        }
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_UntouchedCredential_KeepsTheStoredCiphertextAsync()
    {
        var connectedSystemId = await CreateScimConnectedSystemAsync();
        var first = (await WithJimAsync(jim => jim.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId)))!;
        PasswordOf(first).StringEncryptedValue = PlainPassword;
        await WithJimAsync(jim => jim.ConnectedSystems.UpdateConnectedSystemAsync(first, _key));
        var storedAfterFirstSave = await StoredPasswordAsync(connectedSystemId);

        // A later save of some other setting posts the password field back as it was loaded: the ciphertext.
        var second = (await WithJimAsync(jim => jim.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId)))!;
        second.SettingValues.Single(sv => sv.Setting.Name == ScimConnectorConstants.SettingUsername).StringValue = "svc-jim-2";
        await WithJimAsync(jim => jim.ConnectedSystems.UpdateConnectedSystemAsync(second, _key));

        Assert.That(await StoredPasswordAsync(connectedSystemId), Is.EqualTo(storedAfterFirstSave));
    }

    [Test]
    public async Task ProtectStoredCredentialsAsync_CredentialStoredInClear_EncryptsItAsync()
    {
        var connectedSystemId = await CreateScimConnectedSystemAsync();

        // What a build before #2680 left behind: the portal's plain value, written straight to the column.
        await using (var ctx = NewContext())
        {
            var passwordValueId = await PasswordValueIdAsync(ctx, connectedSystemId);
            await ctx.ConnectedSystemSettingValues
                .Where(sv => sv.Id == passwordValueId)
                .ExecuteUpdateAsync(s => s.SetProperty(sv => sv.StringEncryptedValue, PlainPassword));
        }

        var converged = await WithJimAsync(jim => jim.ConnectedSystems.ProtectStoredCredentialsAsync());

        var stored = await StoredPasswordAsync(connectedSystemId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(converged, Is.EqualTo(1));
            Assert.That(_credentialProtection.IsProtected(stored), Is.True);
            Assert.That(_credentialProtection.Unprotect(stored), Is.EqualTo(PlainPassword));
        }

        // and a second start finds nothing left to do
        Assert.That(await WithJimAsync(jim => jim.ConnectedSystems.ProtectStoredCredentialsAsync()), Is.Zero);
    }

    private async Task<int> CreateScimConnectedSystemAsync()
    {
        var definition = await WithJimAsync(jim => jim.ConnectedSystems.GetConnectorDefinitionAsync(ConnectorConstants.ScimClientConnectorName));
        var connectedSystem = new ConnectedSystem { Name = "Service Provider", ConnectorDefinitionId = definition!.Id };
        await WithJimAsync(jim => jim.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem, _key));
        return connectedSystem.Id;
    }

    private static ConnectedSystemSettingValue PasswordOf(ConnectedSystem connectedSystem) =>
        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == ScimConnectorConstants.SettingPassword);

    private static async Task<int> PasswordValueIdAsync(JimDbContext ctx, int connectedSystemId) =>
        await ctx.ConnectedSystemSettingValues
            .Where(sv => sv.ConnectedSystem.Id == connectedSystemId && sv.Setting.Name == ScimConnectorConstants.SettingPassword)
            .Select(sv => sv.Id)
            .SingleAsync();

    /// <summary>
    /// The column as stored, read on a fresh context so nothing a save left in memory can stand in for it.
    /// </summary>
    private async Task<string?> StoredPasswordAsync(int connectedSystemId)
    {
        await using var ctx = NewContext();
        var passwordValueId = await PasswordValueIdAsync(ctx, connectedSystemId);
        return await ctx.ConnectedSystemSettingValues
            .Where(sv => sv.Id == passwordValueId)
            .Select(sv => sv.StringEncryptedValue)
            .SingleAsync();
    }

    private async Task<T> WithJimAsync<T>(Func<JimApplication, Task<T>> call)
    {
        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx)) { CredentialProtection = _credentialProtection };
        return await call(jim);
    }

    private async Task WithJimAsync(Func<JimApplication, Task> call)
    {
        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx)) { CredentialProtection = _credentialProtection };
        await call(jim);
    }
}
