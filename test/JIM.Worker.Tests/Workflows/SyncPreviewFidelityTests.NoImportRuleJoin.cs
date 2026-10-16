// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Preview fidelity for the joins the run makes through a Connected System Object Type's own Object Matching Rules
/// (#2176). In simple matching mode the run joins an unjoined object through its type's rules even when no import
/// Synchronisation Rule applies to it, which is the usual shape of a target directory whose accounts already exist; in
/// advanced mode the type's rules are kept but never consulted. The preview must make the same decision in both.
/// </summary>
public partial class SyncPreviewFidelityTests
{
    [Test]
    public async Task PreviewSyncForCsoAsync_SimpleModeJoinWithNoImportRule_PredictsTheJoinTheRunMakesAsync()
    {
        var (target, targetCso, matchingRule) = await ArrangeDirectoryAccountMatchedByItsTypeAsync(simpleMode: true, importRule: false);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(target.Id, targetCso.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(target));

        var realTree = MapRealOutcomeTree(activity);
        Assert.That(DescribeTree(realTree), Does.StartWith(nameof(ActivityRunProfileExecutionItemSyncOutcomeType.Joined)),
            "precondition: the run joins the account through its type's rule although no import rule applies");
        Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(realTree)),
            "The preview's outcome tree must have the same shape as the tree the real synchronisation recorded (PRD requirement 9)");

        var joined = preview.OutcomeTree.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Inbound!.WouldJoinMetaverseObjectId, Is.EqualTo((await ReloadEntityAsync(targetCso)).MetaverseObjectId));
            Assert.That(joined.ObjectMatchingRuleId, Is.EqualTo(matchingRule.Id));
            Assert.That(joined.ObjectMatchingRuleDescriptionSnapshot, Is.EqualTo(realTree.Single().ObjectMatchingRuleDescriptionSnapshot));
            Assert.That(joined.MatchedValue, Is.EqualTo("EMP001"));
            Assert.That(preview.Warnings.Select(w => w.Code), Has.No.Member(SyncPreviewMessageCode.NoApplicableSyncRule),
                "the object is processed inbound: it joins, though nothing flows in");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_AdvancedModeWithRulesKeptOnTheType_PredictsNoJoinAsTheRunMakesNoneAsync()
    {
        // Switching to advanced mode keeps the type's rules for a later switch back, but the run no longer consults them,
        // so an import rule without rules of its own joins nothing.
        var (target, targetCso, _) = await ArrangeDirectoryAccountMatchedByItsTypeAsync(simpleMode: false, importRule: true);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(target.Id, targetCso.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(target));

        using (Assert.EnterMultipleScope())
        {
            Assert.That((await ReloadEntityAsync(targetCso)).MetaverseObjectId, Is.Null, "precondition: the run makes no join");
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))),
                "The preview's outcome tree must have the same shape as the tree the real synchronisation recorded (PRD requirement 9)");
            Assert.That(preview.Inbound!.WouldJoinMetaverseObjectId, Is.Null, "the preview predicts no join the run does not make");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_SimpleModeJoinWithNoImportRuleAndADriftedValue_ProposesTheCorrectionTheRunMakesAsync()
    {
        // The target directory's own shape: an existing account joins by its type's rule, nothing flows in, and an export
        // rule enforcing state corrects the display name it holds.
        var (target, targetCso, _) = await ArrangeDirectoryAccountMatchedByItsTypeAsync(simpleMode: true, importRule: false, enforcingExport: true);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(target.Id, targetCso.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(target));

        var realTree = MapRealOutcomeTree(activity);
        Assert.That(DescribeTree(realTree), Does.Contain(nameof(ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection)),
            "precondition: the run corrects the drifted display name of the account it joins");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(realTree)),
                "The preview's outcome tree must have the same shape as the tree the real synchronisation recorded (PRD requirement 9)");
            Assert.That(preview.DriftCorrections.Select(c => c.CorrectedValue), Is.EqualTo(new[] { "Jane Smith" }));
        }
    }

    /// <summary>
    /// HR projects Jane Smith with her Employee ID and display name; a directory holds an unjoined account for her,
    /// "Edited in AD", which its Object Type's own Object Matching Rule matches on Employee ID. Nothing in the directory
    /// flows in. Returns the directory, its account and the type's rule.
    /// </summary>
    private async Task<(ConnectedSystem Target, ConnectedSystemObject TargetCso, ObjectMatchingRule MatchingRule)> ArrangeDirectoryAccountMatchedByItsTypeAsync(
        bool simpleMode, bool importRule, bool enforcingExport = false)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        mvType.Attributes.First(a => a.Name == "DisplayName").Name = Constants.BuiltInAttributes.DisplayName;
        await DbContext.SaveChangesAsync();
        var mvDisplayName = mvType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.DisplayName);
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var source = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(source.Id, "User");
        var hrImport = await CreateImportSyncRuleWithDisplayNameFlowAsync(source, sourceType, mvType);
        var hrEmployeeId = sourceType.Attributes.First(a => a.Name == "EmployeeId");
        hrImport.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = hrImport, TargetMetaverseAttribute = mvEmployeeId, TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrEmployeeId, ConnectedSystemAttributeId = hrEmployeeId.Id } }
        });

        var target = await CreateConnectedSystemAsync("AD Target");
        target.ObjectMatchingRuleMode = simpleMode ? ObjectMatchingRuleMode.ConnectedSystem : ObjectMatchingRuleMode.SyncRule;
        var targetType = await CreateCsoTypeAsync(target.Id, "user");
        var adEmployeeId = targetType.Attributes.First(a => a.Name == "EmployeeId");
        var matchingRule = new ObjectMatchingRule
        {
            Order = 0, CaseSensitive = true,
            ConnectedSystemObjectType = targetType, ConnectedSystemObjectTypeId = targetType.Id,
            MetaverseObjectType = mvType, MetaverseObjectTypeId = mvType.Id,
            TargetMetaverseAttribute = mvEmployeeId, TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = adEmployeeId, ConnectedSystemAttributeId = adEmployeeId.Id }]
        };
        targetType.ObjectMatchingRules.Add(matchingRule);
        if (importRule)
            await CreateImportSyncRuleAsync(target.Id, targetType, mvType, "AD Import", enableProjection: false);
        if (enforcingExport)
        {
            var adExport = await CreateExportSyncRuleAsync(target.Id, targetType, mvType, "AD Export", enableProvisioning: false);
            adExport.EnforceState = true;
            FlowToAttribute(adExport, targetType.Attributes.First(a => a.Name == "DisplayName"), mvDisplayName);
        }
        await DbContext.SaveChangesAsync();

        await CreateCsoAsync(source.Id, sourceType, "Jane Smith", "EMP001");
        var targetCso = await CreateCsoAsync(target.Id, targetType, "Edited in AD", "EMP001");
        await RunFullSyncAsync(source);
        SyncRepo.ClearAllPendingExports();
        Assert.That((await ReloadEntityAsync(targetCso)).MetaverseObjectId, Is.Null, "arrange: the directory account is not joined yet");

        return (target, targetCso, matchingRule);
    }
}
