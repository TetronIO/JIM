// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of <see cref="JIM.PostgresData.Repositories.ConnectedSystemRepository.DeleteConnectedSystemAsync"/>.
/// The deletion is raw SQL, so the EF Core in-memory provider cannot catch column-name regressions; this fixture
/// runs it against a real database. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as
/// <see cref="SystemResetDatabaseTests"/>; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </summary>
/// <remarks>
/// Regression guard: the SyncRuleMapping schema consolidated onto a single <c>SyncRuleId</c> foreign key, but the
/// deletion SQL still referenced the removed <c>AttributeFlowSynchronisationRuleId</c> /
/// <c>ObjectMatchingSynchronisationRuleId</c> columns, throwing PostgreSQL 42703 (undefined column) for every
/// connected-system deletion (the offending statement is parsed regardless of whether any Synchronisation Rules exist).
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemDeletionDatabaseTests
{
    private string _connectionString = null!;

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL connected-system deletion tests.");

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
    public async Task DeleteConnectedSystemAsync_WithNoSyncRules_RemovesTheSystemAsync()
    {
        int systemId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Empty System", ConnectorDefinition = connectorDefinition };
            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            await seed.SaveChangesAsync();
            systemId = system.Id;
        }

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            // Before the fix this throws PostgresException 42703 at the SyncRuleMappingSourceParamValues step.
            await repository.ConnectedSystems.DeleteConnectedSystemAsync(systemId, deleteChangeHistory: true);
        }

        await using var verify = NewContext();
        Assert.That(await verify.ConnectedSystems.AnyAsync(), Is.False);
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_WithSyncRuleAndMapping_RemovesTheWholeGraphAsync()
    {
        int systemId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Mapped System", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system };
            var mvType = new JIM.Models.Core.MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };

            var rule = new SyncRule
            {
                Name = "Import Rule",
                ConnectedSystem = system,
                ConnectedSystemObjectType = csType,
                MetaverseObjectType = mvType,
                Direction = SyncRuleDirection.Import,
                Enabled = true
            };
            var mapping = new SyncRuleMapping { SyncRule = rule };
            mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "\"literal\"" });

            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            seed.ConnectedSystemObjectTypes.Add(csType);
            seed.MetaverseObjectTypes.Add(mvType);
            seed.SyncRules.Add(rule);
            seed.Add(mapping);
            await seed.SaveChangesAsync();
            systemId = system.Id;
        }

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            await repository.ConnectedSystems.DeleteConnectedSystemAsync(systemId, deleteChangeHistory: true);
        }

        await using var verify = NewContext();
        Assert.That(await verify.ConnectedSystems.AnyAsync(), Is.False, "Connected System should be removed.");
        Assert.That(await verify.SyncRules.AnyAsync(), Is.False, "Synchronisation Rules should be removed.");
        Assert.That(await verify.SyncRuleMappings.AnyAsync(), Is.False, "Synchronisation Rule mappings should be removed.");
        Assert.That(await verify.SyncRuleMappingSources.AnyAsync(), Is.False, "Synchronisation Rule mapping sources should be removed.");
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_WithObjectMatchingRule_RemovesTheWholeGraphAsync()
    {
        int systemId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Matched System", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system };
            var mvType = new JIM.Models.Core.MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };

            var rule = new SyncRule
            {
                Name = "Import Rule",
                ConnectedSystem = system,
                ConnectedSystemObjectType = csType,
                MetaverseObjectType = mvType,
                Direction = SyncRuleDirection.Import,
                Enabled = true
            };

            // An Object Matching Rule referencing both the system's object type and its Synchronisation Rule, with a
            // source and a source parameter value (the OMR source graph cascades from the rule).
            var omr = new ObjectMatchingRule
            {
                Order = 0,
                ConnectedSystemObjectType = csType,
                SyncRule = rule,
                MetaverseObjectType = mvType
            };
            var omrSource = new ObjectMatchingRuleSource { Order = 0, Expression = "\"literal\"" };
            omr.Sources.Add(omrSource);

            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            seed.ConnectedSystemObjectTypes.Add(csType);
            seed.MetaverseObjectTypes.Add(mvType);
            seed.SyncRules.Add(rule);
            seed.ObjectMatchingRules.Add(omr);
            await seed.SaveChangesAsync();
            systemId = system.Id;
        }

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            await repository.ConnectedSystems.DeleteConnectedSystemAsync(systemId, deleteChangeHistory: true);
        }

        await using var verify = NewContext();
        Assert.That(await verify.ConnectedSystems.AnyAsync(), Is.False, "Connected System should be removed.");
        Assert.That(await verify.SyncRules.AnyAsync(), Is.False, "Synchronisation Rules should be removed.");
        Assert.That(await verify.ObjectMatchingRules.AnyAsync(), Is.False, "Object Matching Rules should be removed.");
        Assert.That(await verify.ObjectMatchingRuleSources.AnyAsync(), Is.False, "Object Matching Rule sources should be removed.");
    }

    /// <summary>
    /// Identifiers captured from <see cref="SeedFullGraphAsync"/> so the deletion tests can assert both removal
    /// of the connected-system graph and foreign-key null-out on retained audit rows.
    /// </summary>
    private sealed class FullGraphIds
    {
        public int SystemId { get; init; }
        public Guid ContributedMvavId { get; init; }
        public Guid UnresolvedMvavId { get; init; }
        public Guid MvoChangeId { get; init; }
        public Guid CsoChangeId { get; init; }
        public Guid ActivityId { get; init; }
    }

    /// <summary>
    /// Seeds a complete connected-system dependency graph: partition, container, Run Profile, object type and
    /// attribute, Synchronisation Rule (+ mapping + source), a CSO (+ attribute value), a Metaverse Object with one attribute
    /// value contributed by the system and one unresolved reference to a CSO, a Metaverse Object change, a
    /// connected-system object change (+ attribute + value) and an activity. Every inbound foreign key the deletion
    /// must null or reorder is populated, so the fixture exercises the full delete path an empty or sync-rules-only
    /// seed cannot reach. This is the graph that reproduces the partition / run-profile FK violation and the
    /// metaverse-contribution FK violations from issue context.
    /// </summary>
    private async Task<FullGraphIds> SeedFullGraphAsync()
    {
        await using var seed = NewContext();

        // --- Phase 1: Connected System, schema, partition, container, Synchronisation Rule graph ---
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Full Graph System", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var csAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "accountName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text, Selected = true, IsExternalId = true
        };
        csType.Attributes.Add(csAttr);

        var mvType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        var mvTextAttr = new MetaverseAttribute { Name = "accountName", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        var mvRefAttr = new MetaverseAttribute { Name = "manager", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };

        var partition = new ConnectedSystemPartition { ConnectedSystem = system, Name = "DC=test", ExternalId = "DC=test", Selected = true };
        var container = new ConnectedSystemContainer { Partition = partition, Name = "OU=Users", ExternalId = "OU=Users,DC=test", Selected = true };

        var rule = new SyncRule
        {
            Name = "Import Rule",
            ConnectedSystem = system,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvType,
            Direction = SyncRuleDirection.Import,
            Enabled = true
        };
        var mapping = new SyncRuleMapping { SyncRule = rule };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "\"literal\"" });

        seed.AddRange(connectorDefinition, system, csType, mvType, mvTextAttr, mvRefAttr, partition, container, rule, mapping);
        await seed.SaveChangesAsync();

        // --- Phase 2: Run Profile, CSO graph, Metaverse Object + values, change records ---
        var runProfile = new ConnectedSystemRunProfile
        {
            Name = "Full Import",
            ConnectedSystemId = system.Id,
            Partition = partition,
            RunType = ConnectedSystemRunType.FullImport
        };

        var cso = new ConnectedSystemObject
        {
            Type = csType,
            ConnectedSystem = system,
            Partition = partition,
            ExternalIdAttributeId = csAttr.Id,
            Status = ConnectedSystemObjectStatus.Normal
        };
        var csoAttrValue = new ConnectedSystemObjectAttributeValue { Attribute = csAttr, StringValue = "jdoe", ConnectedSystemObject = cso };
        cso.AttributeValues.Add(csoAttrValue);

        var mvo = new MetaverseObject { Type = mvType };
        var contributedMvav = new MetaverseObjectAttributeValue { Attribute = mvTextAttr, StringValue = "jdoe", MetaverseObject = mvo, ContributedBySystem = system };
        var unresolvedMvav = new MetaverseObjectAttributeValue { Attribute = mvRefAttr, MetaverseObject = mvo, UnresolvedReferenceValue = cso };
        mvo.AttributeValues.Add(contributedMvav);
        mvo.AttributeValues.Add(unresolvedMvav);

        var mvoChange = new MetaverseObjectChange
        {
            MetaverseObject = mvo,
            ChangeTime = DateTime.UtcNow,
            ChangeInitiatorType = MetaverseObjectChangeInitiatorType.SynchronisationRule,
            ChangeType = ObjectChangeType.Updated,
            SyncRule = rule,
            SyncRuleName = rule.Name
        };

        var csoChange = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = system.Id,
            ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Updated,
            DeletedObjectType = csType,
            DeletedObjectExternalIdAttributeValue = csoAttrValue,
            ConnectedSystemObject = cso
        };
        var csoChangeAttr = new ConnectedSystemObjectChangeAttribute
        {
            ConnectedSystemChange = csoChange,
            Attribute = csAttr,
            AttributeName = "accountName",
            AttributeType = AttributeDataType.Text
        };
        csoChangeAttr.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue
        {
            ConnectedSystemObjectChangeAttribute = csoChangeAttr,
            ValueChangeType = ValueChangeType.Add,
            StringValue = "jdoe",
            ReferenceValue = cso
        });
        csoChange.AttributeChanges.Add(csoChangeAttr);

        seed.AddRange(runProfile, cso, mvo, mvoChange, csoChange);
        await seed.SaveChangesAsync();

        // --- Phase 3: activity referencing the now-persisted Run Profile and Synchronisation Rule (scalar FKs) ---
        var activity = new Activity
        {
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Delete,
            TargetName = system.Name,
            Status = ActivityStatus.Complete,
            Executed = DateTime.UtcNow,
            InitiatedByType = ActivityInitiatorType.System,
            ConnectedSystemId = system.Id,
            ConnectedSystemRunProfileId = runProfile.Id,
            SyncRuleId = rule.Id
        };
        seed.Add(activity);
        await seed.SaveChangesAsync();

        return new FullGraphIds
        {
            SystemId = system.Id,
            ContributedMvavId = contributedMvav.Id,
            UnresolvedMvavId = unresolvedMvav.Id,
            MvoChangeId = mvoChange.Id,
            CsoChangeId = csoChange.Id,
            ActivityId = activity.Id
        };
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_FullGraph_DeleteChangeHistory_RemovesSystemAndNullsAuditFksAsync()
    {
        var ids = await SeedFullGraphAsync();

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            // Before the fix this throws PostgresException 23503 (foreign key violation): deleting the CSO breaches
            // MetaverseObjectAttributeValues.UnresolvedReferenceValueId, and deleting partitions breaches
            // ConnectedSystemRunProfiles.PartitionId.
            await repository.ConnectedSystems.DeleteConnectedSystemAsync(ids.SystemId, deleteChangeHistory: true);
        }

        await using var verify = NewContext();

        // Connected-system graph fully removed.
        Assert.That(await verify.ConnectedSystems.AnyAsync(), Is.False, "Connected System should be removed.");
        Assert.That(await verify.SyncRules.AnyAsync(), Is.False, "Synchronisation Rules should be removed.");
        Assert.That(await verify.ConnectedSystemRunProfiles.AnyAsync(), Is.False, "Run Profiles should be removed.");
        Assert.That(await verify.ConnectedSystemPartitions.AnyAsync(), Is.False, "Partitions should be removed.");
        Assert.That(await verify.ConnectedSystemObjects.AnyAsync(), Is.False, "CSOs should be removed.");
        Assert.That(await verify.ConnectedSystemObjectChanges.AnyAsync(), Is.False, "Change history should be removed when deleteChangeHistory is true.");

        // Retained audit rows survive with their now-dead foreign keys nulled.
        var activity = await verify.Activities.SingleAsync(a => a.Id == ids.ActivityId);
        Assert.That(activity.ConnectedSystemId, Is.Null, "Activity ConnectedSystemId should be nulled.");
        Assert.That(activity.ConnectedSystemRunProfileId, Is.Null, "Activity ConnectedSystemRunProfileId should be nulled.");
        Assert.That(activity.SyncRuleId, Is.Null, "Activity SyncRuleId should be nulled.");

        Assert.That(await verify.MetaverseObjects.AnyAsync(), Is.True, "Surviving Metaverse Object should be retained.");
        var contributedMvav = await verify.MetaverseObjectAttributeValues.SingleAsync(av => av.Id == ids.ContributedMvavId);
        Assert.That(contributedMvav.ContributedBySystemId, Is.Null, "Contributed metaverse attribute value should keep the value but null the contributor.");
        var unresolvedMvav = await verify.MetaverseObjectAttributeValues.SingleAsync(av => av.Id == ids.UnresolvedMvavId);
        Assert.That(unresolvedMvav.UnresolvedReferenceValueId, Is.Null, "Unresolved reference to a deleted CSO should be nulled.");

        var mvoChange = await verify.MetaverseObjectChanges.SingleAsync(c => c.Id == ids.MvoChangeId);
        Assert.That(mvoChange.SyncRuleId, Is.Null, "Metaverse Object change should keep the record but null the deleted Synchronisation Rule FK.");
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_FullGraph_PreserveChangeHistory_RetainsChangesWithNulledFksAsync()
    {
        var ids = await SeedFullGraphAsync();

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            await repository.ConnectedSystems.DeleteConnectedSystemAsync(ids.SystemId, deleteChangeHistory: false);
        }

        await using var verify = NewContext();

        Assert.That(await verify.ConnectedSystems.AnyAsync(), Is.False, "Connected System should be removed.");
        Assert.That(await verify.SyncRules.AnyAsync(), Is.False, "Synchronisation Rules should be removed.");
        Assert.That(await verify.ConnectedSystemObjects.AnyAsync(), Is.False, "CSOs should be removed.");
        Assert.That(await verify.ConnectedSystemObjectTypes.AnyAsync(), Is.False, "Object types should be removed.");

        // Change history is retained, with foreign keys to the now-deleted schema/objects nulled.
        // DeletedObjectTypeId / DeletedObjectExternalIdAttributeValueId / AttributeId / ReferenceValueId are shadow
        // FKs (no CLR property), so they are read via EF.Property projections which work under NoTracking.
        Assert.That(await verify.ConnectedSystemObjectChanges.AnyAsync(c => c.Id == ids.CsoChangeId), Is.True,
            "Change history should be retained when deleteChangeHistory is false.");

        var deletedObjectTypeId = await verify.ConnectedSystemObjectChanges
            .Where(c => c.Id == ids.CsoChangeId)
            .Select(c => EF.Property<int?>(c, "DeletedObjectTypeId"))
            .SingleAsync();
        Assert.That(deletedObjectTypeId, Is.Null, "Retained change should null its deleted-object-type FK.");

        var deletedExternalIdAttributeValueId = await verify.ConnectedSystemObjectChanges
            .Where(c => c.Id == ids.CsoChangeId)
            .Select(c => EF.Property<Guid?>(c, "DeletedObjectExternalIdAttributeValueId"))
            .SingleAsync();
        Assert.That(deletedExternalIdAttributeValueId, Is.Null, "Retained change should null its deleted-attribute-value FK.");

        var csoChange = await verify.ConnectedSystemObjectChanges.SingleAsync(c => c.Id == ids.CsoChangeId);
        Assert.That(csoChange.ConnectedSystemObjectId, Is.Null, "Retained change should null its CSO FK (SetNull cascade).");

        var changeAttributeFk = await verify.ConnectedSystemObjectChangeAttributes
            .Select(a => EF.Property<int?>(a, "AttributeId"))
            .SingleAsync();
        Assert.That(changeAttributeFk, Is.Null, "Retained change attribute should null its attribute FK (SetNull cascade).");

        var changeValueReferenceFk = await verify.ConnectedSystemObjectChangeAttributeValues
            .Select(v => EF.Property<Guid?>(v, "ReferenceValueId"))
            .SingleAsync();
        Assert.That(changeValueReferenceFk, Is.Null, "Retained change value should null its CSO reference FK (SetNull cascade).");

        // Same audit-preservation behaviour as the delete-history path.
        var activity = await verify.Activities.SingleAsync(a => a.Id == ids.ActivityId);
        Assert.That(activity.ConnectedSystemRunProfileId, Is.Null, "Activity ConnectedSystemRunProfileId should be nulled.");
        Assert.That(activity.SyncRuleId, Is.Null, "Activity SyncRuleId should be nulled.");
        var contributedMvav = await verify.MetaverseObjectAttributeValues.SingleAsync(av => av.Id == ids.ContributedMvavId);
        Assert.That(contributedMvav.ContributedBySystemId, Is.Null, "Contributed metaverse attribute value should null the contributor.");
    }
}
