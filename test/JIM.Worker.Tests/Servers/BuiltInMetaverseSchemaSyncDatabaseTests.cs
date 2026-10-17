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
/// Real-PostgreSQL verification of the built-in Metaverse schema synchronisation pass (issue #1104). The EF Core
/// in-memory provider auto-tracks navigation properties, so the mocked unit tests cannot prove that
/// GetMetaverseAttributesForSchemaSyncAsync / GetBuiltInMetaverseObjectTypesForSchemaSyncAsync use AsTracking and the
/// right Includes; if either regressed, the pass would re-add every Standard Mapping on each startup and the second
/// boot would die on the (MetaverseAttributeId, Standard, CounterpartName) unique index. These tests run the pass
/// against a real database, twice, exactly as consecutive service startups do. Opt-in via <c>JIM_TEST_RESET_DB</c>;
/// ignored when it is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class BuiltInMetaverseSchemaSyncDatabaseTests
{
    private string _connectionString = null!;

    // Mirrors production hosts: JimDbContext defaults to NoTracking on the parameterless-constructor path, so the
    // pass's AsTracking opt-ins are what make its mutations persist. Testing with EF's tracking default would mask
    // a dropped AsTracking.
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL built-in schema sync tests.");

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
    public async Task SyncBuiltInMetaverseSchemaAsync_RunTwiceFromFresh_ConvergesThenNoOpsAsync()
    {
        // first startup: full seed then the schema sync pass, sharing one context as InitialiseDatabaseAsync does.
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            await jim.Seeding.SeedAsync();
            await jim.Seeding.SyncBuiltInMetaverseSchemaAsync();
            await jim.Seeding.CompleteSeedingActivityAsync();
        }

        int attributeCount, mappingCount, bindingCount;
        await using (var check = NewContext())
        {
            attributeCount = await check.MetaverseAttributes.CountAsync();
            mappingCount = await check.MetaverseAttributeStandardMappings.CountAsync();
            bindingCount = await check.MetaverseObjectTypes.Where(t => t.BuiltIn).SelectMany(t => t.Attributes).CountAsync();

            // the catalogue must be fully materialised: every definition exists as a built-in attribute with its
            // mappings, and the SCIM-parity gap attributes are bound per the catalogue.
            Assert.That(attributeCount, Is.EqualTo(BuiltInMetaverseSchema.Attributes.Count));
            Assert.That(mappingCount, Is.EqualTo(BuiltInMetaverseSchema.Attributes.Sum(a => a.StandardMappings.Count)));

            var emails = await check.MetaverseAttributes
                .Include(a => a.StandardMappings)
                .Include(a => a.MetaverseObjectTypes)
                .SingleAsync(a => a.Name == Constants.BuiltInAttributes.Emails);
            Assert.That(emails.BuiltIn, Is.True);
            Assert.That(emails.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
            Assert.That(emails.StandardMappings.Any(m => m.Standard == AttributeStandard.Ldap && m.CounterpartName == "mail"), Is.True);
            Assert.That(emails.MetaverseObjectTypes.Select(t => t.Name),
                Is.EquivalentTo(new[] { Constants.BuiltInObjectTypes.User, Constants.BuiltInObjectTypes.Group }));
        }

        // second startup: SeedAsync short-circuits (ServiceSettings exists); the sync pass must be a pure no-op.
        // this is the regression net for the repository queries: a dropped Include or AsTracking makes the pass
        // re-add mappings or bindings, which either duplicates rows or trips the unique index right here.
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            await jim.Seeding.SeedAsync();
            await jim.Seeding.SyncBuiltInMetaverseSchemaAsync();
            await jim.Seeding.CompleteSeedingActivityAsync();
        }

        await using (var recheck = NewContext())
        {
            Assert.That(await recheck.MetaverseAttributes.CountAsync(), Is.EqualTo(attributeCount), "a second startup must create no attributes");
            Assert.That(await recheck.MetaverseAttributeStandardMappings.CountAsync(), Is.EqualTo(mappingCount), "a second startup must create no Standard Mappings");
            Assert.That(await recheck.MetaverseObjectTypes.Where(t => t.BuiltIn).SelectMany(t => t.Attributes).CountAsync(), Is.EqualTo(bindingCount), "a second startup must create no bindings");
        }
    }

    [Test]
    public async Task SyncBuiltInMetaverseSchemaAsync_ExistingDeploymentMissingGapAttributes_CreatesAndBindsThemAsync()
    {
        // simulate an existing deployment that predates the gap attributes: full legacy seed only (SeedAsync does
        // not create them), then a service upgrade runs the sync pass.
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            await jim.Seeding.SeedAsync();
            await jim.Seeding.CompleteSeedingActivityAsync();
        }

        await using (var check = NewContext())
        {
            Assert.That(await check.MetaverseAttributes.AnyAsync(a => a.Name == Constants.BuiltInAttributes.Emails), Is.False,
                "precondition: the legacy seed must not create the gap attributes");
        }

        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            await jim.Seeding.SeedAsync();
            await jim.Seeding.SyncBuiltInMetaverseSchemaAsync();
            await jim.Seeding.CompleteSeedingActivityAsync();
        }

        await using (var recheck = NewContext())
        {
            var accountEnabled = await recheck.MetaverseAttributes
                .Include(a => a.StandardMappings)
                .Include(a => a.MetaverseObjectTypes)
                .SingleAsync(a => a.Name == Constants.BuiltInAttributes.AccountEnabled);
            Assert.That(accountEnabled.BuiltIn, Is.True);
            Assert.That(accountEnabled.Type, Is.EqualTo(AttributeDataType.Boolean));
            Assert.That(accountEnabled.MetaverseObjectTypes.Select(t => t.Name), Is.EquivalentTo(new[] { Constants.BuiltInObjectTypes.User }));
            Assert.That(accountEnabled.StandardMappings.Any(m => m.Standard == AttributeStandard.Scim && m.CounterpartName == "active"), Is.True);

            // legacy attributes must have gained their Standard Mappings too
            var firstName = await recheck.MetaverseAttributes
                .Include(a => a.StandardMappings)
                .SingleAsync(a => a.Name == Constants.BuiltInAttributes.FirstName);
            Assert.That(firstName.StandardMappings.Any(m => m.Standard == AttributeStandard.Ldap && m.CounterpartName == "givenName"), Is.True);
        }
    }

    [Test]
    public async Task SyncBuiltInMetaverseSchemaAsync_DeploymentSeededWithRfcPathCounterparts_ConvergesToTheFlattenedNamesAsync()
    {
        // #2095: deployments seeded before the fix hold SCIM counterparts as RFC 7643 paths. Simulate one by seeding
        // the current catalogue and then rewriting rows back to the shipped shape: Mobile Number named "phoneNumbers"
        // and Emails carried a SCIM "emails" mapping. Startup must replace the first and remove the second.
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            await jim.Seeding.SeedAsync();
            await jim.Seeding.SyncBuiltInMetaverseSchemaAsync();
            await jim.Seeding.CompleteSeedingActivityAsync();
        }

        await using (var legacy = NewContext())
        {
            var mobileId = await legacy.MetaverseAttributes.Where(a => a.Name == Constants.BuiltInAttributes.MobileNumber).Select(a => a.Id).SingleAsync();
            var emailsId = await legacy.MetaverseAttributes.Where(a => a.Name == Constants.BuiltInAttributes.Emails).Select(a => a.Id).SingleAsync();

            var renamed = await legacy.MetaverseAttributeStandardMappings
                .Where(m => m.MetaverseAttributeId == mobileId && m.Standard == AttributeStandard.Scim)
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.CounterpartName, "phoneNumbers"));
            Assert.That(renamed, Is.EqualTo(1), "precondition: Mobile Number carries one SCIM mapping");

            legacy.MetaverseAttributeStandardMappings.Add(new MetaverseAttributeStandardMapping
            {
                MetaverseAttributeId = emailsId,
                Standard = AttributeStandard.Scim,
                CounterpartName = "emails",
                Notes = "Values only; SCIM sub-attributes such as type and primary are not modelled."
            });
            await legacy.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            await jim.Seeding.SeedAsync();
            await jim.Seeding.SyncBuiltInMetaverseSchemaAsync();
            await jim.Seeding.CompleteSeedingActivityAsync();
        }

        await using (var check = NewContext())
        {
            var scimCounterparts = await check.MetaverseAttributeStandardMappings
                .Where(m => m.Standard == AttributeStandard.Scim)
                .Select(m => new { m.MetaverseAttribute!.Name, m.CounterpartName })
                .ToListAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(scimCounterparts.Where(m => m.Name == Constants.BuiltInAttributes.MobileNumber).Select(m => m.CounterpartName),
                    Is.EqualTo(new[] { "phoneNumbers.mobile" }));
                Assert.That(scimCounterparts.Where(m => m.Name == Constants.BuiltInAttributes.Emails), Is.Empty,
                    "the stale SCIM emails mapping must be removed");
                Assert.That(scimCounterparts, Has.Count.EqualTo(BuiltInMetaverseSchema.Attributes.Sum(a => a.StandardMappings.Count(m => m.Standard == AttributeStandard.Scim))));
            }
        }
    }
}
