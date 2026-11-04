// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL proof that what a Connector's settings check learnt (<see cref="ConnectedSystem.SettingsCheckFacts"/>,
/// #2021) is stored by the save that ran the check, the way the portal saves it, and comes back through the detected
/// capabilities read the Details tab, the REST API and PowerShell all use.
/// </summary>
/// <remarks>
/// Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables like the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemSettingsCheckFactsDatabaseTests
{
    private string _connectionString = null!;
    private ApiKey _key = null!;

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL settings check facts tests.");

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
    public async Task UpdateConnectedSystemAsync_DetachedSaveCarryingFacts_StoresThemAndTheCapabilitiesReadReturnsThemAsync()
    {
        var connectedSystemId = await CreateLdapConnectedSystemAsync();
        var detached = (await WithJimAsync(jim => jim.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId)))!;
        detached.SettingsCheckFacts =
        [
            new ConnectorCapability { Name = "Exchange", Value = "Version 15.2 (Build 1544.4)" },
            new ConnectorCapability { Name = "Exchange Sign-in", Value = "svc-jim@PANOPLY.LOCAL, by Kerberos through dc1.panoply.local" }
        ];

        await WithJimAsync(jim => jim.ConnectedSystems.UpdateConnectedSystemAsync(detached, _key));

        var capabilities = await WithJimAsync(jim => jim.ConnectedSystems.GetConnectedSystemDetectedCapabilitiesAsync(connectedSystemId));
        Assert.That(capabilities!.Select(c => (c.Name, c.Value)), Is.EqualTo(new[]
        {
            ("Exchange", "Version 15.2 (Build 1544.4)"),
            ("Exchange Sign-in", "svc-jim@PANOPLY.LOCAL, by Kerberos through dc1.panoply.local")
        }), "an LDAP Connected System never imported has no directory facts yet, so the check's are all there is");
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_FactsCleared_StoresNullAsync()
    {
        var connectedSystemId = await CreateLdapConnectedSystemAsync();
        var first = (await WithJimAsync(jim => jim.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId)))!;
        first.SettingsCheckFacts = [new ConnectorCapability { Name = "Exchange", Value = "Version 15.2 (Build 1544.4)" }];
        await WithJimAsync(jim => jim.ConnectedSystems.UpdateConnectedSystemAsync(first, _key));

        var second = (await WithJimAsync(jim => jim.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId)))!;
        Assert.That(second.SettingsCheckFacts, Has.Count.EqualTo(1), "a load carries the stored facts");
        second.SettingsCheckFacts = null;
        await WithJimAsync(jim => jim.ConnectedSystems.UpdateConnectedSystemAsync(second, _key));

        await using var ctx = NewContext();
        Assert.That(await ctx.ConnectedSystems.Where(cs => cs.Id == connectedSystemId).Select(cs => cs.SettingsCheckFacts).SingleAsync(), Is.Null);
    }

    private async Task<int> CreateLdapConnectedSystemAsync()
    {
        var definition = await WithJimAsync(jim => jim.ConnectedSystems.GetConnectorDefinitionAsync(ConnectorConstants.LdapConnectorName));
        var connectedSystem = new ConnectedSystem { Name = "PANOPLY", ConnectorDefinitionId = definition!.Id };
        await WithJimAsync(jim => jim.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem, _key));
        return connectedSystem.Id;
    }

    private async Task<T> WithJimAsync<T>(Func<JimApplication, Task<T>> call)
    {
        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        return await call(jim);
    }

    private async Task WithJimAsync(Func<JimApplication, Task> call)
    {
        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        await call(jim);
    }
}
