// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL round-trip checks that attribute value changes record the Synchronisation Rule
/// that contributed each value (#1519), across the three raw-SQL write paths that bypass the EF
/// model: Metaverse Object change history, Connected System Object (export) change history, and
/// Pending Export attribute value changes. The in-memory provider cannot see raw-SQL column drift,
/// so these must run against real PostgreSQL.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed
/// tests; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class AttributeValueChangeSyncRuleAttributionDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL attribute value change Synchronisation Rule attribution tests.");

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

    /// <summary>
    /// Seeds a minimal Connected System / Metaverse Object Type / Synchronisation Rule graph so a
    /// value change can carry a real, FK-satisfying <c>ContributedBySyncRuleId</c>.
    /// </summary>
    private async Task<(MetaverseObjectType MvType, MetaverseAttribute Attribute, SyncRule Rule)> SeedMvoTypeAndSyncRuleAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = $"Connector-{Guid.NewGuid():N}", BuiltIn = false };
        var system = new ConnectedSystem { Name = $"System-{Guid.NewGuid():N}", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var mvType = new MetaverseObjectType { Name = $"Person-{Guid.NewGuid():N}", PluralName = "People" };
        var attribute = new MetaverseAttribute { Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        mvType.Attributes.Add(attribute);
        seed.AddRange(connectorDefinition, system, csType, mvType);
        await seed.SaveChangesAsync();

        var rule = new SyncRule
        {
            Name = "HR to AD - Users",
            ConnectedSystem = system,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvType,
            Direction = SyncRuleDirection.Import
        };
        seed.Add(rule);
        await seed.SaveChangesAsync();

        return (mvType, attribute, rule);
    }

    [Test]
    public async Task PersistPendingMvoChangesAsync_ValueChangeCarriesContributedBySyncRule_RoundTripsIdAndNameAsync()
    {
        // Arrange
        var (mvType, attribute, rule) = await SeedMvoTypeAndSyncRuleAsync();

        Guid mvoId;
        await using (var seed = NewContext())
        {
            // mvType was loaded/saved on a different, now-disposed context, so it and its
            // Attributes are untracked here; attach it first so Add(mvo) below does not walk the
            // graph and try to re-insert the already-persisted type and attribute (src/CLAUDE.md,
            // "DbSet.Add Walks the Graph; Entry() Does Not").
            seed.Attach(mvType);
            var mvo = new MetaverseObject { Type = mvType, Created = DateTime.UtcNow };
            seed.Add(mvo);
            await seed.SaveChangesAsync();
            mvoId = mvo.Id;
        }

        var change = new MetaverseObjectChange
        {
            MetaverseObject = new MetaverseObject { Id = mvoId },
            ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Updated,
            ChangeInitiatorType = MetaverseObjectChangeInitiatorType.SynchronisationRule
        };
        var attributeChange = new MetaverseObjectChangeAttribute
        {
            Attribute = attribute,
            AttributeName = attribute.Name,
            AttributeType = attribute.Type,
            MetaverseObjectChange = change
        };
        change.AttributeChanges.Add(attributeChange);
        attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
            attributeChange, ValueChangeType.Add, "jsmith@example.com")
        {
            ContributedBySyncRuleId = rule.Id,
            ContributedBySyncRuleName = rule.Name
        });

        // Act
        await using (var writeCtx = NewContext())
        {
            var repository = new PostgresDataRepository(writeCtx);
            await repository.Sync.PersistPendingMvoChangesAsync([change], []);
        }

        // Assert
        await using var verify = NewContext();
        var persisted = await verify.MetaverseObjectChangeAttributeValues
            .AsNoTracking()
            .SingleAsync(v => v.MetaverseObjectChangeAttribute.MetaverseObjectChange.MetaverseObject!.Id == mvoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.ContributedBySyncRuleId, Is.EqualTo(rule.Id));
            Assert.That(persisted.ContributedBySyncRuleName, Is.EqualTo("HR to AD - Users"));
        }
    }

    [Test]
    public async Task PersistPendingMvoChangesAsync_ContributingSyncRuleLaterDeleted_NameSurvivesIdReadsNullAsync()
    {
        // Arrange
        var (mvType, attribute, rule) = await SeedMvoTypeAndSyncRuleAsync();

        Guid mvoId;
        await using (var seed = NewContext())
        {
            // mvType was loaded/saved on a different, now-disposed context, so it and its
            // Attributes are untracked here; attach it first so Add(mvo) below does not walk the
            // graph and try to re-insert the already-persisted type and attribute (src/CLAUDE.md,
            // "DbSet.Add Walks the Graph; Entry() Does Not").
            seed.Attach(mvType);
            var mvo = new MetaverseObject { Type = mvType, Created = DateTime.UtcNow };
            seed.Add(mvo);
            await seed.SaveChangesAsync();
            mvoId = mvo.Id;
        }

        var change = new MetaverseObjectChange
        {
            MetaverseObject = new MetaverseObject { Id = mvoId },
            ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Updated,
            ChangeInitiatorType = MetaverseObjectChangeInitiatorType.SynchronisationRule
        };
        var attributeChange = new MetaverseObjectChangeAttribute
        {
            Attribute = attribute,
            AttributeName = attribute.Name,
            AttributeType = attribute.Type,
            MetaverseObjectChange = change
        };
        change.AttributeChanges.Add(attributeChange);
        attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
            attributeChange, ValueChangeType.Add, "jsmith@example.com")
        {
            ContributedBySyncRuleId = rule.Id,
            ContributedBySyncRuleName = rule.Name
        });

        await using (var writeCtx = NewContext())
        {
            var repository = new PostgresDataRepository(writeCtx);
            await repository.Sync.PersistPendingMvoChangesAsync([change], []);
        }

        // Act - delete the contributing Synchronisation Rule
        await using (var deleteCtx = NewContext())
        {
            await deleteCtx.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""SyncRules"" WHERE ""Id"" = {rule.Id}");
        }

        // Assert - the FK's SetNull behaviour clears the id at the database level; the denormalised
        // name is unaffected because it is a plain column, not a foreign key.
        await using var verify = NewContext();
        var persisted = await verify.MetaverseObjectChangeAttributeValues
            .AsNoTracking()
            .SingleAsync(v => v.MetaverseObjectChangeAttribute.MetaverseObjectChange.MetaverseObject!.Id == mvoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.ContributedBySyncRuleId, Is.Null);
            Assert.That(persisted.ContributedBySyncRuleName, Is.EqualTo("HR to AD - Users"));
        }
    }

    [Test]
    public async Task PersistRpeiCsoChangesAsync_ValueChangeCarriesSyncRuleAttribution_RoundTripsIdAndNameAsync()
    {
        // Arrange
        var connectorDefinition = new ConnectorDefinition { Name = $"Connector-{Guid.NewGuid():N}", BuiltIn = false };
        var system = new ConnectedSystem { Name = $"System-{Guid.NewGuid():N}", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var mailAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "mail", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(mailAttribute);

        await using (var seed = NewContext())
        {
            seed.AddRange(connectorDefinition, system, csType);
            await seed.SaveChangesAsync();
        }

        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = system.Id,
            ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Exported,
            InitiatedByType = ActivityInitiatorType.System
        };
        var attributeChange = new ConnectedSystemObjectChangeAttribute
        {
            Attribute = mailAttribute,
            AttributeName = mailAttribute.Name,
            AttributeType = mailAttribute.Type,
            ConnectedSystemChange = change
        };
        change.AttributeChanges.Add(attributeChange);
        attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(
            attributeChange, ValueChangeType.Add, "jsmith@example.com")
        {
            SyncRuleId = 999,
            SyncRuleName = "HR to AD - Users"
        });

        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Exported,
            ConnectedSystemObjectChange = change
        };

        // Act
        await using (var writeCtx = NewContext())
        {
            var repository = new PostgresDataRepository(writeCtx);
            await repository.Sync.PersistRpeiCsoChangesAsync([rpei]);
        }

        // Assert
        await using var verify = NewContext();
        var persisted = await verify.ConnectedSystemObjectChangeAttributeValues
            .AsNoTracking()
            .SingleAsync(v => v.ConnectedSystemObjectChangeAttribute.ConnectedSystemChange.Id == change.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.SyncRuleId, Is.EqualTo(999));
            Assert.That(persisted.SyncRuleName, Is.EqualTo("HR to AD - Users"));
        }
    }

    [Test]
    public async Task CreatePendingExportsAsync_AttributeValueChangeCarriesSyncRuleAttribution_RoundTripsIdAndNameAsync()
    {
        // Arrange
        var connectorDefinition = new ConnectorDefinition { Name = $"Connector-{Guid.NewGuid():N}", BuiltIn = false };
        var system = new ConnectedSystem { Name = $"System-{Guid.NewGuid():N}", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var mailAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "mail", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(mailAttribute);

        await using (var seed = NewContext())
        {
            seed.AddRange(connectorDefinition, system, csType);
            await seed.SaveChangesAsync();
        }

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = mailAttribute.Id,
            Attribute = mailAttribute,
            StringValue = "jsmith@example.com",
            ChangeType = PendingExportAttributeChangeType.Update,
            SyncRuleId = 999,
            SyncRuleName = "HR to AD - Users"
        });

        // Act
        await using (var writeCtx = NewContext())
        {
            var repository = new PostgresDataRepository(writeCtx);
            await repository.Sync.CreatePendingExportsAsync([pendingExport]);
        }

        // Assert
        await using var verify = NewContext();
        var persisted = await verify.PendingExportAttributeValueChanges
            .AsNoTracking()
            .SingleAsync(v => v.PendingExportId == pendingExport.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.SyncRuleId, Is.EqualTo(999));
            Assert.That(persisted.SyncRuleName, Is.EqualTo("HR to AD - Users"));
        }
    }

    /// <summary>
    /// The read path behind <c>GET /api/v1/metaverse/objects/{id}/change-history</c> (and
    /// Get-JIMMetaverseObjectChangeHistory): the EF projection into <see cref="MvoValueChangeDto"/>
    /// must carry the contributor fields through, not just the raw-SQL write path.
    /// </summary>
    [Test]
    public async Task GetMvoChangeHistoryAsync_ValueHasContributingSyncRule_DtoCarriesIdAndNameAsync()
    {
        // Arrange
        var (mvType, attribute, rule) = await SeedMvoTypeAndSyncRuleAsync();

        Guid mvoId;
        await using (var seed = NewContext())
        {
            seed.Attach(mvType);
            var mvo = new MetaverseObject { Type = mvType, Created = DateTime.UtcNow };
            seed.Add(mvo);
            await seed.SaveChangesAsync();
            mvoId = mvo.Id;
        }

        var change = new MetaverseObjectChange
        {
            MetaverseObject = new MetaverseObject { Id = mvoId },
            ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Updated,
            ChangeInitiatorType = MetaverseObjectChangeInitiatorType.SynchronisationRule
        };
        var attributeChange = new MetaverseObjectChangeAttribute
        {
            Attribute = attribute,
            AttributeName = attribute.Name,
            AttributeType = attribute.Type,
            MetaverseObjectChange = change
        };
        change.AttributeChanges.Add(attributeChange);
        attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
            attributeChange, ValueChangeType.Add, "jsmith@example.com")
        {
            ContributedBySyncRuleId = rule.Id,
            ContributedBySyncRuleName = rule.Name
        });

        await using (var writeCtx = NewContext())
        {
            var repository = new PostgresDataRepository(writeCtx);
            await repository.Sync.PersistPendingMvoChangesAsync([change], []);
        }

        // Act
        await using var readCtx = NewContext();
        var readRepository = new PostgresDataRepository(readCtx);
        var (items, totalCount) = await readRepository.Metaverse.GetMvoChangeHistoryAsync(mvoId, 1, 10);

        // Assert
        var valueChange = items.Single().AttributeChanges.Single().ValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(totalCount, Is.EqualTo(1));
            Assert.That(valueChange.ContributedBySyncRuleId, Is.EqualTo(rule.Id));
            Assert.That(valueChange.ContributedBySyncRuleName, Is.EqualTo("HR to AD - Users"));
        }
    }

    /// <summary>
    /// The read path behind <c>GET .../connector-space/{csoId}/change-history</c> (and
    /// Get-JIMConnectedSystemObjectChangeHistory): the EF projection into <see cref="CsoValueChangeDto"/>
    /// must carry the export rule attribution through.
    /// </summary>
    [Test]
    public async Task GetCsoChangeHistoryAsync_ValueHasSyncRuleAttribution_DtoCarriesIdAndNameAsync()
    {
        // Arrange
        var connectorDefinition = new ConnectorDefinition { Name = $"Connector-{Guid.NewGuid():N}", BuiltIn = false };
        var system = new ConnectedSystem { Name = $"System-{Guid.NewGuid():N}", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var mailAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "mail", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(mailAttribute);

        Guid csoId;
        await using (var seed = NewContext())
        {
            seed.AddRange(connectorDefinition, system, csType);
            await seed.SaveChangesAsync();

            var cso = new ConnectedSystemObject
            {
                Type = csType,
                ConnectedSystem = system,
                Status = ConnectedSystemObjectStatus.Normal,
                JoinType = ConnectedSystemObjectJoinType.NotJoined,
                ExternalIdAttributeId = mailAttribute.Id
            };
            seed.Add(cso);
            await seed.SaveChangesAsync();
            csoId = cso.Id;
        }

        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = system.Id,
            ConnectedSystemObjectId = csoId,
            ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Exported,
            InitiatedByType = ActivityInitiatorType.System
        };
        var attributeChange = new ConnectedSystemObjectChangeAttribute
        {
            Attribute = mailAttribute,
            AttributeName = mailAttribute.Name,
            AttributeType = mailAttribute.Type,
            ConnectedSystemChange = change
        };
        change.AttributeChanges.Add(attributeChange);
        attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(
            attributeChange, ValueChangeType.Add, "jsmith@example.com")
        {
            SyncRuleId = 999,
            SyncRuleName = "HR to AD - Users"
        });

        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Exported,
            ConnectedSystemObjectChange = change
        };

        await using (var writeCtx = NewContext())
        {
            var repository = new PostgresDataRepository(writeCtx);
            await repository.Sync.PersistRpeiCsoChangesAsync([rpei]);
        }

        // Act
        await using var readCtx = NewContext();
        var readRepository = new PostgresDataRepository(readCtx);
        var (items, totalCount) = await readRepository.ConnectedSystems.GetCsoChangeHistoryAsync(csoId, 1, 10);

        // Assert
        var valueChange = items.Single().AttributeChanges.Single().ValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(totalCount, Is.EqualTo(1));
            Assert.That(valueChange.SyncRuleId, Is.EqualTo(999));
            Assert.That(valueChange.SyncRuleName, Is.EqualTo("HR to AD - Users"));
        }
    }
}
