// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for the Unique Value Generation schema (#242, Phase 1): the cascade lifecycle of
/// <see cref="GeneratedValueAssignment"/> and <see cref="GeneratedValueSequence"/>, the check constraints that
/// enforce each table's "exactly one" invariant, the uniqueness indexes, and the exclusion table's removal from
/// <see cref="JIM.PostgresData.Repositories.ConnectedSystemRepository.DeleteConnectedSystemAsync"/>.
/// <para>
/// Only a real provider can see any of this: the in-memory provider enforces no foreign keys, no check
/// constraints and no unique indexes, so every fault this fixture guards against passes silently there.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class GeneratedValueSchemaDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Unique Value Generation schema tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    /// <summary>
    /// One Connected System with a joined-up import mapping (targeting a fresh Metaverse attribute) and export
    /// mapping (targeting a fresh Connected System attribute), each with a Generation row, plus one Metaverse
    /// Object and one Connected System Object to hang assignments off. Every id a test needs is unique to it
    /// (suffixed), so tests never collide with each other's rows.
    /// </summary>
    private async Task<Estate> SeedEstateAsync(string suffix)
    {
        await using var ctx = NewContext();

        var mvoType = new MetaverseObjectType { Name = $"Person-{suffix}", PluralName = $"People-{suffix}" };
        var mvAttribute = new MetaverseAttribute { Name = $"Account Name-{suffix}", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        ctx.MetaverseObjectTypes.Add(mvoType);
        ctx.MetaverseAttributes.Add(mvAttribute);

        var connectorDefinition = new ConnectorDefinition { Name = $"def-{suffix}" };
        var system = new ConnectedSystem { Name = $"system-{suffix}", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var csAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "accountName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(csAttribute);
        ctx.AddRange(connectorDefinition, system, csType);
        await ctx.SaveChangesAsync();

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow };
        var cso = new ConnectedSystemObject
        {
            Type = csType,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            DateJoined = DateTime.UtcNow,
            ExternalIdAttributeId = csAttribute.Id
        };
        ctx.MetaverseObjects.Add(mvo);
        ctx.ConnectedSystemObjects.Add(cso);
        await ctx.SaveChangesAsync();

        var importRule = new SyncRule
        {
            Name = $"import-{suffix}", Direction = SyncRuleDirection.Import, ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = csType.Id, MetaverseObjectTypeId = mvoType.Id
        };
        var exportRule = new SyncRule
        {
            Name = $"export-{suffix}", Direction = SyncRuleDirection.Export, ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = csType.Id, MetaverseObjectTypeId = mvoType.Id
        };
        ctx.SyncRules.AddRange(importRule, exportRule);
        await ctx.SaveChangesAsync();

        var importMapping = new SyncRuleMapping { SyncRuleId = importRule.Id, TargetMetaverseAttributeId = mvAttribute.Id };
        var exportMapping = new SyncRuleMapping { SyncRuleId = exportRule.Id, TargetConnectedSystemAttributeId = csAttribute.Id };
        ctx.SyncRuleMappings.AddRange(importMapping, exportMapping);
        await ctx.SaveChangesAsync();

        var importGeneration = new SyncRuleMappingGeneration { SyncRuleMappingId = importMapping.Id, TokenKind = GeneratedValueTokenKind.Sequence };
        var exportGeneration = new SyncRuleMappingGeneration { SyncRuleMappingId = exportMapping.Id, TokenKind = GeneratedValueTokenKind.Sequence };
        ctx.SyncRuleMappingGenerations.AddRange(importGeneration, exportGeneration);
        await ctx.SaveChangesAsync();

        return new Estate(
            MvoId: mvo.Id,
            MvoTypeId: mvoType.Id,
            MvAttributeId: mvAttribute.Id,
            CsoId: cso.Id,
            CsAttributeId: csAttribute.Id,
            ImportRuleId: importRule.Id,
            ImportMappingId: importMapping.Id,
            ImportGenerationId: importGeneration.Id,
            ExportGenerationId: exportGeneration.Id);
    }

    private sealed record Estate(
        Guid MvoId,
        int MvoTypeId,
        int MvAttributeId,
        Guid CsoId,
        int CsAttributeId,
        int ImportRuleId,
        int ImportMappingId,
        int ImportGenerationId,
        int ExportGenerationId);

    private static GeneratedValueAssignment ImportAssignment(Estate estate, string value, int generationId) => new()
    {
        Id = Guid.NewGuid(),
        MetaverseObjectId = estate.MvoId,
        MetaverseAttributeId = estate.MvAttributeId,
        Value = value,
        NormalisedValue = value.ToLowerInvariant(),
        State = GeneratedValueAssignmentState.Committed,
        SyncRuleMappingGenerationId = generationId,
        CommittedAt = DateTime.UtcNow
    };

    private static GeneratedValueAssignment ExportAssignment(Estate estate, string value, int generationId) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemObjectId = estate.CsoId,
        ConnectedSystemObjectTypeAttributeId = estate.CsAttributeId,
        Value = value,
        NormalisedValue = value.ToLowerInvariant(),
        State = GeneratedValueAssignmentState.Committed,
        SyncRuleMappingGenerationId = generationId,
        CommittedAt = DateTime.UtcNow
    };

    // ---- Cascade lifecycle ----

    [Test]
    public async Task GeneratedValueAssignment_DeletedWhenMetaverseObjectIsDeletedAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);

        await using (var ctx = NewContext())
        {
            ctx.GeneratedValueAssignments.Add(ImportAssignment(estate, "joe.bloggs", estate.ImportGenerationId));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
            await ctx.Database.ExecuteSqlRawAsync(@"DELETE FROM ""MetaverseObjects"" WHERE ""Id"" = {0}", estate.MvoId);

        await using var assertCtx = NewContext();
        Assert.That(await assertCtx.GeneratedValueAssignments.AnyAsync(a => a.MetaverseObjectId == estate.MvoId), Is.False,
            "an assignment must not outlive the Metaverse Object it belongs to");
    }

    [Test]
    public async Task GeneratedValueAssignment_DeletedWhenConnectedSystemObjectIsDeletedAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);

        await using (var ctx = NewContext())
        {
            ctx.GeneratedValueAssignments.Add(ExportAssignment(estate, "joe.bloggs", estate.ExportGenerationId));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
            await ctx.Database.ExecuteSqlRawAsync(@"DELETE FROM ""ConnectedSystemObjects"" WHERE ""Id"" = {0}", estate.CsoId);

        await using var assertCtx = NewContext();
        Assert.That(await assertCtx.GeneratedValueAssignments.AnyAsync(a => a.ConnectedSystemObjectId == estate.CsoId), Is.False,
            "an assignment must not outlive the Connected System Object it belongs to");
    }

    [Test]
    public async Task GeneratedValueAssignment_DeletedWhenGenerationRowIsDeletedAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);
        Guid assignmentId;

        await using (var ctx = NewContext())
        {
            var assignment = ImportAssignment(estate, "joe.bloggs", estate.ImportGenerationId);
            assignmentId = assignment.Id;
            ctx.GeneratedValueAssignments.Add(assignment);
            await ctx.SaveChangesAsync();
        }

        // Removing the mapping is what an administrator does to un-generate an attribute (or delete the
        // Synchronisation Rule outright); the Generation row and every assignment it produced must go with it.
        await using (var ctx = NewContext())
            await ctx.Database.ExecuteSqlRawAsync(@"DELETE FROM ""SyncRuleMappings"" WHERE ""Id"" = {0}", estate.ImportMappingId);

        await using var assertCtx = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await assertCtx.SyncRuleMappingGenerations.AnyAsync(g => g.Id == estate.ImportGenerationId), Is.False,
                "the Generation row is a 1:1 child of the mapping and must go with it");
            Assert.That(await assertCtx.GeneratedValueAssignments.AnyAsync(a => a.Id == assignmentId), Is.False,
                "the assignment must go with the Generation row that produced it");
        }
    }

    [Test]
    public async Task GeneratedValueSequence_SurvivesDeletingItsMappingAndRuleAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);
        int sequenceId;

        await using (var ctx = NewContext())
        {
            var sequence = new GeneratedValueSequence
            {
                MetaverseAttributeId = estate.MvAttributeId,
                NextValue = 1000,
                LastMovedBySyncRuleMappingId = estate.ImportMappingId,
                LastMovedAt = DateTime.UtcNow
            };
            ctx.GeneratedValueSequences.Add(sequence);
            await ctx.SaveChangesAsync();
            sequenceId = sequence.Id;
        }

        // Deleting the whole Synchronisation Rule cascades to the mapping, which is what LastMovedBySyncRuleMappingId
        // points at; the counter itself must be entirely unaffected (plan decision 3), and the provenance FK must
        // clear rather than block the delete.
        await using (var ctx = NewContext())
            await ctx.Database.ExecuteSqlRawAsync(@"DELETE FROM ""SyncRules"" WHERE ""Id"" = {0}", estate.ImportRuleId);

        await using var assertCtx = NewContext();
        var sequenceAfter = await assertCtx.GeneratedValueSequences.SingleOrDefaultAsync(s => s.Id == sequenceId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sequenceAfter, Is.Not.Null, "the counter must survive deleting the mapping and rule that last moved it");
            Assert.That(sequenceAfter!.NextValue, Is.EqualTo(1000), "the counter's own progress is untouched");
            Assert.That(sequenceAfter.LastMovedBySyncRuleMappingId, Is.Null,
                "the provenance reference to the deleted mapping must be cleared, not left dangling");
        }
    }

    [Test]
    public async Task GeneratedValueSequence_DeletedWhenMetaverseAttributeIsDeletedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        int attributeId;
        int sequenceId;

        await using (var ctx = NewContext())
        {
            var attribute = new MetaverseAttribute { Name = $"Employee Number-{suffix}", Type = AttributeDataType.Number, AttributePlurality = AttributePlurality.SingleValued };
            ctx.MetaverseAttributes.Add(attribute);
            await ctx.SaveChangesAsync();
            attributeId = attribute.Id;

            var sequence = new GeneratedValueSequence { MetaverseAttributeId = attributeId, NextValue = 1 };
            ctx.GeneratedValueSequences.Add(sequence);
            await ctx.SaveChangesAsync();
            sequenceId = sequence.Id;
        }

        await using (var ctx = NewContext())
            await ctx.Database.ExecuteSqlRawAsync(@"DELETE FROM ""MetaverseAttributes"" WHERE ""Id"" = {0}", attributeId);

        await using var assertCtx = NewContext();
        Assert.That(await assertCtx.GeneratedValueSequences.AnyAsync(s => s.Id == sequenceId), Is.False,
            "the counter must not outlive the attribute it counts for (plan decision 3: deleted only with the attribute)");
    }

    // ---- Check constraints ----

    [Test]
    public async Task GeneratedValueAssignment_CheckConstraint_RejectsRowWithBothModesPopulatedAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);

        await using var ctx = NewContext();
        ctx.GeneratedValueAssignments.Add(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = estate.MvoId,
            MetaverseAttributeId = estate.MvAttributeId,
            ConnectedSystemObjectId = estate.CsoId,
            ConnectedSystemObjectTypeAttributeId = estate.CsAttributeId,
            Value = "joe.bloggs",
            NormalisedValue = "joe.bloggs",
            State = GeneratedValueAssignmentState.Proposed,
            SyncRuleMappingGenerationId = estate.ImportGenerationId
        });

        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>(),
            "CK_GeneratedValueAssignments_OneMode must reject a row carrying both the import and export pair");
    }

    [Test]
    public async Task GeneratedValueAssignment_CheckConstraint_RejectsRowWithNeitherModePopulatedAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);

        await using var ctx = NewContext();
        ctx.GeneratedValueAssignments.Add(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            Value = "joe.bloggs",
            NormalisedValue = "joe.bloggs",
            State = GeneratedValueAssignmentState.Proposed,
            SyncRuleMappingGenerationId = estate.ImportGenerationId
        });

        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>(),
            "CK_GeneratedValueAssignments_OneMode must reject a row carrying neither pair");
    }

    [Test]
    public async Task GeneratedValueSequence_CheckConstraint_RejectsRowWithBothAttributesPopulatedAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);

        await using var ctx = NewContext();
        ctx.GeneratedValueSequences.Add(new GeneratedValueSequence
        {
            MetaverseAttributeId = estate.MvAttributeId,
            ConnectedSystemObjectTypeAttributeId = estate.CsAttributeId,
            NextValue = 1
        });

        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>(),
            "CK_GeneratedValueSequences_OneAttribute must reject a row naming both a Metaverse and a Connected System attribute");
    }

    [Test]
    public async Task GeneratedValueSequence_CheckConstraint_RejectsRowWithNeitherAttributePopulatedAsync()
    {
        await using var ctx = NewContext();
        ctx.GeneratedValueSequences.Add(new GeneratedValueSequence { NextValue = 1 });

        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>(),
            "CK_GeneratedValueSequences_OneAttribute must reject a row naming no attribute at all");
    }

    // ---- Uniqueness indexes ----

    [Test]
    public async Task GeneratedValueAssignment_UniqueIndex_RejectsDuplicateNormalisedValueForTheSameAttributeAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);

        // A second, distinct Metaverse Object to hold the colliding value. The Type FK is set via the shadow
        // property rather than the navigation: assigning a detached MetaverseObjectType to the navigation would
        // have EF walk the graph and try to re-insert it (DbSet.Add walks the graph; src/CLAUDE.md), colliding
        // with the row SeedEstateAsync already created.
        Guid secondMvoId;
        await using (var ctx = NewContext())
        {
            var second = new MetaverseObject { Id = Guid.NewGuid(), Created = DateTime.UtcNow };
            ctx.MetaverseObjects.Add(second);
            ctx.Entry(second).Property("TypeId").CurrentValue = estate.MvoTypeId;
            await ctx.SaveChangesAsync();
            secondMvoId = second.Id;
        }

        await using (var ctx = NewContext())
        {
            ctx.GeneratedValueAssignments.Add(ImportAssignment(estate, "Joe.Bloggs", estate.ImportGenerationId));
            await ctx.SaveChangesAsync();
        }

        await using var dupCtx = NewContext();
        dupCtx.GeneratedValueAssignments.Add(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = secondMvoId,
            MetaverseAttributeId = estate.MvAttributeId,
            Value = "JOE.BLOGGS", // same value, different case: the index is on the lower-cased NormalisedValue.
            NormalisedValue = "joe.bloggs",
            State = GeneratedValueAssignmentState.Proposed,
            SyncRuleMappingGenerationId = estate.ImportGenerationId
        });

        Assert.That(async () => await dupCtx.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>(),
            "two live assignments for the same attribute must not hold the same value, case-insensitively (plan decision 13)");
    }

    [Test]
    public async Task GeneratedValueAssignment_UniqueIndex_RejectsSecondAssignmentForTheSameObjectAndAttributeAsync()
    {
        var estate = await SeedEstateAsync(Guid.NewGuid().ToString("N")[..8]);

        await using (var ctx = NewContext())
        {
            ctx.GeneratedValueAssignments.Add(ImportAssignment(estate, "joe.bloggs", estate.ImportGenerationId));
            await ctx.SaveChangesAsync();
        }

        await using var dupCtx = NewContext();
        dupCtx.GeneratedValueAssignments.Add(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = estate.MvoId,
            MetaverseAttributeId = estate.MvAttributeId,
            Value = "someone.else",
            NormalisedValue = "someone.else",
            State = GeneratedValueAssignmentState.Proposed,
            SyncRuleMappingGenerationId = estate.ImportGenerationId
        });

        Assert.That(async () => await dupCtx.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>(),
            "an object can hold at most one live assignment per attribute");
    }

    // ---- Connected System deletion ----

    [Test]
    public async Task DeleteConnectedSystemAsync_RemovesExclusionsNamingTheDeletedSystemAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // System A owns the generated mapping; System B is a separate system, excluded from its availability
        // checks. Deleting System B must remove the exclusion row without touching System A's configuration.
        int systemAId, systemBId, mappingId, generationId;
        await using (var ctx = NewContext())
        {
            var mvoType = new MetaverseObjectType { Name = $"Person-{suffix}", PluralName = $"People-{suffix}" };
            var mvAttribute = new MetaverseAttribute { Name = $"Account Name-{suffix}", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
            ctx.MetaverseObjectTypes.Add(mvoType);
            ctx.MetaverseAttributes.Add(mvAttribute);

            var connectorDefinitionA = new ConnectorDefinition { Name = $"def-a-{suffix}" };
            var connectorDefinitionB = new ConnectorDefinition { Name = $"def-b-{suffix}" };
            var systemA = new ConnectedSystem { Name = $"system-a-{suffix}", ConnectorDefinition = connectorDefinitionA };
            var systemB = new ConnectedSystem { Name = $"system-b-{suffix}", ConnectorDefinition = connectorDefinitionB };
            ctx.AddRange(connectorDefinitionA, connectorDefinitionB, systemA, systemB);
            await ctx.SaveChangesAsync();

            var csTypeA = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = systemA, Selected = true };
            ctx.ConnectedSystemObjectTypes.Add(csTypeA);
            await ctx.SaveChangesAsync();

            var importRule = new SyncRule
            {
                Name = $"import-{suffix}", Direction = SyncRuleDirection.Import, ConnectedSystemId = systemA.Id,
                ConnectedSystemObjectTypeId = csTypeA.Id, MetaverseObjectTypeId = mvoType.Id
            };
            ctx.SyncRules.Add(importRule);
            await ctx.SaveChangesAsync();

            var mapping = new SyncRuleMapping { SyncRuleId = importRule.Id, TargetMetaverseAttributeId = mvAttribute.Id };
            ctx.SyncRuleMappings.Add(mapping);
            await ctx.SaveChangesAsync();

            var generation = new SyncRuleMappingGeneration { SyncRuleMappingId = mapping.Id, TokenKind = GeneratedValueTokenKind.Sequence };
            ctx.SyncRuleMappingGenerations.Add(generation);
            await ctx.SaveChangesAsync();

            ctx.SyncRuleMappingGenerationExclusions.Add(new SyncRuleMappingGenerationExclusion
            {
                SyncRuleMappingGenerationId = generation.Id,
                ConnectedSystemId = systemB.Id
            });
            await ctx.SaveChangesAsync();

            systemAId = systemA.Id;
            systemBId = systemB.Id;
            mappingId = mapping.Id;
            generationId = generation.Id;
        }

        await using (var deleteCtx = NewContext())
        {
            var repository = new PostgresDataRepository(deleteCtx);

            Assert.That(async () => await repository.ConnectedSystems.DeleteConnectedSystemAsync(systemBId),
                Throws.Nothing,
                "a Connected System excluded from another system's generated mapping must still be deletable");
        }

        await using var assertCtx = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await assertCtx.ConnectedSystems.AnyAsync(cs => cs.Id == systemBId), Is.False,
                "the excluded system itself is gone");
            Assert.That(await assertCtx.SyncRuleMappingGenerationExclusions.AnyAsync(e => e.ConnectedSystemId == systemBId), Is.False,
                "the exclusion naming the deleted system must be removed");
            Assert.That(await assertCtx.SyncRuleMappingGenerations.AnyAsync(g => g.Id == generationId), Is.True,
                "System A's own generated mapping must be untouched by deleting the system it merely excludes");
            Assert.That(await assertCtx.SyncRuleMappings.AnyAsync(m => m.Id == mappingId), Is.True,
                "System A's own mapping must be untouched");
            Assert.That(await assertCtx.ConnectedSystems.AnyAsync(cs => cs.Id == systemAId), Is.True,
                "System A itself must be untouched");
        }

        // Clean up System A, which the test above deliberately left behind.
        await using var cleanupCtx = NewContext();
        await new PostgresDataRepository(cleanupCtx).ConnectedSystems.DeleteConnectedSystemAsync(systemAId);
    }
}
