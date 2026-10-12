// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Preview fidelity for a join or projection that flows nothing. The run records it all the same, as a lone Joined or
/// Projected outcome, because the object now has a Metaverse Object it did not have before; a preview that shows
/// nothing for it tells the administrator no change is needed when the run is about to make one.
/// </summary>
public partial class SyncPreviewFidelityTests
{
    [Test]
    public async Task PreviewSyncForCsoAsync_JoinWithNothingFlowing_TreeMatchesTheRealSyncOutcomeTreeAsync()
    {
        // Arrange - a Directory projects a person carrying an Employee ID; HR's object matches it on Employee ID and
        // flows nothing in.
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "User");
        var directoryImportRule = await CreateImportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Import");
        var directoryEmployeeIdAttr = directoryType.Attributes.First(a => a.Name == "EmployeeId");
        directoryImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = directoryImportRule,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = directoryEmployeeIdAttr,
                ConnectedSystemAttributeId = directoryEmployeeIdAttr.Id
            }}
        });

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "Person");
        var hrEmployeeIdAttr = hrType.Attributes.First(a => a.Name == "EmployeeId");
        var hrImportRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import", enableProjection: false);
        hrImportRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            Order = 0,
            CaseSensitive = true,
            SyncRule = hrImportRule,
            MetaverseObjectType = mvType,
            MetaverseObjectTypeId = mvType.Id,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = hrEmployeeIdAttr, ConnectedSystemAttributeId = hrEmployeeIdAttr.Id }]
        });
        await DbContext.SaveChangesAsync();

        await CreateCsoAsync(directory.Id, directoryType, "Jane Smith", "EMP001");
        var hrCso = await CreateCsoAsync(hr.Id, hrType, "Jane Smith (HR)", "EMP001");
        await RunFullSyncAsync(directory);

        // Act 1 - preview HR's object, BEFORE the real sync, over identical data
        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(hr.Id, hrCso.Id);

        // Act 2 - the real synchronisation over the same (undisturbed) data
        var hrActivity = await RunFullSyncAsync(hr);

        // Assert - both trees are the one Joined outcome
        var realTree = MapRealOutcomeTree(hrActivity);
        Assert.That(DescribeTree(realTree), Does.StartWith(nameof(ActivityRunProfileExecutionItemSyncOutcomeType.Joined)),
            "precondition: the run records the join although nothing flowed");
        Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(realTree)),
            "The preview's outcome tree must have the same shape as the tree the real synchronisation recorded (PRD requirement 9)");
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ProjectionWithNothingFlowing_TreeMatchesTheRealSyncOutcomeTreeAsync()
    {
        // Arrange - an import rule that projects and flows nothing in
        var source = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(source.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(source.Id, sourceType, mvType, "HR Import");
        var cso = await CreateCsoAsync(source.Id, sourceType, "John Smith", "EMP001");

        // Act 1 - preview, BEFORE the real sync, over identical data
        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(source.Id, cso.Id);

        // Act 2 - the real synchronisation over the same (undisturbed) data
        var activity = await RunFullSyncAsync(source);

        // Assert - both trees are the one Projected outcome
        var realTree = MapRealOutcomeTree(activity);
        Assert.That(DescribeTree(realTree), Does.StartWith(nameof(ActivityRunProfileExecutionItemSyncOutcomeType.Projected)),
            "precondition: the run records the projection although nothing flowed");
        Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(realTree)),
            "The preview's outcome tree must have the same shape as the tree the real synchronisation recorded (PRD requirement 9)");
    }
}
