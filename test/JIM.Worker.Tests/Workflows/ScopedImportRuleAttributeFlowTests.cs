// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Workflow tests for per-rule import scope during Attribute Flow (#1199). A Connected System may hold several
/// import Synchronisation Rules for the same object type, each with its own Scoping Criteria; that is the
/// mechanism behind fine-grained authority, where one system is nominally authoritative for an attribute while a
/// narrowly scoped rule on the same (or another) system takes authority for a defined subset of objects.
///
/// Object scope was originally evaluated only as an aggregate gate ("is this Connected System Object in scope of
/// at least one import rule?") plus a per-rule filter for join and projection. Attribute Flow then applied EVERY
/// enabled import rule's mappings, so a rule that the object was explicitly out of scope for still contributed
/// its values. These tests pin the per-rule behaviour: a rule contributes to an object only when that object is
/// in that rule's own scope.
///
/// Topology: one "Directory" Connected System with two import rules over the same object type. The plain rule is
/// unscoped, projects, and contributes Description at priority 2 from PlainDescription. The exception rule is
/// scoped to ScopeFlag == "InScope", does not project, and contributes Description at priority 1 from
/// ExceptionDescription. Two objects differ only in their ScopeFlag, so the winning rule (and therefore the
/// resulting Description) is the only thing that distinguishes them.
/// </summary>
[TestFixture]
public class ScopedImportRuleAttributeFlowTests : WorkflowTestBase
{
    private const string InScopePlainDescription = "In-scope object, plain rule value";
    private const string InScopeExceptionDescription = "In-scope object, exception rule value";
    private const string OutOfScopePlainDescription = "Out-of-scope object, plain rule value";
    private const string OutOfScopeExceptionDescription = "Out-of-scope object, exception rule value";

    [Test]
    public async Task AttributeFlow_ObjectInScopeOfAScopedRule_TakesThatRulesHigherPriorityValueAsync()
    {
        var ctx = await SetUpScopedExceptionTopologyAsync();

        await RunFullSyncAsync(ctx.System);

        var description = GetDescription(ctx, "IN-SCOPE");
        Assert.That(description?.StringValue, Is.EqualTo(InScopeExceptionDescription),
            "the scoped exception rule is priority 1 and the object is in its scope, so its value must win");
        Assert.That(description?.ContributedBySyncRuleId, Is.EqualTo(ctx.ExceptionRuleId),
            "provenance must name the exception rule: both rules read the same Connected System Object, so only the " +
            "contributing rule distinguishes which one won");
    }

    [Test]
    public async Task AttributeFlow_ObjectOutOfScopeOfAScopedRule_DoesNotTakeThatRulesValueAsync()
    {
        var ctx = await SetUpScopedExceptionTopologyAsync();

        await RunFullSyncAsync(ctx.System);

        var description = GetDescription(ctx, "OUT-OF-SCOPE");
        Assert.That(description?.StringValue, Is.EqualTo(OutOfScopePlainDescription),
            "the object is out of the exception rule's scope, so that rule has no opinion and the plain rule's " +
            "priority 2 value must win despite being lower priority");
        Assert.That(description?.StringValue, Is.Not.EqualTo(OutOfScopeExceptionDescription),
            "a rule the object is out of scope for must not contribute at all");
        Assert.That(description?.ContributedBySyncRuleId, Is.EqualTo(ctx.PlainRuleId),
            "provenance must name the plain rule");
    }

    [Test]
    public async Task AttributeFlow_ScopedRuleWithNoMatchingObjects_LeavesEveryObjectOnThePlainRuleAsync()
    {
        // The same topology with a scope no object satisfies. Nothing should change hands: this guards the
        // aggregate out-of-scope gate, which must keep treating an object as in scope overall while it remains in
        // scope of at least one rule, rather than disconnecting objects that merely miss a narrower rule.
        var ctx = await SetUpScopedExceptionTopologyAsync(exceptionScopeValue: "NobodyHasThisFlag");

        await RunFullSyncAsync(ctx.System);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetDescription(ctx, "IN-SCOPE")?.StringValue, Is.EqualTo(InScopePlainDescription));
            Assert.That(GetDescription(ctx, "OUT-OF-SCOPE")?.StringValue, Is.EqualTo(OutOfScopePlainDescription));
        }
    }

    /// <summary>
    /// Returns the single resolved Description value for the named object, failing the test if resolution left
    /// more than one. Description is single-valued, so two values means two rules both contributed rather than
    /// one winning: the same defect these tests exist for, seen from the persistence side.
    /// </summary>
    private MetaverseObjectAttributeValue? GetDescription(ScopedExceptionContext ctx, string employeeId)
    {
        var mvo = SyncRepo.MetaverseObjects.Values.Single(m =>
            m.AttributeValues.Any(av => av.AttributeId == ctx.MvEmployeeIdAttributeId && av.StringValue == employeeId));

        var values = mvo.AttributeValues.Where(av => av.AttributeId == ctx.MvDescriptionAttributeId && !av.NullValue).ToList();
        Assert.That(values, Has.Count.LessThanOrEqualTo(1),
            $"'{employeeId}' resolved to {values.Count} Description values ({string.Join(", ", values.Select(v => $"'{v.StringValue}'"))}); " +
            "a single-valued attribute must have exactly one winner");

        return values.SingleOrDefault();
    }

    /// <summary>
    /// Builds one Connected System carrying two import Synchronisation Rules for the same object type: an
    /// unscoped plain rule (projects, Description at priority 2) and a scoped exception rule (no projection,
    /// Description at priority 1). Two objects are created, distinguished only by their ScopeFlag value.
    /// </summary>
    private async Task<ScopedExceptionContext> SetUpScopedExceptionTopologyAsync(string exceptionScopeValue = "InScope")
    {
        var system = await CreateConnectedSystemAsync("Directory");

        var externalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var employeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var plainDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "PlainDescription", Type = AttributeDataType.Text, Selected = true };
        var exceptionDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExceptionDescription", Type = AttributeDataType.Text, Selected = true };
        var scopeFlagAttr = new ConnectedSystemObjectTypeAttribute { Name = "ScopeFlag", Type = AttributeDataType.Text, Selected = true };

        var csoType = await CreateCsoTypeAsync(system.Id, "DirectoryUser", new List<ConnectedSystemObjectTypeAttribute>
        {
            externalIdAttr, displayNameAttr, employeeIdAttr, plainDescriptionAttr, exceptionDescriptionAttr, scopeFlagAttr
        });

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvDescriptionAttr = new MetaverseAttribute
        {
            Name = "Description",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(mvDescriptionAttr);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(mvDescriptionAttr);

        // The plain rule: unscoped, projects, and is the nominal authority for Description at priority 2.
        var plainRule = await CreateImportSyncRuleAsync(system.Id, csoType, mvType, "Directory Import");
        plainRule.AttributeFlowRules.Add(BuildDirectImportMapping(plainRule, mvDisplayNameAttr, displayNameAttr));
        plainRule.AttributeFlowRules.Add(BuildDirectImportMapping(plainRule, mvEmployeeIdAttr, employeeIdAttr));
        plainRule.AttributeFlowRules.Add(BuildDirectImportMapping(plainRule, mvDescriptionAttr, plainDescriptionAttr, priority: 2));

        // The exception rule: scoped, contributes only Description, and outranks the plain rule where it applies.
        // No projection, because the plain rule already projects every object.
        var exceptionRule = await CreateImportSyncRuleAsync(system.Id, csoType, mvType, "Directory Import (Exceptions)", enableProjection: false);
        exceptionRule.AttributeFlowRules.Add(BuildDirectImportMapping(exceptionRule, mvDescriptionAttr, exceptionDescriptionAttr, priority: 1));
        exceptionRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    ConnectedSystemAttribute = scopeFlagAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = exceptionScopeValue,
                    CaseSensitive = true
                }
            }
        });
        await DbContext.SaveChangesAsync();

        await CreateScopedCsoAsync(system.Id, csoType, "In Scope Person", "IN-SCOPE", "InScope",
            plainDescriptionAttr, InScopePlainDescription, exceptionDescriptionAttr, InScopeExceptionDescription, scopeFlagAttr);
        await CreateScopedCsoAsync(system.Id, csoType, "Out Of Scope Person", "OUT-OF-SCOPE", "OutOfScope",
            plainDescriptionAttr, OutOfScopePlainDescription, exceptionDescriptionAttr, OutOfScopeExceptionDescription, scopeFlagAttr);

        return new ScopedExceptionContext(system, mvDescriptionAttr.Id, mvEmployeeIdAttr.Id, plainRule.Id, exceptionRule.Id);
    }

    private async Task CreateScopedCsoAsync(
        int connectedSystemId,
        ConnectedSystemObjectType csoType,
        string displayName,
        string employeeId,
        string scopeFlag,
        ConnectedSystemObjectTypeAttribute plainDescriptionAttr,
        string plainDescription,
        ConnectedSystemObjectTypeAttribute exceptionDescriptionAttr,
        string exceptionDescription,
        ConnectedSystemObjectTypeAttribute scopeFlagAttr)
    {
        var cso = await CreateCsoAsync(connectedSystemId, csoType, displayName, employeeId);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = plainDescriptionAttr.Id, Attribute = plainDescriptionAttr, StringValue = plainDescription, ConnectedSystemObject = cso
        });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = exceptionDescriptionAttr.Id, Attribute = exceptionDescriptionAttr, StringValue = exceptionDescription, ConnectedSystemObject = cso
        });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = scopeFlagAttr.Id, Attribute = scopeFlagAttr, StringValue = scopeFlag, ConnectedSystemObject = cso
        });
    }

    private static SyncRuleMapping BuildDirectImportMapping(SyncRule rule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source, int priority = int.MaxValue)
    {
        return new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            Priority = priority,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        };
    }

    private async Task RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
    }

    private sealed record ScopedExceptionContext(
        ConnectedSystem System,
        int MvDescriptionAttributeId,
        int MvEmployeeIdAttributeId,
        int PlainRuleId,
        int ExceptionRuleId);
}
