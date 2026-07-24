// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that the bulk RPEI insert persists <c>PendingExportId</c>. The raw-SQL
/// insert paths (single-connection parameterised INSERT and the parallel COPY) originally omitted the
/// column, so every reference-recall RPEI landed with a NULL <c>PendingExportId</c> (confirmed on the
/// Scale500k25kGroups run: 21,824 recall RPEIs, all NULL), breaking the operations-tab drill-down that
/// loads the Pending Export by id. The in-memory provider stores the object graph verbatim and so
/// cannot catch a missing column in the hand-written SQL; this fixture is the regression guard the
/// raw-SQL-write rules require. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class RpeiPendingExportIdPersistenceDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL RPEI PendingExportId persistence tests.");

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
    public async Task BulkInsertRpeisAsync_RecallRpeiWithPendingExportId_PersistsTheColumnAsync()
    {
        var activityId = await SeedActivityAsync();
        var pendingExportId = Guid.NewGuid();

        // ConnectedSystemObjectId is left null to avoid seeding a full CSO graph: it is a foreign key,
        // whereas PendingExportId (the column under test) is a loose Guid with no FK constraint.
        var withPendingExport = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.PendingExport,
            PendingExportId = pendingExportId,
            DisplayNameSnapshot = "Team Alpha",
            ExternalIdSnapshot = "cn=Team Alpha,ou=Groups,dc=corp",
            ObjectTypeSnapshot = "group"
        };
        // A non-recall RPEI without a Pending Export must still round-trip a NULL PendingExportId.
        var withoutPendingExport = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.DisconnectedOutOfScope,
            DisplayNameSnapshot = "Some User"
        };

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            await repository.Sync.BulkInsertRpeisAsync([withPendingExport, withoutPendingExport]);
        }

        await using var verifyCtx = NewContext();
        var persistedWith = await verifyCtx.ActivityRunProfileExecutionItems
            .AsNoTracking().SingleAsync(r => r.Id == withPendingExport.Id);
        var persistedWithout = await verifyCtx.ActivityRunProfileExecutionItems
            .AsNoTracking().SingleAsync(r => r.Id == withoutPendingExport.Id);

        Assert.That(persistedWith.PendingExportId, Is.EqualTo(pendingExportId),
            "The recall RPEI's PendingExportId must be persisted so the operations tab can load the Pending Export");
        Assert.That(persistedWith.ExternalIdSnapshot, Is.EqualTo("cn=Team Alpha,ou=Groups,dc=corp"));
        Assert.That(persistedWith.ObjectTypeSnapshot, Is.EqualTo("group"));
        Assert.That(persistedWithout.PendingExportId, Is.Null,
            "An RPEI with no Pending Export must persist a NULL PendingExportId, not a spurious value");
    }

    /// <summary>
    /// Verifies the end-of-run recall snapshot lookup (<c>GetConnectedSystemObjectDisplaySnapshotsAsync</c>)
    /// translates on real PostgreSQL and returns the external ID and object type without materialising the
    /// attribute-value collection. The correlated single-column scalar subqueries must not degrade into a
    /// whole-table window function (the trap that made an earlier operations-tab query time out), so this
    /// also guards that the query stays cheap as membership grows.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectDisplaySnapshotsAsync_ReturnsExternalIdAndTypeAsync()
    {
        Guid groupCsoId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Glitterband LDAP", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };
            var dnAttribute = new ConnectedSystemObjectTypeAttribute
            {
                Name = "distinguishedName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
                IsExternalId = true, Selected = true
            };
            csType.Attributes.Add(dnAttribute);
            seed.AddRange(connectorDefinition, system, csType);
            await seed.SaveChangesAsync();

            var groupCso = new ConnectedSystemObject
            {
                Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
                ExternalIdAttributeId = dnAttribute.Id
            };
            groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                ConnectedSystemObject = groupCso,
                Attribute = dnAttribute,
                StringValue = "cn=Team Alpha,ou=Groups,dc=corp"
            });
            seed.Add(groupCso);
            await seed.SaveChangesAsync();
            groupCsoId = groupCso.Id;
        }

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var snapshots = await repository.Sync.GetConnectedSystemObjectDisplaySnapshotsAsync([groupCsoId]);

        Assert.That(snapshots, Contains.Key(groupCsoId));
        Assert.That(snapshots[groupCsoId].ExternalId, Is.EqualTo("cn=Team Alpha,ou=Groups,dc=corp"));
        Assert.That(snapshots[groupCsoId].TypeName, Is.EqualTo("group"));
    }

    private async Task<Guid> SeedActivityAsync()
    {
        await using var ctx = NewContext();
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetName = "Full Sync",
            TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.Complete,
            InitiatedByType = ActivityInitiatorType.System
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        return activity.Id;
    }
}
