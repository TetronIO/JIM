// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.ExampleData;
using JIM.Models.Logic;
using JIM.Models.Scheduling;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of <see cref="JIM.PostgresData.Repositories.SystemRepository.ResetSystemAsync"/>.
/// The factory reset is implemented in raw SQL (TRUNCATE / ordered DELETE with foreign-key cascades), which the
/// EF Core in-memory provider cannot meaningfully execute, so this fixture exercises the real SQL against a real
/// database. It is opt-in: set the <c>JIM_TEST_RESET_DB</c> environment variable to the name of a PostgreSQL
/// database the test may freely wipe (host/credentials via <c>JIM_TEST_RESET_*</c>, defaulting to a local
/// instance). When the variable is absent the fixture is ignored, so ordinary <c>dotnet test</c> runs are unaffected.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SystemResetDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            // Mirror JimDbContext.OnConfiguring: the production model carries deliberate snapshot drift,
            // so the pending-model-changes warning is suppressed there. The DI options constructor bypasses
            // OnConfiguring, so suppress it here too (otherwise Migrate() throws once all migrations are applied).
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL reset tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        _connectionString = $"Host={host};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp()
    {
        // Clean slate: truncate every table (including built-ins and the migration-managed singleton)
        // so each test seeds and asserts in isolation.
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

    /// <summary>
    /// Seeds a representative graph: built-in + custom roles, attributes and object type, an administrator
    /// MVO (with built-in and custom attribute values and the Administrator role membership), a non-admin MVO,
    /// an empty Connected System, and the service settings singleton. Returns the two MVO ids.
    /// </summary>
    private async Task<(Guid adminId, Guid userId)> SeedAsync()
    {
        await using var ctx = NewContext();

        var adminRole = new Role { Name = Constants.BuiltInRoles.Administrator, BuiltIn = true };
        var customRole = new Role { Name = "Custom Role", BuiltIn = false };
        ctx.Roles.AddRange(adminRole, customRole);

        var userType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.User, PluralName = "Users", BuiltIn = true };
        var customType = new MetaverseObjectType { Name = "Widget", PluralName = "Widgets", BuiltIn = false };
        ctx.MetaverseObjectTypes.AddRange(userType, customType);

        var builtInAttr = new MetaverseAttribute
        {
            Name = Constants.BuiltInAttributes.DisplayName,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            BuiltIn = true
        };
        var customAttr = new MetaverseAttribute
        {
            Name = "Favourite Colour",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            BuiltIn = false
        };
        ctx.MetaverseAttributes.AddRange(builtInAttr, customAttr);

        var admin = new MetaverseObject { Type = userType, Roles = new List<Role> { adminRole } };
        admin.AttributeValues.Add(new MetaverseObjectAttributeValue { MetaverseObject = admin, Attribute = builtInAttr, StringValue = "Ada Admin" });
        admin.AttributeValues.Add(new MetaverseObjectAttributeValue { MetaverseObject = admin, Attribute = customAttr, StringValue = "green" });

        var user = new MetaverseObject { Type = userType, Roles = new List<Role>() };
        user.AttributeValues.Add(new MetaverseObjectAttributeValue { MetaverseObject = user, Attribute = builtInAttr, StringValue = "Norman NonAdmin" });

        ctx.MetaverseObjects.AddRange(admin, user);

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        ctx.ConnectorDefinitions.Add(connectorDefinition);
        ctx.ConnectedSystems.Add(new ConnectedSystem { Name = "Test System", ConnectorDefinition = connectorDefinition });

        ctx.Add(new ServiceSettings { IsServiceInMaintenanceMode = false });

        await ctx.SaveChangesAsync();
        return (admin.Id, user.Id);
    }

    [Test]
    public async Task ResetSystemAsync_DefaultMode_PreservesAdministratorsAndWipesEverythingElseAsync()
    {
        var (adminId, userId) = await SeedAsync();

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var result = await repository.System.ResetSystemAsync(includeAdministrators: false);

            Assert.That(result.AdministratorsRetained, Is.EqualTo(1));
            Assert.That(result.AdministratorsRemoved, Is.EqualTo(0));
            Assert.That(result.MetaverseObjectsRemoved, Is.EqualTo(1), "Only the non-administrator MVO should be counted as removed.");
            Assert.That(result.ConnectedSystemsRemoved, Is.EqualTo(1));
        }

        await using var verify = NewContext();
        Assert.That(await verify.MetaverseObjects.AnyAsync(o => o.Id == adminId), Is.True, "Administrator MVO must be preserved.");
        Assert.That(await verify.MetaverseObjects.AnyAsync(o => o.Id == userId), Is.False, "Non-administrator MVO must be removed.");

        // Administrator keeps its built-in attribute value; the custom one is cascade-removed with the custom attribute.
        var adminAttrValues = await verify.MetaverseObjectAttributeValues
            .Where(av => av.MetaverseObject.Id == adminId)
            .Include(av => av.Attribute)
            .ToListAsync();
        Assert.That(adminAttrValues, Has.Count.EqualTo(1));
        Assert.That(adminAttrValues[0].Attribute.Name, Is.EqualTo(Constants.BuiltInAttributes.DisplayName));

        // Built-ins preserved, custom rows gone.
        Assert.That(await verify.Roles.CountAsync(r => r.BuiltIn), Is.EqualTo(1));
        Assert.That(await verify.Roles.AnyAsync(r => !r.BuiltIn), Is.False);
        Assert.That(await verify.MetaverseAttributes.AnyAsync(a => !a.BuiltIn), Is.False);
        Assert.That(await verify.MetaverseObjectTypes.AnyAsync(t => !t.BuiltIn), Is.False);
        Assert.That(await verify.ConnectedSystems.AnyAsync(), Is.False);
        Assert.That(await verify.ServiceSettings.AnyAsync(), Is.True, "The service settings singleton must be preserved.");

        // The administrator retains the Administrator role membership.
        var adminRoleStillAssigned = await verify.Database
            .SqlQueryRaw<int>(@"SELECT COUNT(*)::int AS ""Value"" FROM ""MetaverseObjectRole"" WHERE ""StaticMembersId"" = {0}", adminId)
            .SingleAsync();
        Assert.That(adminRoleStillAssigned, Is.EqualTo(1));
    }

    [Test]
    public async Task ResetSystemAsync_IncludeAdministrators_RemovesAdministratorsTooAsync()
    {
        // Seed for its side effect; neither returned id is used in this total-wipe assertion.
        await SeedAsync();

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var result = await repository.System.ResetSystemAsync(includeAdministrators: true);

            Assert.That(result.AdministratorsRetained, Is.EqualTo(0));
            Assert.That(result.AdministratorsRemoved, Is.EqualTo(1));
            Assert.That(result.MetaverseObjectsRemoved, Is.EqualTo(2));
        }

        await using var verify = NewContext();
        Assert.That(await verify.MetaverseObjects.AnyAsync(), Is.False, "All MVOs including administrators must be removed.");
        Assert.That(await verify.Roles.AnyAsync(r => r.BuiltIn), Is.True, "Built-in roles must still be preserved.");
        Assert.That(await verify.ServiceSettings.AnyAsync(), Is.True);
    }

    /// <summary>
    /// Verifies the counts added to close the reporting completeness gap: Object Matching Rules,
    /// schedule executions, change history (metaverse + connected-system object changes), and custom
    /// example data templates. Built-in example data templates must be preserved (only the custom one counted).
    /// </summary>
    [Test]
    public async Task ResetSystemAsync_CapturesNewlyCountedObjectTypesAsync()
    {
        await using (var ctx = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var connectedSystem = new ConnectedSystem { Name = "Test System", ConnectorDefinition = connectorDefinition };
            var userType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.User, PluralName = "Users", BuiltIn = true };
            var mvo = new MetaverseObject { Type = userType, Roles = new List<Role>() };
            var schedule = new Schedule { Name = "Nightly" };

            ctx.ConnectorDefinitions.Add(connectorDefinition);
            ctx.ConnectedSystems.Add(connectedSystem);
            ctx.MetaverseObjectTypes.Add(userType);
            ctx.MetaverseObjects.Add(mvo);
            ctx.Schedules.Add(schedule);
            ctx.ObjectMatchingRules.Add(new ObjectMatchingRule { Order = 1 });
            ctx.ExampleDataTemplates.Add(new ExampleDataTemplate { Name = "Custom Template", BuiltIn = false });
            ctx.ExampleDataTemplates.Add(new ExampleDataTemplate { Name = "Built-in Template", BuiltIn = true });

            // Save the parents first so the Connected System has a generated id for the CSO change FK.
            await ctx.SaveChangesAsync();

            ctx.MetaverseObjectChanges.Add(new MetaverseObjectChange
            {
                MetaverseObject = mvo,
                ChangeTime = DateTime.UtcNow,
                ChangeType = ObjectChangeType.Created,
                ChangeInitiatorType = MetaverseObjectChangeInitiatorType.System
            });
            ctx.ConnectedSystemObjectChanges.Add(new ConnectedSystemObjectChange
            {
                ConnectedSystemId = connectedSystem.Id,
                ChangeTime = DateTime.UtcNow,
                ChangeType = ObjectChangeType.Added
            });
            ctx.ScheduleExecutions.Add(new ScheduleExecution
            {
                Schedule = schedule,
                ScheduleName = schedule.Name,
                Status = ScheduleExecutionStatus.Completed
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var result = await repository.System.ResetSystemAsync(includeAdministrators: true);

            Assert.Multiple(() =>
            {
                Assert.That(result.ObjectMatchingRulesRemoved, Is.EqualTo(1));
                Assert.That(result.ScheduleExecutionsRemoved, Is.EqualTo(1));
                Assert.That(result.SchedulesRemoved, Is.EqualTo(1));
                Assert.That(result.MetaverseObjectChangesRemoved, Is.EqualTo(1));
                Assert.That(result.ConnectedSystemObjectChangesRemoved, Is.EqualTo(1));
                Assert.That(result.CustomExampleDataTemplatesRemoved, Is.EqualTo(1), "Only the non-built-in template should be counted.");
            });
        }

        await using var verify = NewContext();
        Assert.That(await verify.ObjectMatchingRules.AnyAsync(), Is.False);
        Assert.That(await verify.ScheduleExecutions.AnyAsync(), Is.False);
        Assert.That(await verify.MetaverseObjectChanges.AnyAsync(), Is.False);
        Assert.That(await verify.ConnectedSystemObjectChanges.AnyAsync(), Is.False);
        Assert.That(await verify.ExampleDataTemplates.AnyAsync(t => !t.BuiltIn), Is.False, "Custom template must be removed.");
        Assert.That(await verify.ExampleDataTemplates.AnyAsync(t => t.BuiltIn), Is.True, "Built-in template must be preserved.");
    }

    /// <summary>
    /// The reset's TRUNCATE ... CASCADE wipes the built-in example data template's attributes as collateral (they are
    /// pulled in via ExampleDataTemplateAttributes -> ConnectedSystemAttributes -> ... -> ConnectedSystems). The
    /// built-in template is meant to survive a reset, so EnsureBuiltInExampleDataTemplateAsync recreates it. This
    /// verifies the recreate against real PostgreSQL, including the many-to-many reference object types (which a naive
    /// re-insert of a graph referencing existing rows would get wrong).
    /// </summary>
    [Test]
    public async Task EnsureBuiltInExampleDataTemplate_AfterAttributesAreCascadeWiped_RestoresThemIncludingReferencesAsync()
    {
        // Arrange: a full first-run seed creates the built-in "Users & Groups" template with all its attributes.
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            await jim.Seeding.SeedAsync();
        }

        int seededAttributeCount;
        await using (var ctx = NewContext())
            seededAttributeCount = await ctx.ExampleDataTemplateAttributes.CountAsync();
        Assert.That(seededAttributeCount, Is.GreaterThan(0), "seeding should create the built-in template's attributes");

        // Simulate the reset's cascade wiping the attributes (leaving the template + object-type shell).
        await using (var ctx = NewContext())
            await ctx.Database.ExecuteSqlRawAsync(@"TRUNCATE TABLE ""ExampleDataTemplateAttributes"" CASCADE;");
        await using (var ctx = NewContext())
            Assert.That(await ctx.ExampleDataTemplateAttributes.CountAsync(), Is.EqualTo(0), "precondition: the cascade wiped the attributes");

        // Act: the repair.
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            await jim.Seeding.EnsureBuiltInExampleDataTemplateAsync();
        }

        // Assert: every attribute is restored, and the many-to-many reference object types are wired up again.
        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var template = await repository.ExampleData.GetTemplateAsync("Users & Groups");
            Assert.That(template, Is.Not.Null);

            var restoredAttributes = template!.ObjectTypes.SelectMany(ot => ot.TemplateAttributes).ToList();
            Assert.That(restoredAttributes.Count, Is.EqualTo(seededAttributeCount), "all attributes should be restored");
            Assert.That(restoredAttributes.Any(a => a.ReferenceMetaverseObjectTypes.Count > 0), Is.True,
                "reference attributes (e.g. Manager) should have their many-to-many object types restored");
        }

        // And it is idempotent: a second call against a now-complete template changes nothing.
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            await jim.Seeding.EnsureBuiltInExampleDataTemplateAsync();
        }
        await using (var ctx = NewContext())
            Assert.That(await ctx.ExampleDataTemplateAttributes.CountAsync(), Is.EqualTo(seededAttributeCount),
                "a second EnsureBuiltIn call on a complete template must be a no-op");
    }
}
