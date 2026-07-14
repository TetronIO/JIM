// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
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
/// Real-PostgreSQL verification of the set-based reference recall staging (#1003): the raw-SQL
/// existence query's provider-specific surface (array parameters, LOWER() case folding), the
/// Summary-tier referencing-object load, and the end-to-end fast path on a tracking context.
/// The in-memory provider cannot catch any of these (LINQ parity hides the SQL, and it performs
/// no relational row-count checks), so this fixture is the regression guard the raw-SQL-write
/// rules require. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other
/// <c>RequiresPostgres</c> fixtures; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ReferenceRecallStagingDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL reference recall staging tests.");

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
    /// Seeds a target system with a group CSO whose member rows reference a deleted-member CSO
    /// three ways: resolved reference only, raw string only (different casing), and an unrelated
    /// member. Only the first two may match the existence query.
    /// </summary>
    private async Task<(Guid GroupCsoId, Guid DeletedMemberCsoId, int MemberAttributeId)> SeedGroupWithMemberRowsAsync(
        string capturedDn)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband LDAP", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "GROUP", ConnectedSystem = system, Selected = true };
        var memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = csType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        csType.Attributes.Add(memberAttr);
        seed.AddRange(connectorDefinition, system, csType);
        await seed.SaveChangesAsync();

        var deletedMemberCso = new ConnectedSystemObject
        {
            Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal
        };
        var unrelatedMemberCso = new ConnectedSystemObject
        {
            Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal
        };
        var groupCso = new ConnectedSystemObject
        {
            Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal
        };
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = groupCso,
            Attribute = memberAttr,
            ReferenceValue = deletedMemberCso
        });
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = groupCso,
            Attribute = memberAttr,
            // Different casing from the captured value: DNs are case-insensitive in LDAP, and the
            // existence query must fold case exactly as export evaluation's comparison does.
            UnresolvedReferenceValue = capturedDn.ToUpperInvariant()
        });
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = groupCso,
            Attribute = memberAttr,
            ReferenceValue = unrelatedMemberCso,
            UnresolvedReferenceValue = "uid=someone.else,ou=People,dc=glitterband,dc=local"
        });
        seed.AddRange(deletedMemberCso, unrelatedMemberCso, groupCso);
        await seed.SaveChangesAsync();

        return (groupCso.Id, deletedMemberCso.Id, memberAttr.Id);
    }

    /// <summary>
    /// The existence query must match by resolved reference id AND by case-insensitive raw string,
    /// return nothing for unrelated rows, and leave the connection closed (pool discipline).
    /// </summary>
    [Test]
    public async Task GetCsoReferenceValueMatchesAsync_MatchesByReferenceValueIdAndCaseInsensitiveValueAsync()
    {
        const string capturedDn = "uid=lena.leaver,ou=People,dc=glitterband,dc=local";
        var (groupCsoId, deletedMemberCsoId, memberAttributeId) = await SeedGroupWithMemberRowsAsync(capturedDn);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var matches = await repository.Sync.GetCsoReferenceValueMatchesAsync(
            [groupCsoId],
            [memberAttributeId],
            [deletedMemberCsoId],
            [capturedDn.ToLowerInvariant()]);

        Assert.That(matches, Has.Count.EqualTo(2),
            "Exactly the resolved-reference row and the case-folded raw-string row must match");
        Assert.That(matches.Any(m => m.ReferenceValueId == deletedMemberCsoId), Is.True,
            "The row matched by resolved reference must be returned");
        Assert.That(matches.Any(m => string.Equals(m.UnresolvedReferenceValue, capturedDn, StringComparison.OrdinalIgnoreCase)
                                     && m.ReferenceValueId == null), Is.True,
            "The row matched by case-insensitive raw string must be returned");
        Assert.That(matches.All(m => m.ConnectedSystemObjectId == groupCsoId), Is.True);
        Assert.That(ctx.Database.GetDbConnection().State, Is.EqualTo(System.Data.ConnectionState.Closed),
            "The existence query must return its connection to the pool");
    }

    /// <summary>
    /// The Summary-tier referencing-object load must project the shadow TypeId column and the
    /// display name, and return only the requested scoping-criteria attribute values.
    /// </summary>
    [Test]
    public async Task GetMetaverseObjectRecallSummariesAsync_ReturnsTypeIdDisplayNameAndOnlyCriteriaAttributesAsync()
    {
        await using var seed = NewContext();
        var scopedAttr = new MetaverseAttribute { Name = "Group Category", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var otherAttr = new MetaverseAttribute { Name = "Description", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var mvType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
        mvType.Attributes.Add(scopedAttr);
        mvType.Attributes.Add(otherAttr);
        seed.Add(mvType);
        await seed.SaveChangesAsync();

        var mvo = new MetaverseObject { Type = mvType, CachedDisplayName = "Team Alpha" };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), MetaverseObject = mvo, Attribute = scopedAttr, StringValue = "distribution"
        });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), MetaverseObject = mvo, Attribute = otherAttr, StringValue = "A team"
        });
        seed.Add(mvo);
        await seed.SaveChangesAsync();
        var mvoId = mvo.Id;
        var mvTypeId = mvType.Id;
        var scopedAttrId = scopedAttr.Id;

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var summaries = await repository.Sync.GetMetaverseObjectRecallSummariesAsync([mvoId], [scopedAttrId]);

        Assert.That(summaries, Has.Count.EqualTo(1));
        var summary = summaries.Single();
        Assert.That(summary.TypeId, Is.EqualTo(mvTypeId), "The shadow TypeId column must be projected");
        Assert.That(summary.DisplayName, Is.EqualTo("Team Alpha"));
        Assert.That(summary.ScopingAttributeValues, Has.Count.EqualTo(1),
            "Only the requested scoping-criteria attribute values may be loaded");
        Assert.That(summary.ScopingAttributeValues.Single().AttributeId, Is.EqualTo(scopedAttrId));
        Assert.That(summary.ScopingAttributeValues.Single().StringValue, Is.EqualTo("distribution"));
        Assert.That(ctx.Database.GetDbConnection().State, Is.EqualTo(System.Data.ConnectionState.Closed),
            "The summaries load must return its connection to the pool");
    }

    /// <summary>
    /// End-to-end fast path on a tracking context that holds the referencing group's CSO and its
    /// existing Pending Export: capture, set-based delete, stage, then SaveChangesAsync must not
    /// throw (the delete-then-create persistence must leave no poisoned tracked instances), and
    /// the staged export must carry the pre-resolved removal.
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_FastPathOnTrackingContext_StagesRemovalAndSaveChangesSucceedsAsync()
    {
        // Seed: source-of-truth graph for one group referencing one member, with an existing
        // unexported Update Pending Export on the group's CSO.
        const string memberDn = "uid=lena.leaver,ou=People,dc=glitterband,dc=local";
        Guid memberMvoId, groupMvoId, groupCsoId;
        int systemId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Glitterband LDAP", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "GROUP", ConnectedSystem = system, Selected = true };
            var memberAttr = new ConnectedSystemObjectTypeAttribute
            {
                Name = "member", ConnectedSystemObjectType = csType, Type = AttributeDataType.Reference,
                AttributePlurality = AttributePlurality.MultiValued, Selected = true
            };
            var dnAttr = new ConnectedSystemObjectTypeAttribute
            {
                Name = "distinguishedName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued, IsSecondaryExternalId = true, Selected = true
            };
            csType.Attributes.Add(memberAttr);
            csType.Attributes.Add(dnAttr);

            var mvMemberAttr = new MetaverseAttribute
            {
                Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued
            };
            var mvGroupType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
            mvGroupType.Attributes.Add(mvMemberAttr);
            var mvPersonType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
            seed.AddRange(connectorDefinition, system, csType, mvGroupType, mvPersonType);
            await seed.SaveChangesAsync();

            var exportRule = new SyncRule
            {
                Name = "Group Export",
                Enabled = true,
                Direction = SyncRuleDirection.Export,
                ConnectedSystemId = system.Id,
                MetaverseObjectTypeId = mvGroupType.Id,
                ConnectedSystemObjectTypeId = csType.Id
            };
            exportRule.AttributeFlowRules.Add(new SyncRuleMapping
            {
                TargetConnectedSystemAttribute = memberAttr,
                Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvMemberAttr } }
            });
            seed.Add(exportRule);

            var memberMvo = new MetaverseObject { Type = mvPersonType };
            var memberCso = new ConnectedSystemObject
            {
                Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
                MetaverseObject = memberMvo, JoinType = ConnectedSystemObjectJoinType.Provisioned,
                DateJoined = DateTime.UtcNow
            };
            memberCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), ConnectedSystemObject = memberCso, Attribute = dnAttr, StringValue = memberDn
            });

            var groupMvo = new MetaverseObject { Type = mvGroupType, CachedDisplayName = "Team Alpha" };
            groupMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(), MetaverseObject = groupMvo, Attribute = mvMemberAttr, ReferenceValue = memberMvo
            });
            var groupCso = new ConnectedSystemObject
            {
                Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
                MetaverseObject = groupMvo, JoinType = ConnectedSystemObjectJoinType.Provisioned,
                DateJoined = DateTime.UtcNow
            };
            groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), ConnectedSystemObject = groupCso, Attribute = memberAttr,
                ReferenceValue = memberCso, UnresolvedReferenceValue = memberDn
            });
            seed.AddRange(memberMvo, memberCso, groupMvo, groupCso);
            await seed.SaveChangesAsync();

            var existingPendingExport = new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = system.Id,
                ConnectedSystemObjectId = groupCso.Id,
                SourceMetaverseObjectId = groupMvo.Id,
                ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };
            seed.Add(existingPendingExport);
            await seed.SaveChangesAsync();

            memberMvoId = memberMvo.Id;
            groupMvoId = groupMvo.Id;
            groupCsoId = groupCso.Id;
            systemId = system.Id;
        }

        // Act on a single tracking context, mirroring the worker's long-lived run context.
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        using var jim = new JimApplication(repository, syncRepository: repository.Sync);

        // Earlier page processing tracks the group's CSO and its Pending Export.
        _ = await ctx.ConnectedSystemObjects.Include(cso => cso.AttributeValues)
            .SingleAsync(cso => cso.Id == groupCsoId);
        _ = await ctx.PendingExports.Include(pe => pe.AttributeValueChanges)
            .SingleAsync(pe => pe.ConnectedSystemObjectId == groupCsoId);

        var memberMvoTracked = await ctx.MetaverseObjects.SingleAsync(m => m.Id == memberMvoId);

        var context = await jim.ExportEvaluation.CaptureReferenceRecallContextAsync([memberMvoId]);
        await repository.Sync.DeleteMetaverseObjectsAsync([memberMvoTracked]);
        var result = await jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [memberMvoId]);

        Assert.That(result.FastPathReferencingObjects, Is.EqualTo(1));
        Assert.That(result.PendingExportsStaged, Is.EqualTo(1));

        // The tracking context must survive a subsequent save (raw-SQL write discipline).
        Assert.DoesNotThrowAsync(() => ctx.SaveChangesAsync(),
            "SaveChangesAsync after fast-path staging must not throw: tracked instances of the " +
            "replaced Pending Export must have been detached by the delete-then-create persistence");

        await using var verify = NewContext();
        var stagedPendingExport = await verify.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .SingleAsync(pe => pe.ConnectedSystemObjectId == groupCsoId);
        Assert.That(stagedPendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Update));
        Assert.That(stagedPendingExport.SourceMetaverseObjectId, Is.EqualTo(groupMvoId));
        Assert.That(stagedPendingExport.ConnectedSystemId, Is.EqualTo(systemId));
        var change = stagedPendingExport.AttributeValueChanges.Single();
        Assert.That(change.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Remove));
        Assert.That(change.StringValue, Is.EqualTo(memberDn),
            "The removal must carry the pre-resolved member DN captured before deletion");
    }
}
