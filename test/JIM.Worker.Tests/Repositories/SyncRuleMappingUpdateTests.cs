// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Tests.Workflows;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests for persisting attribute priority renumbering across sibling Synchronisation Rule mappings (#91).
/// Contributor lists are materialised with the context's default no-tracking behaviour, so every sibling
/// mapping carries its own TargetMetaverseAttribute instance with the same key. Persisting the renumbered
/// mappings must therefore never attach their navigation graphs; attaching the second duplicate-key
/// MetaverseAttribute instance throws an EF identity conflict (surfaced as an API 500 on
/// POST sync-rules/{id}/mappings via AutoAssignImportMappingPriorityAsync).
/// </summary>
[TestFixture]
public class SyncRuleMappingUpdateTests : WorkflowTestBase
{
    [Test]
    public async Task UpdateSyncRuleMappings_SiblingsCarryDuplicateAttributeInstances_PersistsScalarsWithoutIdentityConflictAsync()
    {
        // --- Seed: two import rules whose mappings target the same Metaverse attribute ---
        var system = await CreateConnectedSystemAsync("HR");
        var externalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var csoType = await CreateCsoTypeAsync(system.Id, "User",
            new List<ConnectedSystemObjectTypeAttribute> { externalIdAttr, displayNameAttr });

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");

        var ruleA = await CreateImportSyncRuleAsync(system.Id, csoType, mvType, "Rule A");
        var mappingA = new SyncRuleMapping
        {
            SyncRule = ruleA,
            SyncRuleId = ruleA.Id,
            Priority = int.MaxValue,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = displayNameAttr, ConnectedSystemAttributeId = displayNameAttr.Id } }
        };
        ruleA.AttributeFlowRules.Add(mappingA);

        var ruleB = await CreateImportSyncRuleAsync(system.Id, csoType, mvType, "Rule B");
        var mappingB = new SyncRuleMapping
        {
            SyncRule = ruleB,
            SyncRuleId = ruleB.Id,
            Priority = int.MaxValue,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = displayNameAttr, ConnectedSystemAttributeId = displayNameAttr.Id } }
        };
        ruleB.AttributeFlowRules.Add(mappingB);
        await DbContext.SaveChangesAsync();

        // --- Simulate the production contributor list: no-tracking materialisation gives every sibling
        // mapping its own TargetMetaverseAttribute instance with the same key ---
        var detachedA = BuildDetachedMapping(mappingA, priority: 1, mvDisplayNameAttr.Id);
        var detachedB = BuildDetachedMapping(mappingB, priority: 2, mvDisplayNameAttr.Id);
        detachedB.NullIsValue = true;

        // --- Act: persisting the renumbered priorities must not attach the duplicate-key graphs ---
        await Repository.ConnectedSystems.UpdateSyncRuleMappingsAsync(new[] { detachedA, detachedB });

        // --- Assert: both scalar changes persisted ---
        var persistedA = await DbContext.SyncRuleMappings.AsNoTracking().SingleAsync(m => m.Id == mappingA.Id);
        var persistedB = await DbContext.SyncRuleMappings.AsNoTracking().SingleAsync(m => m.Id == mappingB.Id);
        Assert.That(persistedA.Priority, Is.EqualTo(1), "mapping A's renumbered priority must persist");
        Assert.That(persistedB.Priority, Is.EqualTo(2), "mapping B's renumbered priority must persist");
        Assert.That(persistedB.NullIsValue, Is.True, "mapping B's null-handling change must persist");
    }

    private static SyncRuleMapping BuildDetachedMapping(SyncRuleMapping persisted, int priority, int targetAttributeId)
    {
        return new SyncRuleMapping
        {
            Id = persisted.Id,
            SyncRuleId = persisted.SyncRuleId,
            Priority = priority,
            TargetMetaverseAttributeId = targetAttributeId,
            // A fresh MetaverseAttribute instance per mapping, sharing the same key: exactly what a
            // no-tracking contributor query materialises per row in production.
            TargetMetaverseAttribute = new MetaverseAttribute
            {
                Id = targetAttributeId,
                Name = "DisplayName",
                Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued
            }
        };
    }
}
