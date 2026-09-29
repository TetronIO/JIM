// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for the Synchronisation Rule mapping properties that carry a store-level default
/// (<see cref="SyncRuleMapping.InboundValueProcessing"/>, <see cref="SyncRuleMapping.Enabled"/> and
/// <see cref="SyncRuleMapping.Priority"/>).
/// <para>
/// EF Core omits a column from an INSERT when the property holds its sentinel (the CLR default unless configured
/// otherwise), so the database default is written in its place. With a store default that differs from the CLR
/// default, a mapping created with every inbound processing option off, or created disabled, was silently stored
/// with the opposite setting. Only a real provider applies store defaults, so the in-memory provider cannot see this.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleMappingStoreDefaultsDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Synchronisation Rule mapping store default tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    private async Task<(int SyncRuleId, int MvAttributeId)> SeedImportRuleAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var ctx = NewContext();

        var mvoType = new MetaverseObjectType { Name = $"Person-{suffix}", PluralName = $"People-{suffix}" };
        var mvAttribute = new MetaverseAttribute { Name = $"Display Name-{suffix}", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var connectorDefinition = new ConnectorDefinition { Name = $"def-{suffix}" };
        var system = new ConnectedSystem { Name = $"system-{suffix}", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        ctx.AddRange(mvoType, mvAttribute, connectorDefinition, system, csType);
        await ctx.SaveChangesAsync();

        var rule = new SyncRule
        {
            Name = $"import-{suffix}", Direction = SyncRuleDirection.Import, ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = csType.Id, MetaverseObjectTypeId = mvoType.Id
        };
        ctx.SyncRules.Add(rule);
        await ctx.SaveChangesAsync();
        return (rule.Id, mvAttribute.Id);
    }

    private async Task<SyncRuleMapping> InsertAndReloadAsync(Action<SyncRuleMapping> configure)
    {
        var (syncRuleId, mvAttributeId) = await SeedImportRuleAsync();
        var mapping = new SyncRuleMapping { SyncRuleId = syncRuleId, TargetMetaverseAttributeId = mvAttributeId };
        configure(mapping);

        await using (var ctx = NewContext())
        {
            ctx.SyncRuleMappings.Add(mapping);
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = NewContext();
        return await readCtx.SyncRuleMappings.SingleAsync(m => m.Id == mapping.Id);
    }

    [Test]
    public async Task Insert_InboundValueProcessingNone_IsStoredAsNoneAsync()
    {
        var stored = await InsertAndReloadAsync(m => m.InboundValueProcessing = InboundValueProcessing.None);

        Assert.That(stored.InboundValueProcessing, Is.EqualTo(InboundValueProcessing.None),
            "a mapping created with every inbound processing option off must not pick up the store default");
    }

    [Test]
    public async Task Insert_InboundValueProcessingDefault_IsStoredAsDefaultAsync()
    {
        var stored = await InsertAndReloadAsync(_ => { });

        Assert.That(stored.InboundValueProcessing, Is.EqualTo(InboundValueProcessing.TreatWhitespaceAsNoValue));
    }

    [Test]
    public async Task Insert_EnabledFalse_IsStoredAsDisabledAsync()
    {
        var stored = await InsertAndReloadAsync(m => m.Enabled = false);

        Assert.That(stored.Enabled, Is.False, "a mapping created disabled must not be stored enabled");
    }

    [Test]
    public async Task Insert_EnabledDefault_IsStoredAsEnabledAsync()
    {
        var stored = await InsertAndReloadAsync(_ => { });

        Assert.That(stored.Enabled, Is.True);
    }

    [Test]
    public async Task Insert_PriorityZero_IsStoredAsZeroAsync()
    {
        var stored = await InsertAndReloadAsync(m => m.Priority = 0);

        Assert.That(stored.Priority, Is.EqualTo(0));
    }

    [Test]
    public async Task Insert_PriorityDefault_IsStoredAsUnrankedAsync()
    {
        var stored = await InsertAndReloadAsync(_ => { });

        Assert.That(stored.Priority, Is.EqualTo(int.MaxValue));
    }
}
