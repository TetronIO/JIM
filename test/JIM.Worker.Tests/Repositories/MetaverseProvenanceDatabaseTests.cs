// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL checks for the value provenance repository queries (#399): the object summary and attribute
/// history queries group and join in SQL, and the Attribute Priority mapping load Includes what
/// <see cref="SyncRuleMapping.GetSourceType"/> needs. The EF in-memory provider auto-tracks navigation properties
/// and would mask a missing <c>Include</c> or a grouping bug in any of these, so this suite is the only place
/// they are actually proven (<c>test/CLAUDE.md</c>, "EF Core In-Memory Database Limitation").
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseProvenanceDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL value provenance tests.");

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

    /// <summary>
    /// Seeds a Connected System with a "user" object type carrying a "dept" text attribute, a matching Metaverse
    /// Object Type with a "Department" attribute, and an import Synchronisation Rule with an attribute mapping
    /// from "dept" to "Department".
    /// </summary>
    private async Task<(ConnectedSystem System, ConnectedSystemObjectType CsType, ConnectedSystemObjectTypeAttribute CsAttribute,
        MetaverseObjectType MvType, MetaverseAttribute MvAttribute, SyncRule Rule, SyncRuleMapping Mapping)> SeedSchemaAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = $"Connector-{Guid.NewGuid():N}", BuiltIn = false };
        var system = new ConnectedSystem { Name = $"HR-{Guid.NewGuid():N}", ConnectorDefinition = connectorDefinition };
        var csAttribute = new ConnectedSystemObjectTypeAttribute { Name = "dept", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true, Attributes = new List<ConnectedSystemObjectTypeAttribute> { csAttribute } };
        var mvAttribute = new MetaverseAttribute { Name = "Department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var mvType = new MetaverseObjectType { Name = $"Person-{Guid.NewGuid():N}", PluralName = "People", Attributes = new List<MetaverseAttribute> { mvAttribute } };
        seed.AddRange(connectorDefinition, system, csType, mvType);
        await seed.SaveChangesAsync();

        var rule = new SyncRule
        {
            Name = "HR Import",
            ConnectedSystem = system,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvType,
            Direction = SyncRuleDirection.Import,
            Enabled = true
        };
        seed.Add(rule);
        await seed.SaveChangesAsync();

        var mapping = new SyncRuleMapping { SyncRule = rule, SyncRuleId = rule.Id, TargetMetaverseAttribute = mvAttribute, TargetMetaverseAttributeId = mvAttribute.Id, Priority = 1 };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttribute = csAttribute, ConnectedSystemAttributeId = csAttribute.Id, Order = 0 });
        seed.Add(mapping);
        await seed.SaveChangesAsync();

        return (system, csType, csAttribute, mvType, mvAttribute, rule, mapping);
    }

    private static async Task<Guid> SeedMetaverseObjectAsync(JimDbContext ctx, MetaverseObjectType mvType)
    {
        ctx.Attach(mvType);
        var mvo = new MetaverseObject { Type = mvType, Created = DateTime.UtcNow };
        ctx.Add(mvo);
        await ctx.SaveChangesAsync();
        return mvo.Id;
    }

    /// <summary>
    /// A reference to an already-persisted Metaverse Object, attached Unchanged so a child entity added
    /// against it (via a plain <c>Add</c>, which walks the graph) does not try to re-insert the parent
    /// (src/CLAUDE.md, "DbSet.Add Walks the Graph; Entry() Does Not"). Returns the already-tracked instance
    /// when this context has attached it before, since attaching a second stub with the same key throws.
    /// </summary>
    private static MetaverseObject AttachMvoStub(JimDbContext ctx, Guid mvoId)
    {
        var tracked = ctx.ChangeTracker.Entries<MetaverseObject>().FirstOrDefault(e => e.Entity.Id == mvoId);
        if (tracked != null)
            return tracked.Entity;

        var stub = new MetaverseObject { Id = mvoId };
        ctx.Attach(stub);
        return stub;
    }

    #region GetImportSyncRuleMappingsForMetaverseAttributeAsync (Sources/Generation Include)

    [Test]
    public async Task GetImportSyncRuleMappingsForMetaverseAttributeAsync_LoadsSourcesAndConnectedSystemAttributeAsync()
    {
        var (_, _, csAttribute, mvType, mvAttribute, _, _) = await SeedSchemaAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var mappings = await repository.ConnectedSystems.GetImportSyncRuleMappingsForMetaverseAttributeAsync(mvType.Id, mvAttribute.Id);

        var mapping = mappings.Single();
        using (Assert.EnterMultipleScope())
        {
            // Regression: without .Include(m => m.Sources).ThenInclude(s => s.ConnectedSystemAttribute), Sources
            // is empty on a real database (unlike the in-memory provider, which auto-tracks it regardless), and
            // GetSourceType() silently misclassifies every mapping as AdvancedMapping.
            Assert.That(mapping.Sources, Has.Count.EqualTo(1));
            Assert.That(mapping.Sources[0].ConnectedSystemAttribute, Is.Not.Null);
            Assert.That(mapping.Sources[0].ConnectedSystemAttribute!.Id, Is.EqualTo(csAttribute.Id));
            Assert.That(mapping.GetSourceType(), Is.EqualTo(SyncRuleMappingSourcesType.AttributeMapping));
        }
    }

    #endregion

    #region GetMetaverseObjectProvenanceAsync

    [Test]
    public async Task GetMetaverseObjectProvenanceAsync_MetaverseObjectDoesNotExist_ReturnsNullAsync()
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.Metaverse.GetMetaverseObjectProvenanceAsync(Guid.NewGuid());

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetMetaverseObjectProvenanceAsync_GroupsOriginsAndResolvesNamesAsync()
    {
        var (system, _, _, mvType, mvAttribute, rule, _) = await SeedSchemaAsync();

        Guid mvoId;
        await using (var ctx = NewContext())
            mvoId = await SeedMetaverseObjectAsync(ctx, mvType);

        await using (var ctx = NewContext())
        {
            ctx.Attach(mvAttribute);
            ctx.MetaverseObjectAttributeValues.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = AttachMvoStub(ctx, mvoId),
                Attribute = mvAttribute,
                AttributeId = mvAttribute.Id,
                StringValue = "Engineering",
                ContributedBySystemId = system.Id,
                ContributedBySyncRuleId = rule.Id
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = NewContext();
        var repository = new PostgresDataRepository(readCtx);
        var result = await repository.Metaverse.GetMetaverseObjectProvenanceAsync(mvoId);

        Assert.That(result, Is.Not.Null);
        var attributeSummary = result!.Attributes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attributeSummary.AttributeId, Is.EqualTo(mvAttribute.Id));
            Assert.That(attributeSummary.AttributeName, Is.EqualTo("Department"));
            var origin = attributeSummary.Origins.Single();
            Assert.That(origin.Kind, Is.EqualTo(ValueOriginKind.SynchronisationRule));
            Assert.That(origin.ConnectedSystemName, Is.EqualTo(system.Name));
            Assert.That(origin.SyncRuleName, Is.EqualTo("HR Import"));
            Assert.That(origin.SyncRuleDeleted, Is.False);
        }
    }

    [Test]
    public async Task GetMetaverseObjectProvenanceAsync_ContributingRuleDeleted_SystemNameSurvivesRuleIdIsNullAsync()
    {
        var (system, _, _, mvType, mvAttribute, rule, mapping) = await SeedSchemaAsync();

        Guid mvoId;
        await using (var ctx = NewContext())
            mvoId = await SeedMetaverseObjectAsync(ctx, mvType);

        await using (var ctx = NewContext())
        {
            ctx.Attach(mvAttribute);
            ctx.MetaverseObjectAttributeValues.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = AttachMvoStub(ctx, mvoId),
                Attribute = mvAttribute,
                AttributeId = mvAttribute.Id,
                StringValue = "Engineering",
                ContributedBySystemId = system.Id,
                ContributedBySyncRuleId = rule.Id
            });
            await ctx.SaveChangesAsync();
        }

        // Delete the Synchronisation Rule; the model's SetNull FK behaviour nulls ContributedBySyncRuleId
        // while ContributedBySystemId (denormalised) survives.
        await using (var ctx = NewContext())
        {
            ctx.Remove(await ctx.SyncRuleMappings.SingleAsync(m => m.Id == mapping.Id));
            ctx.Remove(await ctx.SyncRules.SingleAsync(r => r.Id == rule.Id));
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = NewContext();
        var repository = new PostgresDataRepository(readCtx);
        var result = await repository.Metaverse.GetMetaverseObjectProvenanceAsync(mvoId);

        var origin = result!.Attributes.Single().Origins.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(origin.ConnectedSystemName, Is.EqualTo(system.Name));
            Assert.That(origin.SyncRuleId, Is.Null);
            Assert.That(origin.SyncRuleDeleted, Is.True);
        }
    }

    [Test]
    public async Task GetMetaverseObjectProvenanceAsync_NoContributorRecorded_ReturnsNotRecordedAsync()
    {
        var (_, _, _, mvType, mvAttribute, _, _) = await SeedSchemaAsync();

        Guid mvoId;
        await using (var ctx = NewContext())
            mvoId = await SeedMetaverseObjectAsync(ctx, mvType);

        await using (var ctx = NewContext())
        {
            ctx.Attach(mvAttribute);
            ctx.MetaverseObjectAttributeValues.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = AttachMvoStub(ctx, mvoId),
                Attribute = mvAttribute,
                AttributeId = mvAttribute.Id,
                StringValue = "Set internally"
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = NewContext();
        var repository = new PostgresDataRepository(readCtx);
        var result = await repository.Metaverse.GetMetaverseObjectProvenanceAsync(mvoId);

        Assert.That(result!.Attributes.Single().Origins.Single().Kind, Is.EqualTo(ValueOriginKind.NotRecorded));
    }

    #endregion

    #region GetMetaverseAttributeCurrentValuesAsync

    [Test]
    public async Task GetMetaverseAttributeCurrentValuesAsync_CapsResultsAndReportsTrueTotalAsync()
    {
        var (system, _, _, mvType, mvAttribute, rule, _) = await SeedSchemaAsync();
        // Make the attribute multi-valued for this test's purposes at the storage level: the repository
        // query does not consult AttributePlurality, only the row count, so this is safe without remodelling.

        Guid mvoId;
        await using (var ctx = NewContext())
            mvoId = await SeedMetaverseObjectAsync(ctx, mvType);

        await using (var ctx = NewContext())
        {
            ctx.Attach(mvAttribute);
            for (var i = 0; i < 5; i++)
            {
                ctx.MetaverseObjectAttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    MetaverseObject = AttachMvoStub(ctx, mvoId),
                    Attribute = mvAttribute,
                    AttributeId = mvAttribute.Id,
                    StringValue = $"Value {i}",
                    ContributedBySystemId = system.Id,
                    ContributedBySyncRuleId = rule.Id
                });
            }
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = NewContext();
        var repository = new PostgresDataRepository(readCtx);
        var (values, totalCount) = await repository.Metaverse.GetMetaverseAttributeCurrentValuesAsync(mvoId, mvAttribute.Id, cap: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(totalCount, Is.EqualTo(5));
            Assert.That(values, Has.Count.EqualTo(3));
            Assert.That(values[0].Origin.SyncRuleName, Is.EqualTo("HR Import"));
        }
    }

    #endregion

    #region GetLastAttributeSetChangeAsync / GetAttributeHistoryRawEntriesAsync

    [Test]
    public async Task GetLastAttributeSetChangeAsync_NoChangesRecorded_ReturnsNullAsync()
    {
        var (_, _, _, mvType, mvAttribute, _, _) = await SeedSchemaAsync();
        Guid mvoId;
        await using (var ctx = NewContext())
            mvoId = await SeedMetaverseObjectAsync(ctx, mvType);

        await using var readCtx = NewContext();
        var repository = new PostgresDataRepository(readCtx);
        var result = await repository.Metaverse.GetLastAttributeSetChangeAsync(mvoId, mvAttribute.Id);

        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// Seeds a single-valued attribute's history as: Add "First" (its own change), then Remove "First" +
    /// Add "Second" in one later change (a Set), matching the shape <c>PairAttributeHistory</c> expects.
    /// </summary>
    private static async Task RecordSetHistoryAsync(JimDbContext ctx, Guid mvoId, MetaverseAttribute attribute, SyncRule rule, DateTime firstChangeTime, DateTime secondChangeTime)
    {
        ctx.Attach(attribute);

        var firstChange = new MetaverseObjectChange
        {
            MetaverseObject = AttachMvoStub(ctx, mvoId),
            ChangeTime = firstChangeTime,
            ChangeType = ObjectChangeType.Updated,
            ChangeInitiatorType = MetaverseObjectChangeInitiatorType.SynchronisationRule
        };
        var firstAttrChange = new MetaverseObjectChangeAttribute { Attribute = attribute, AttributeName = attribute.Name, AttributeType = attribute.Type, MetaverseObjectChange = firstChange };
        firstChange.AttributeChanges.Add(firstAttrChange);
        firstAttrChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(firstAttrChange, ValueChangeType.Add, "First")
        {
            ContributedBySyncRuleId = rule.Id,
            ContributedBySyncRuleName = rule.Name
        });
        ctx.Add(firstChange);
        await ctx.SaveChangesAsync();

        var secondChange = new MetaverseObjectChange
        {
            MetaverseObject = AttachMvoStub(ctx, mvoId),
            ChangeTime = secondChangeTime,
            ChangeType = ObjectChangeType.Updated,
            ChangeInitiatorType = MetaverseObjectChangeInitiatorType.SynchronisationRule
        };
        var secondAttrChange = new MetaverseObjectChangeAttribute { Attribute = attribute, AttributeName = attribute.Name, AttributeType = attribute.Type, MetaverseObjectChange = secondChange };
        secondChange.AttributeChanges.Add(secondAttrChange);
        secondAttrChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(secondAttrChange, ValueChangeType.Remove, "First"));
        secondAttrChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(secondAttrChange, ValueChangeType.Add, "Second")
        {
            ContributedBySyncRuleId = rule.Id,
            ContributedBySyncRuleName = rule.Name
        });
        ctx.Add(secondChange);
        await ctx.SaveChangesAsync();
    }

    [Test]
    public async Task GetLastAttributeSetChangeAsync_ReturnsTheMostRecentAddAsync()
    {
        var (_, _, _, mvType, mvAttribute, rule, _) = await SeedSchemaAsync();
        Guid mvoId;
        await using (var ctx = NewContext())
            mvoId = await SeedMetaverseObjectAsync(ctx, mvType);

        var firstTime = DateTime.UtcNow.AddDays(-2);
        var secondTime = DateTime.UtcNow.AddDays(-1);
        await using (var ctx = NewContext())
            await RecordSetHistoryAsync(ctx, mvoId, mvAttribute, rule, firstTime, secondTime);

        await using var readCtx = NewContext();
        var repository = new PostgresDataRepository(readCtx);
        var result = await repository.Metaverse.GetLastAttributeSetChangeAsync(mvoId, mvAttribute.Id);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ChangeTime, Is.EqualTo(secondTime).Within(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task GetAttributeHistoryRawEntriesAsync_ReturnsRawRowsNewestFirstForPairingByProvenanceLogicAsync()
    {
        var (_, _, _, mvType, mvAttribute, rule, _) = await SeedSchemaAsync();
        Guid mvoId;
        await using (var ctx = NewContext())
            mvoId = await SeedMetaverseObjectAsync(ctx, mvType);

        var firstTime = DateTime.UtcNow.AddDays(-2);
        var secondTime = DateTime.UtcNow.AddDays(-1);
        await using (var ctx = NewContext())
            await RecordSetHistoryAsync(ctx, mvoId, mvAttribute, rule, firstTime, secondTime);

        await using var readCtx = NewContext();
        var repository = new PostgresDataRepository(readCtx);
        var rawEntries = await repository.Metaverse.GetAttributeHistoryRawEntriesAsync(mvoId, mvAttribute.Id, rawCap: 150);

        // Three raw rows: the first Add, and the second change's Remove + Add.
        Assert.That(rawEntries, Has.Count.EqualTo(3));

        var (entries, truncated) = ProvenanceLogic.PairAttributeHistory(rawEntries, AttributePlurality.SingleValued, cap: 50);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(truncated, Is.False);
            Assert.That(entries, Has.Count.EqualTo(2));
            Assert.That(entries[0].Kind, Is.EqualTo(AttributeHistoryChangeKind.Set));
            Assert.That(entries[0].Value, Is.EqualTo("Second"));
            Assert.That(entries[0].PreviousValue, Is.EqualTo("First"));
            Assert.That(entries[0].SyncRuleName, Is.EqualTo("HR Import"));
            Assert.That(entries[1].Kind, Is.EqualTo(AttributeHistoryChangeKind.Added));
            Assert.That(entries[1].Value, Is.EqualTo("First"));
        }
    }

    #endregion

    #region GetContributingConnectedSystemObjectAsync / GetJoinedConnectedSystemObjectForProvenanceAsync

    [Test]
    public async Task GetContributingConnectedSystemObjectAsync_ReturnsTheJoinedCsoWithNameAndTypeAsync()
    {
        // Note: IX_ConnectedSystemObjects_ConnectedSystemId_MetaverseObjectId_Unique (migration
        // AddUniqueSameSystemJoinIndex) means a Metaverse Object can be joined to at most one Connected
        // System Object PER Connected System, regardless of object type; the "prefer the contributing
        // rule's object type" branch in GetContributingConnectedSystemObjectAsync therefore never has more
        // than one candidate to choose from in practice, but must still return that one candidate correctly.
        var (system, csType, _, mvType, _, rule, _) = await SeedSchemaAsync();

        Guid mvoId;
        Guid csoId;
        await using (var ctx = NewContext())
        {
            mvoId = await SeedMetaverseObjectAsync(ctx, mvType);

            // ConnectedSystem left null and only the FK scalar set (src/CLAUDE.md, "DbSet.Add Walks the
            // Graph"): system came from SeedSchemaAsync's own disposed context, so a loaded ConnectedSystem
            // navigation here would walk into it and its own ConnectorDefinition, re-inserting both.
            var cso = new ConnectedSystemObject { ConnectedSystemId = system.Id, TypeId = csType.Id, MetaverseObjectId = mvoId };
            ctx.Add(cso);
            await ctx.SaveChangesAsync();
            csoId = cso.Id;
        }

        await using var readCtx = NewContext();
        var repository = new PostgresDataRepository(readCtx);
        var result = await repository.Metaverse.GetContributingConnectedSystemObjectAsync(mvoId, system.Id, rule.Id);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(csoId));
        Assert.That(result!.TypeName, Is.EqualTo("user"));
        Assert.That(result!.ConnectedSystemName, Is.EqualTo(system.Name));
    }

    [Test]
    public async Task GetJoinedConnectedSystemObjectForProvenanceAsync_LoadsAttributeValuesAndTypeAttributesAsync()
    {
        var (system, csType, csAttribute, mvType, _, _, _) = await SeedSchemaAsync();

        Guid mvoId;
        await using (var ctx = NewContext())
        {
            mvoId = await SeedMetaverseObjectAsync(ctx, mvType);
            ctx.Attach(csType);
            var cso = new ConnectedSystemObject { ConnectedSystem = system, ConnectedSystemId = system.Id, Type = csType, TypeId = csType.Id, MetaverseObjectId = mvoId };
            ctx.Add(cso);
            await ctx.SaveChangesAsync();

            ctx.Attach(csAttribute);
            ctx.ConnectedSystemObjectAttributeValues.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, Attribute = csAttribute, AttributeId = csAttribute.Id, StringValue = "Engineering" });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = NewContext();
        var repository = new PostgresDataRepository(readCtx);
        var cso2 = await repository.Metaverse.GetJoinedConnectedSystemObjectForProvenanceAsync(mvoId, system.Id, csType.Id);

        Assert.That(cso2, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso2!.AttributeValues.Single().StringValue, Is.EqualTo("Engineering"));
            Assert.That(cso2!.Type.Attributes.Select(a => a.Id), Does.Contain(csAttribute.Id));
        }
    }

    #endregion
}
