// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
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
        await PostgresTestDatabase.ResetAsync(_connectionString);
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
    /// The existence query's matching rules (#2669): a resolved reference (a <c>ReferenceValueId</c>) is matched by its
    /// id alone, so a resolved row whose retained DN equals the deleted object's while its id points at a different
    /// object is NOT matched; an unresolved reference is matched by its string ignoring case, whatever the casing; and
    /// nothing outside the given Connected System Objects or attributes is returned, whichever way it would match.
    /// </summary>
    [Test]
    public async Task GetCsoReferenceValueMatchesAsync_ResolvedMatchedByIdOnlyUnresolvedByValue_HonoursFiltersAsync()
    {
        const string deletedDn = "uid=lena.leaver,ou=People,dc=glitterband,dc=local";
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband LDAP", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "GROUP", ConnectedSystem = system, Selected = true };
        var memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = csType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        var managedByAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "managedBy", ConnectedSystemObjectType = csType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(memberAttr);
        csType.Attributes.Add(managedByAttr);
        seed.AddRange(connectorDefinition, system, csType);
        await seed.SaveChangesAsync();

        ConnectedSystemObject NewCso() => new() { Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal };
        var deletedMemberCso = NewCso();
        var otherMemberCso = NewCso();
        var groupCso = NewCso();
        var unlistedGroupCso = NewCso();

        ConnectedSystemObjectAttributeValue AddValue(ConnectedSystemObject owner, ConnectedSystemObjectTypeAttribute attribute,
            ConnectedSystemObject? referenced, string? unresolved)
        {
            var value = new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), ConnectedSystemObject = owner, Attribute = attribute,
                ReferenceValue = referenced, UnresolvedReferenceValue = unresolved
            };
            owner.AttributeValues.Add(value);
            return value;
        }

        // Matched: resolved to the deleted object (it keeps its imported DN, as every resolved reference does).
        var resolvedToDeleted = AddValue(groupCso, memberAttr, deletedMemberCso, deletedDn);
        // Matched: unresolved, in casings other than the one captured.
        var unresolvedUpper = AddValue(groupCso, memberAttr, null, deletedDn.ToUpperInvariant());
        var unresolvedMixed = AddValue(groupCso, memberAttr, null, "UID=Lena.Leaver,OU=People,DC=glitterband,DC=local");
        // NOT matched: resolved to a different object, though its retained DN string equals the deleted object's.
        AddValue(groupCso, memberAttr, otherMemberCso, deletedDn);
        // NOT matched: unrelated, resolved and unresolved.
        AddValue(groupCso, memberAttr, otherMemberCso, "uid=someone.else,ou=People,dc=glitterband,dc=local");
        AddValue(groupCso, memberAttr, null, "uid=another.one,ou=People,dc=glitterband,dc=local");
        // NOT matched: an attribute outside the filter, either way of matching.
        AddValue(groupCso, managedByAttr, deletedMemberCso, deletedDn);
        AddValue(groupCso, managedByAttr, null, deletedDn);
        // NOT matched: a Connected System Object outside the filter, either way of matching.
        AddValue(unlistedGroupCso, memberAttr, deletedMemberCso, deletedDn);
        AddValue(unlistedGroupCso, memberAttr, null, deletedDn);

        seed.AddRange(deletedMemberCso, otherMemberCso, groupCso, unlistedGroupCso);
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var matches = await repository.Sync.GetCsoReferenceValueMatchesAsync(
            [groupCso.Id],
            [memberAttr.Id],
            [deletedMemberCso.Id],
            [deletedDn]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(matches.Select(m => m.AttributeValueId),
                Is.EquivalentTo(new[] { resolvedToDeleted.Id, unresolvedUpper.Id, unresolvedMixed.Id }),
                "Exactly the row resolved to the deleted object and the two unresolved rows naming it may match");
            Assert.That(matches.Single(m => m.AttributeValueId == resolvedToDeleted.Id).ReferenceValueId,
                Is.EqualTo(deletedMemberCso.Id));
            Assert.That(matches.Where(m => m.AttributeValueId != resolvedToDeleted.Id).Select(m => m.ReferenceValueId),
                Is.All.Null, "Rows matched by value are unresolved");
            Assert.That(matches.Select(m => m.ConnectedSystemObjectId), Is.All.EqualTo(groupCso.Id));
            Assert.That(matches.Select(m => m.AttributeId), Is.All.EqualTo(memberAttr.Id));
            Assert.That(ctx.Database.GetDbConnection().State, Is.EqualTo(System.Data.ConnectionState.Closed),
                "The existence query must return its connection to the pool");
        }
    }

    /// <summary>
    /// The existence query must find its rows through indexes keyed on what it is looking for, not by reading every
    /// member of every referencing group (#2669). Deprovisioning 5,000 leavers at Scale500k25kGroups spent about 8 s
    /// here, because the string comparison sat in an OR beside the id comparison where no index could serve it, so
    /// PostgreSQL read each group's whole membership through the (object, attribute) index and filtered it. Each way of
    /// matching must instead be an index condition: the resolved id on the reference index, the lowered string on the
    /// partial functional index over unresolved references.
    /// </summary>
    [Test]
    public async Task GetCsoReferenceValueMatchesAsync_LargeGroup_PlanReadsOnlyTheSoughtRowsThroughIndexesAsync()
    {
        const string deletedDn = "uid=lena.leaver,ou=People,dc=glitterband,dc=local";
        var (groupCsoId, deletedMemberCsoId, memberAttributeId) = await SeedGroupWithMemberRowsAsync(deletedDn);

        await using var ctx = NewContext();
        var connection = (Npgsql.NpgsqlConnection)ctx.Database.GetDbConnection();
        await connection.OpenAsync();

        // A large group: 2,000 member objects held as resolved references (each keeping its DN, as resolution leaves
        // it) and 20,000 still unresolved. Copied from seeded rows so every other column takes a valid value.
        await using (var populate = new Npgsql.NpgsqlCommand("""
            INSERT INTO "ConnectedSystemObjects"
            SELECT (jsonb_populate_record(cso, jsonb_build_object('Id', md5('member' || n)::uuid))).*
            FROM "ConnectedSystemObjects" AS cso, generate_series(1, 2000) AS n
            WHERE cso."Id" = @deletedCsoId;

            INSERT INTO "ConnectedSystemObjectAttributeValues"
            SELECT (jsonb_populate_record(av, jsonb_build_object(
                'Id', md5('resolved' || n)::uuid,
                'ReferenceValueId', md5('member' || n)::uuid,
                'UnresolvedReferenceValue', 'uid=member' || n || ',ou=People,dc=glitterband,dc=local'))).*
            FROM "ConnectedSystemObjectAttributeValues" AS av, generate_series(1, 2000) AS n
            WHERE av."ConnectedSystemObjectId" = @groupCsoId AND av."ReferenceValueId" = @deletedCsoId;

            INSERT INTO "ConnectedSystemObjectAttributeValues"
            SELECT (jsonb_populate_record(av, jsonb_build_object(
                'Id', md5('unresolved' || n)::uuid,
                'UnresolvedReferenceValue', 'uid=pending' || n || ',ou=People,dc=glitterband,dc=local'))).*
            FROM "ConnectedSystemObjectAttributeValues" AS av, generate_series(1, 20000) AS n
            WHERE av."ConnectedSystemObjectId" = @groupCsoId AND av."ReferenceValueId" IS NULL;

            ANALYZE "ConnectedSystemObjects";
            ANALYZE "ConnectedSystemObjectAttributeValues";
            """, connection))
        {
            populate.Parameters.AddWithValue("groupCsoId", groupCsoId);
            populate.Parameters.AddWithValue("deletedCsoId", deletedMemberCsoId);
            await populate.ExecuteNonQueryAsync();
        }

        await using var explain = new Npgsql.NpgsqlCommand(
            "EXPLAIN " + JIM.PostgresData.Repositories.SyncRepository.CsoReferenceValueMatchSql, connection);
        JIM.PostgresData.Repositories.SyncRepository.AddCsoReferenceValueMatchParameters(explain,
            [groupCsoId], [memberAttributeId], [deletedMemberCsoId], [deletedDn]);

        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                plan.Add(reader.GetString(0));
        }

        var planText = string.Join("\n", plan);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Any(line => line.Contains("Seq Scan on \"ConnectedSystemObjectAttributeValues\"")), Is.False,
                "The query scans every attribute value.\n" + planText);
            Assert.That(plan.Where(line => line.Contains("Index Cond:")).Any(line => line.Contains("\"ReferenceValueId\" = ANY")), Is.True,
                "The resolved id must be an index condition.\n" + planText);
            Assert.That(plan.Where(line => line.Contains("Index Cond:")).Any(line => line.Contains("lower(")), Is.True,
                "The lowered reference string must be an index condition.\n" + planText);
            Assert.That(plan.Where(line => line.Contains("Filter:")).Any(line => line.Contains("lower(")), Is.False,
                "The reference string must not be compared row by row after the scan.\n" + planText);
        }
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
        Assert.That(() => ctx.SaveChangesAsync(), Throws.Nothing,
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

        // The authority check that runs before every export (#1885) must find the removal authorised: it is attributed
        // to the export rule whose enabled Attribute Flow targets the member attribute, on a joined group. Neither its
        // candidate query nor its decision may count it, or every leaver's group removals would be read back, or
        // withdrawn, at the start of the export (#2669).
        await using var authorityContext = NewContext();
        var authorityRepository = new PostgresDataRepository(authorityContext);
        using var authorityJim = new JimApplication(authorityRepository, syncRepository: authorityRepository.Sync);
        var candidates = await authorityRepository.Sync.GetUpdatePendingExportsWithQueuedChangesPossiblyWithoutAuthorityAsync(systemId, []);
        var withdrawal = await authorityJim.ExportExecution.WithdrawQueuedChangesWithoutAuthorityAsync(systemId);
        await using var afterWithdrawal = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidates, Is.Empty, "a recall-staged removal is not a candidate for withdrawal");
            Assert.That(withdrawal, Is.EqualTo((0, 0)), "nothing is withdrawn");
            Assert.That(await afterWithdrawal.PendingExportAttributeValueChanges.AnyAsync(c => c.Id == change.Id), Is.True,
                "the removal is still queued");
        }
    }
}
