// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification that seeding built-in configuration can be repeated (issue #1287). SeedAsync
/// documents that a crash part way through is safe because the next start seeds again; that promise was false, and
/// an instance whose ServiceSettings row was absent while the rest of the seed data was present crash-looped on
/// every start.
/// </summary>
/// <remarks>
/// Only a real database can prove this. Three of the four faults are constraint violations the EF Core in-memory
/// provider cannot raise: it enforces no primary key on <c>ExampleDataSets</c> when an already-persisted set is
/// re-inserted, and none on the <c>MetaverseAttributeMetaverseObjectType</c> join table when a binding is added
/// twice. The mock-based <see cref="SeedingIdempotencyTests"/> covers the seeding logic; this fixture covers what
/// the database says about it.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other real-database fixtures; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SeedingIdempotencyDatabaseTests
{
    private string _connectionString = null!;

    // TrackAll, because that is what JIM.Worker configures (JIM.Worker/Program.cs) and the worker is the only host
    // that seeds. The tracking behaviour is load-bearing here: seeding tops up an existing Example Data Set's values
    // and adds missing Object Type bindings by mutating tracked entities.
    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL seeding idempotency tests.");

        TestUtilities.SetEnvironmentVariables();

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
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(@"
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory') LOOP
                    EXECUTE 'TRUNCATE TABLE ""' || r.tablename || '"" RESTART IDENTITY CASCADE';
                END LOOP;
            END $$;");
    }

    [Test]
    public async Task SeedAsync_RetriedAfterServiceSettingsIsLost_CompletesAndDuplicatesNothingAsync()
    {
        await SeedAsync();
        var afterFirstPass = await CountsAsync();

        // The state a crash part way through leaves behind, and the state the crash loop was observed in: every
        // built-in object present, but the ServiceSettings row that marks seeding complete absent, so the next start
        // seeds again from the top.
        await using (var ctx = NewContext())
            await ctx.Database.ExecuteSqlRawAsync(@"DELETE FROM ""ServiceSettings""");

        Assert.That(SeedAsync, Throws.Nothing, "a retry against a partially-seeded database must complete");

        var afterRetry = await CountsAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterRetry.ExampleDataSets, Is.EqualTo(afterFirstPass.ExampleDataSets), "Example Data Sets");
            Assert.That(afterRetry.ExampleDataSetValues, Is.EqualTo(afterFirstPass.ExampleDataSetValues), "Example Data Set values");
            Assert.That(afterRetry.MetaverseAttributes, Is.EqualTo(afterFirstPass.MetaverseAttributes), "Metaverse Attributes");
            Assert.That(afterRetry.MetaverseObjectTypes, Is.EqualTo(afterFirstPass.MetaverseObjectTypes), "Metaverse Object Types");
            Assert.That(afterRetry.AttributeBindings, Is.EqualTo(afterFirstPass.AttributeBindings), "Metaverse Attribute bindings");
            Assert.That(afterRetry.PredefinedSearches, Is.EqualTo(afterFirstPass.PredefinedSearches), "Predefined Searches");
            Assert.That(afterRetry.ExampleDataTemplates, Is.EqualTo(afterFirstPass.ExampleDataTemplates), "Example Data Templates");
            Assert.That(afterRetry.ConnectorDefinitions, Is.EqualTo(afterFirstPass.ConnectorDefinitions), "Connector Definitions");
            Assert.That(afterRetry.ServiceSettings, Is.EqualTo(1), "exactly one ServiceSettings row");
        }

        // Nothing was created, so nothing may be re-baselined: a second Create Activity for an object that already
        // existed would misreport its origin in the change history.
        Assert.That(afterRetry.CreateActivities, Is.EqualTo(afterFirstPass.CreateActivities),
            "a retry that creates nothing must record no further Create Activities");
    }

    [Test]
    public async Task SeedAsync_RunTwiceWithoutLosingServiceSettings_IsANoOpAsync()
    {
        await SeedAsync();
        var afterFirstPass = await CountsAsync();

        await SeedAsync();

        Assert.That(await CountsAsync(), Is.EqualTo(afterFirstPass), "an ordinary restart must change nothing");
    }

    [Test]
    public async Task SyncBuiltInConnectorDefinitionsAsync_DefinitionMissingFromASeededDatabase_CreatesItAsync()
    {
        // The upgrade case: a Connector added to BuiltInConnectors() in a later release reaches a deployment that
        // was seeded before it existed. SeedAsync short-circuits there, so the startup sync has to create it.
        await SeedAsync();

        await using (var ctx = NewContext())
        {
            // its settings hold a non-cascading foreign key to it, so they go first.
            await ctx.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""ConnectorDefinitionSettings"" WHERE ""ConnectorDefinitionId"" IN
                  (SELECT ""Id"" FROM ""ConnectorDefinitions"" WHERE ""Name"" = 'JIM SQL Connector')");
            await ctx.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""ConnectorDefinitions"" WHERE ""Name"" = 'JIM SQL Connector'");
        }

        await using (var ctx = NewContext())
        {
            var jim = NewApplication(ctx);
            await jim.Seeding.SyncBuiltInConnectorDefinitionsAsync();
            await jim.Seeding.CompleteSeedingActivityAsync();
        }

        await using (var ctx = NewContext())
        {
            var definition = await ctx.ConnectorDefinitions
                .Include(d => d.Settings)
                .SingleOrDefaultAsync(d => d.Name == "JIM SQL Connector");

            Assert.That(definition, Is.Not.Null, "the missing built-in Connector Definition must be created");
            Assert.That(definition!.BuiltIn, Is.True);
            Assert.That(definition.Settings, Is.Not.Empty, "it must carry the Connector's declared settings");

            // keyed on the new definition's id, not its name: the definition deleted above left its own baseline
            // Activity behind, and the recreated definition starts its history at version 1 again.
            var activity = await ctx.Activities.SingleOrDefaultAsync(a =>
                a.TargetType == ActivityTargetType.ConnectorDefinition &&
                a.ConnectorDefinitionId == definition.Id &&
                a.ConfigurationChangeVersion == 1);
            Assert.That(activity, Is.Not.Null, "its creation must be recorded as a System-attributed version-1 baseline");
            Assert.That(activity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
            Assert.That(activity.ParentActivityId, Is.Not.Null, "grouped under the System Initialisation Activity");
        }
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The seeding half of <see cref="JimApplication.InitialiseDatabaseAsync"/>, on a context of its own so each pass
    /// starts with a cold change tracker, exactly as a restarted worker does. Service Setting synchronisation is left
    /// out: it reads its values from the environment and has nothing to do with this issue.
    /// </summary>
    private async Task SeedAsync()
    {
        await using var ctx = NewContext();
        var jim = NewApplication(ctx);
        try
        {
            await jim.Seeding.SeedAsync();
            await jim.Seeding.SyncBuiltInMetaverseSchemaAsync();
            await jim.Seeding.SeedBuiltInSchedulesAsync();
            await jim.Seeding.SeedBuiltInRolesAsync();
            await jim.Seeding.SyncBuiltInConnectorDefinitionsAsync();
            await jim.Seeding.EnsureBuiltInExampleDataTemplateAsync();
            await jim.Seeding.CompleteSeedingActivityAsync();
        }
        catch (Exception ex)
        {
            await jim.Seeding.FailSeedingActivityAsync(ex);
            throw;
        }
    }

    private static JimApplication NewApplication(JimDbContext context) =>
        new(new PostgresDataRepository(context)) { CredentialProtection = new FakeProtection() };

    private async Task<SeededCounts> CountsAsync()
    {
        await using var ctx = NewContext();
        return new SeededCounts(
            await ctx.ExampleDataSets.CountAsync(),
            await ctx.ExampleDataSetValues.CountAsync(),
            await ctx.MetaverseAttributes.CountAsync(),
            await ctx.MetaverseObjectTypes.CountAsync(),
            // the attribute-to-object-type binding is a join table with no entity type of its own, so it is counted
            // directly. Duplicating a binding is what a retry did before the fix, and PostgreSQL rejected the whole
            // seed for it.
            await ctx.Database.SqlQueryRaw<int>(
                @"SELECT COUNT(*)::int AS ""Value"" FROM ""MetaverseAttributeMetaverseObjectType""").SingleAsync(),
            await ctx.PredefinedSearches.CountAsync(),
            await ctx.ExampleDataTemplates.CountAsync(),
            await ctx.ConnectorDefinitions.CountAsync(),
            await ctx.ServiceSettings.CountAsync(),
            await ctx.Activities.CountAsync(a => a.TargetOperationType == ActivityTargetOperationType.Create));
    }

    private sealed record SeededCounts(
        int ExampleDataSets,
        int ExampleDataSetValues,
        int MetaverseAttributes,
        int MetaverseObjectTypes,
        int AttributeBindings,
        int PredefinedSearches,
        int ExampleDataTemplates,
        int ConnectorDefinitions,
        int ServiceSettings,
        int CreateActivities);

    private sealed class FakeProtection : ICredentialProtectionService
    {
        private const string Prefix = "$JIM$v1$";

        public string? Protect(string? plainText) =>
            string.IsNullOrEmpty(plainText) ? plainText : Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

        public string? Unprotect(string? protectedData) =>
            string.IsNullOrEmpty(protectedData) || !IsProtected(protectedData)
                ? protectedData
                : Encoding.UTF8.GetString(Convert.FromBase64String(protectedData[Prefix.Length..]));

        public bool IsProtected(string? value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
