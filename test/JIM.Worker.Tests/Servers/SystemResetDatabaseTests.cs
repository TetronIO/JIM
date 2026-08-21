// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.ExampleData;
using JIM.Models.Logic;
using JIM.Models.Scheduling;
using JIM.Models.Search;
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
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

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

    /// <summary>
    /// A preserved administrator whose reference attribute (for example Manager) pointed at a wiped
    /// object must not keep an informationless all-null ghost row after the reset (#1019): the
    /// restore nulls references to wiped objects before re-inserting, and a valueless row asserts
    /// nothing while rendering as a blank entry and corrupting later exports.
    /// </summary>
    [Test]
    public async Task ResetSystemAsync_AdminReferencedWipedObject_NoGhostRowRestoredAsync()
    {
        Guid adminId;
        await using (var seed = NewContext())
        {
            var adminRole = new Role { Name = Constants.BuiltInRoles.Administrator, BuiltIn = true };
            var userType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.User, PluralName = "Users", BuiltIn = true };
            var displayNameAttr = new MetaverseAttribute
            {
                Name = Constants.BuiltInAttributes.DisplayName,
                Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued,
                BuiltIn = true
            };
            var managerAttr = new MetaverseAttribute
            {
                Name = Constants.BuiltInAttributes.Manager,
                Type = AttributeDataType.Reference,
                AttributePlurality = AttributePlurality.SingleValued,
                BuiltIn = true
            };
            seed.Roles.Add(adminRole);
            seed.MetaverseObjectTypes.Add(userType);
            seed.MetaverseAttributes.AddRange(displayNameAttr, managerAttr);

            var wipedManager = new MetaverseObject { Type = userType, Roles = new List<Role>() };
            var admin = new MetaverseObject { Type = userType, Roles = new List<Role> { adminRole } };
            admin.AttributeValues.Add(new MetaverseObjectAttributeValue { MetaverseObject = admin, Attribute = displayNameAttr, StringValue = "Ada Admin" });
            admin.AttributeValues.Add(new MetaverseObjectAttributeValue { MetaverseObject = admin, Attribute = managerAttr, ReferenceValue = wipedManager });
            seed.MetaverseObjects.AddRange(admin, wipedManager);
            seed.Add(new ServiceSettings { IsServiceInMaintenanceMode = false });
            await seed.SaveChangesAsync();
            adminId = admin.Id;
        }

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var result = await repository.System.ResetSystemAsync(includeAdministrators: false);
            Assert.That(result.AdministratorsRetained, Is.EqualTo(1));
        }

        await using var verify = NewContext();
        var adminRows = await verify.MetaverseObjectAttributeValues
            .Where(av => av.MetaverseObject.Id == adminId)
            .Include(av => av.Attribute)
            .ToListAsync();
        Assert.That(adminRows, Has.Count.EqualTo(1),
            "Only the Display Name row may survive; the Manager row pointed at a wiped object and became valueless");
        Assert.That(adminRows[0].Attribute.Name, Is.EqualTo(Constants.BuiltInAttributes.DisplayName));
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
                Status = ScheduleExecutionStatus.Complete
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var result = await repository.System.ResetSystemAsync(includeAdministrators: true);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.ObjectMatchingRulesRemoved, Is.EqualTo(1));
                Assert.That(result.ScheduleExecutionsRemoved, Is.EqualTo(1));
                Assert.That(result.SchedulesRemoved, Is.EqualTo(1));
                Assert.That(result.MetaverseObjectChangesRemoved, Is.EqualTo(1));
                Assert.That(result.ConnectedSystemObjectChangesRemoved, Is.EqualTo(1));
                Assert.That(result.CustomExampleDataTemplatesRemoved, Is.EqualTo(1), "Only the non-built-in template should be counted.");
            }
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

        // Assert: every attribute is restored.
        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var template = await repository.ExampleData.GetTemplateAsync("Users & Groups");
            Assert.That(template, Is.Not.Null);

            var restoredAttributes = template!.ObjectTypes.SelectMany(ot => ot.TemplateAttributes).ToList();
            Assert.That(restoredAttributes.Count, Is.EqualTo(seededAttributeCount), "all attributes should be restored");
        }

        // And the many-to-many reference object types (e.g. Manager -> User) are wired up again. Assert against the
        // join table directly: it is the source of truth and does not depend on which navigation properties a given
        // retrieval query happens to eager-load (the by-name GetTemplateAsync does not include them).
        await using (var ctx = NewContext())
        {
            var referenceRowCount = await ctx.Database
                .SqlQueryRaw<int>(@"SELECT COUNT(*)::int AS ""Value"" FROM ""ExampleDataTemplateAttributeMetaverseObjectType""")
                .SingleAsync();
            Assert.That(referenceRowCount, Is.GreaterThan(0),
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

    /// <summary>
    /// The factory reset restores JIM's built-in configuration by re-running the whole built-in configuration
    /// pipeline, rather than repairing the specific things previous resets were observed to lose (issue #916).
    /// This proves the pipeline's two jobs against real PostgreSQL at once: it restores a built-in the wipe removed
    /// (the Schedules it truncates), and creates a built-in the database has never held, which is what a Connector
    /// or Predefined Search introduced in a later release looks like from an existing deployment.
    /// </summary>
    [Test]
    public async Task ResetSystemAsync_BuiltInsMissingOrNeverSeeded_AreRestoredByThePipelineAsync()
    {
        TestUtilities.SetEnvironmentVariables();

        // Arrange: apply the built-in configuration exactly as worker startup does.
        await using (var ctx = NewContext())
        {
            using var jim = new JimApplication(new PostgresDataRepository(ctx));
            await jim.Seeding.ApplyBuiltInConfigurationAsync();
        }

        var seeded = await CountBuiltInsAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(seeded.ObjectTypes, Is.GreaterThan(0), "precondition: the built-in Metaverse Object Types are seeded");
            Assert.That(seeded.Attributes, Is.GreaterThan(0));
            Assert.That(seeded.PredefinedSearches, Is.GreaterThan(0));
            Assert.That(seeded.ExampleDataSets, Is.GreaterThan(0));
            Assert.That(seeded.ExampleDataTemplateAttributes, Is.GreaterThan(0));
            Assert.That(seeded.ConnectorDefinitions, Is.GreaterThan(0));
            Assert.That(seeded.Schedules, Is.GreaterThan(0));
            Assert.That(seeded.Roles, Is.GreaterThan(0));
            Assert.That(seeded.ServiceSettings, Is.GreaterThan(0));
        }

        // Remove built-ins the pipeline owns but the wipe does NOT remove, so the reset has to create them rather
        // than merely leave them alone: a Predefined Search (owned by SeedAsync, which used to run once and never
        // again) and every built-in Connector Definition (owned by the startup sync). The wipe truncates the
        // Schedules and Activities tables by itself, so those categories are covered without help.
        await using (var ctx = NewContext())
        {
            // Criteria groups, criteria and Connector Definition settings all cascade from their owner now
            // (#1477), so deleting the owner is enough.
            await ctx.Database.ExecuteSqlRawAsync(@"DELETE FROM ""PredefinedSearches"" WHERE ""Uri"" = 'distribution-groups';");
            await ctx.Database.ExecuteSqlRawAsync(@"DELETE FROM ""ConnectorDefinitions"" WHERE ""BuiltIn"";");
        }

        // Act: the reset, through the application server so the pipeline runs, not just the repository's wipe.
        await using (var ctx = NewContext())
        {
            using var jim = new JimApplication(new PostgresDataRepository(ctx));
            await jim.System.ResetSystemAsync(
                ActivityInitiatorType.ApiKey, Guid.NewGuid(), "Infrastructure Key", includeAdministrators: false);
        }

        // Assert: every built-in category is back at its seeded count. Equality, not mere presence, is what proves
        // the pipeline neither missed a category nor duplicated one it should have left alone.
        var afterReset = await CountBuiltInsAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterReset.ObjectTypes, Is.EqualTo(seeded.ObjectTypes));
            Assert.That(afterReset.Attributes, Is.EqualTo(seeded.Attributes));
            Assert.That(afterReset.PredefinedSearches, Is.EqualTo(seeded.PredefinedSearches),
                "the deleted built-in Predefined Search must be re-created, and the others left alone");
            Assert.That(afterReset.PredefinedSearchCriteriaGroups, Is.EqualTo(seeded.PredefinedSearchCriteriaGroups),
                "a re-created Predefined Search must come back whole, criteria groups included");
            Assert.That(afterReset.ExampleDataSets, Is.EqualTo(seeded.ExampleDataSets));
            Assert.That(afterReset.ExampleDataTemplateAttributes, Is.EqualTo(seeded.ExampleDataTemplateAttributes),
                "the built-in Example Data Template's attributes are truncate collateral and must be restored");
            Assert.That(afterReset.ConnectorDefinitions, Is.EqualTo(seeded.ConnectorDefinitions),
                "built-in Connector Definitions the database has never held must be created by the reset");
            Assert.That(afterReset.Schedules, Is.EqualTo(seeded.Schedules),
                "the wipe truncates Schedules; the built-in ones must be back before the reset returns");
            Assert.That(afterReset.Roles, Is.EqualTo(seeded.Roles));
            Assert.That(afterReset.ServiceSettings, Is.EqualTo(seeded.ServiceSettings));
        }

        await using (var verify = NewContext())
        {
            var schedule = await verify.Schedules.SingleAsync(sc => sc.BuiltIn);
            Assert.That(schedule.IsEnabled, Is.True, "the restored built-in Schedule must be enabled");
            Assert.That(schedule.CreatedByType, Is.EqualTo(ActivityInitiatorType.System),
                "built-in Schedules are created through the audited path, attributed to System");

            // Provenance: the wipe truncated the Activities table, so every built-in that survived it needs its
            // version-1 baseline re-recorded, and every built-in the pipeline created needs its Create Activity.
            Assert.That(await verify.Activities.AnyAsync(a => a.TargetType == ActivityTargetType.SystemInitialisation), Is.True,
                "the restore must be grouped under a System Initialisation Activity");
            Assert.That(await verify.Activities.AnyAsync(a =>
                    a.TargetType == ActivityTargetType.SystemInitialisation && a.Status != ActivityStatus.Complete), Is.False,
                "leaving the parent InProgress would block any subsequent reset via the in-progress guard");
        }
    }

    /// <summary>
    /// A factory reset must remove custom configuration that has child rows, which is the ordinary shape of it
    /// rather than an edge case: a Predefined Search filters via criteria groups, a Connector Definition declares
    /// settings, an Example Data Set holds values, and an Example Data Template covers Object Types (issue #1477).
    /// None of those child foreign keys used to cascade, so the wipe's <c>WHERE "BuiltIn" = false</c> deletes failed
    /// with a foreign-key violation; because the whole wipe runs in one transaction, the entire reset rolled back
    /// and nothing at all was removed.
    /// <para>
    /// The in-memory provider enforces no foreign keys, so only a real database can see this.
    /// </para>
    /// </summary>
    [Test]
    public async Task ResetSystemAsync_CustomConfigurationWithChildRows_IsRemovedRatherThanBlockedAsync()
    {
        await SeedCustomConfigurationWithChildRowsAsync();

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var result = await repository.System.ResetSystemAsync(includeAdministrators: false);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.CustomPredefinedSearchesRemoved, Is.EqualTo(1));
                Assert.That(result.CustomConnectorDefinitionsRemoved, Is.EqualTo(1));
                Assert.That(result.CustomExampleDataSetsRemoved, Is.EqualTo(1));
                Assert.That(result.CustomExampleDataTemplatesRemoved, Is.EqualTo(1));
            }
        }

        await using var verify = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.PredefinedSearches.AnyAsync(s => !s.BuiltIn), Is.False, "the custom Predefined Search must be removed");
            Assert.That(await verify.PredefinedSearchCriteriaGroups.AnyAsync(), Is.False, "its criteria groups, nested ones included, must go with it");
            Assert.That(await verify.PredefinedSearchCriteria.AnyAsync(), Is.False, "its criteria must go with it");
            Assert.That(await verify.ConnectorDefinitions.AnyAsync(d => !d.BuiltIn), Is.False, "the custom Connector Definition must be removed");
            Assert.That(await verify.ConnectorDefinitionSettings.AnyAsync(), Is.False, "its settings must go with it");
            Assert.That(await verify.ExampleDataSets.AnyAsync(s => !s.BuiltIn), Is.False, "the custom Example Data Set must be removed");
            Assert.That(await verify.ExampleDataSetValues.AnyAsync(), Is.False, "its values must go with it");
            Assert.That(await verify.ExampleDataTemplates.AnyAsync(t => !t.BuiltIn), Is.False, "the custom Example Data Template must be removed");
            Assert.That(await verify.ExampleDataObjectTypes.AnyAsync(), Is.False, "its Object Types must go with it");
        }
    }

    /// <summary>
    /// A custom Metaverse Attribute chosen as the SSO unique identifier is referenced by the preserved Service
    /// Settings singleton, so deleting the attribute would violate that foreign key and roll the reset back
    /// (issue #1477). The reference is customer configuration and the attribute it names is about to be removed,
    /// so the reset clears it rather than being blocked by it.
    /// </summary>
    [Test]
    public async Task ResetSystemAsync_CustomAttributeIsTheSsoUniqueIdentifier_ReferenceIsClearedAsync()
    {
        await using (var seed = NewContext())
        {
            var customAttr = new MetaverseAttribute
            {
                Name = "Payroll Number",
                Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued,
                BuiltIn = false
            };
            seed.MetaverseAttributes.Add(customAttr);
            seed.Add(new ServiceSettings
            {
                IsServiceInMaintenanceMode = false,
                SSOUniqueIdentifierClaimType = "employeeNumber",
                SSOUniqueIdentifierMetaverseAttribute = customAttr
            });
            await seed.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            await repository.System.ResetSystemAsync(includeAdministrators: false);
        }

        await using var verify = NewContext();
        var settings = await verify.ServiceSettings
            .Include(ss => ss.SSOUniqueIdentifierMetaverseAttribute)
            .SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.MetaverseAttributes.AnyAsync(a => !a.BuiltIn), Is.False, "the custom attribute must be removed");
            Assert.That(settings.SSOUniqueIdentifierMetaverseAttribute, Is.Null,
                "the Service Settings reference to the removed attribute must be cleared, not left dangling");
        }
    }

    /// <summary>
    /// Seeds one custom row in each configuration category the wipe removes with a <c>WHERE "BuiltIn" = false</c>
    /// delete, each carrying the child rows an administrator-created one really has.
    /// </summary>
    private async Task SeedCustomConfigurationWithChildRowsAsync()
    {
        await using var ctx = NewContext();

        var userType = new MetaverseObjectType { Name = Constants.BuiltInObjectTypes.User, PluralName = "Users", BuiltIn = true };
        var builtInAttr = new MetaverseAttribute
        {
            Name = Constants.BuiltInAttributes.DisplayName,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            BuiltIn = true
        };
        ctx.MetaverseObjectTypes.Add(userType);
        ctx.MetaverseAttributes.Add(builtInAttr);

        // A Predefined Search with a nested criteria group, so both the search -> group and group -> group
        // foreign keys are exercised, plus a criterion under each.
        var search = new PredefinedSearch
        {
            Name = "Contractors",
            Uri = "contractors",
            BuiltIn = false,
            MetaverseObjectType = userType
        };
        search.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = builtInAttr, Position = 0 });
        var topLevelGroup = new PredefinedSearchCriteriaGroup { Type = SearchGroupType.All, Position = 0 };
        topLevelGroup.Criteria.Add(new PredefinedSearchCriteria
        {
            ComparisonType = SearchComparisonType.Equals,
            MetaverseAttribute = builtInAttr,
            StringValue = "Contractor"
        });
        var nestedGroup = new PredefinedSearchCriteriaGroup { Type = SearchGroupType.Any, Position = 0 };
        nestedGroup.Criteria.Add(new PredefinedSearchCriteria
        {
            ComparisonType = SearchComparisonType.StartsWith,
            MetaverseAttribute = builtInAttr,
            StringValue = "C"
        });
        topLevelGroup.ChildGroups.Add(nestedGroup);
        search.CriteriaGroups.Add(topLevelGroup);
        ctx.PredefinedSearches.Add(search);

        // A Connector Definition with settings.
        var connectorDefinition = new ConnectorDefinition { Name = "Contoso Connector", BuiltIn = false };
        connectorDefinition.Settings.Add(new ConnectorDefinitionSetting
        {
            Name = "Hostname",
            Category = ConnectedSystemSettingCategory.Connectivity,
            Type = ConnectedSystemSettingType.String,
            Required = true
        });
        ctx.ConnectorDefinitions.Add(connectorDefinition);

        // An Example Data Set with values.
        var dataSet = new ExampleDataSet { Name = "Custom Surnames", Culture = "en-GB", BuiltIn = false };
        dataSet.Values.Add(new ExampleDataSetValue { StringValue = "Ashworth" });
        ctx.ExampleDataSets.Add(dataSet);

        // An Example Data Template covering an Object Type.
        var template = new ExampleDataTemplate { Name = "Custom Template", BuiltIn = false };
        template.ObjectTypes.Add(new ExampleDataObjectType { MetaverseObjectType = userType, ObjectsToCreate = 10 });
        ctx.ExampleDataTemplates.Add(template);

        ctx.Add(new ServiceSettings { IsServiceInMaintenanceMode = false });

        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// A count per built-in configuration category. Counted straight from the tables rather than through repository
    /// queries so no assertion depends on which navigations a given retrieval method eager-loads.
    /// </summary>
    private sealed record BuiltInCounts(
        int ObjectTypes,
        int Attributes,
        int PredefinedSearches,
        int PredefinedSearchCriteriaGroups,
        int ExampleDataSets,
        int ExampleDataTemplateAttributes,
        int ConnectorDefinitions,
        int Schedules,
        int Roles,
        int ServiceSettings);

    private async Task<BuiltInCounts> CountBuiltInsAsync()
    {
        await using var ctx = NewContext();
        return new BuiltInCounts(
            await ctx.MetaverseObjectTypes.CountAsync(t => t.BuiltIn),
            await ctx.MetaverseAttributes.CountAsync(a => a.BuiltIn),
            await ctx.PredefinedSearches.CountAsync(s => s.BuiltIn),
            await ctx.PredefinedSearchCriteriaGroups.CountAsync(),
            await ctx.ExampleDataSets.CountAsync(s => s.BuiltIn),
            await ctx.ExampleDataTemplateAttributes.CountAsync(),
            await ctx.ConnectorDefinitions.CountAsync(d => d.BuiltIn),
            await ctx.Schedules.CountAsync(s => s.BuiltIn),
            await ctx.Roles.CountAsync(r => r.BuiltIn),
            await ctx.ServiceSettingItems.CountAsync());
    }
}
