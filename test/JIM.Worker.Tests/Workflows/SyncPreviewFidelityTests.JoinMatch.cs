// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

public partial class SyncPreviewFidelityTests
{
    /// <summary>
    /// A join made by matching (#2052): the preview names the Object Matching Rule and value the join would be made on,
    /// and the real synchronisation records the same rule, description and value on its Joined outcome. The two
    /// describe the rule from separately loaded copies of it, so this is what keeps them in agreement.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task PreviewSyncForCsoAsync_JoinByMatching_NamesTheRuleAndValueTheRealSyncRecordsAsync(bool simpleMode)
    {
        // Arrange - a Directory projects a person carrying an Employee ID; HR's object matches it on Employee ID
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
        var matchingRule = new ObjectMatchingRule
        {
            Order = 0,
            CaseSensitive = true,
            MetaverseObjectType = mvType,
            MetaverseObjectTypeId = mvType.Id,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = hrEmployeeIdAttr, ConnectedSystemAttributeId = hrEmployeeIdAttr.Id }]
        };
        // An import rule that does not project, in both modes: in simple mode it holds no matching rule of its own, but
        // without one the preview does not yet predict the join at all (#2176).
        var hrImportRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import", enableProjection: false);
        if (simpleMode)
        {
            hr.ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem;
            matchingRule.ConnectedSystemObjectType = hrType;
            matchingRule.ConnectedSystemObjectTypeId = hrType.Id;
            hrType.ObjectMatchingRules.Add(matchingRule);
        }
        else
        {
            matchingRule.SyncRule = hrImportRule;
            hrImportRule.ObjectMatchingRules.Add(matchingRule);
        }
        await DbContext.SaveChangesAsync();

        await CreateCsoAsync(directory.Id, directoryType, "Jane Smith", "EMP001");
        var hrCso = await CreateCsoAsync(hr.Id, hrType, "Jane Smith (HR)", "EMP001");
        await RunFullSyncAsync(directory);

        // Act 1 - preview HR's object, BEFORE the real sync, over identical data
        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(hr.Id, hrCso.Id);

        // Act 2 - the real synchronisation over the same (undisturbed) data
        var hrActivity = await RunFullSyncAsync(hr);

        // Assert - the same tree, and the same rule, description and value on both Joined roots
        var realTree = MapRealOutcomeTree(hrActivity);
        Assert.That(realTree, Is.Not.Empty, "The real synchronisation must have recorded an outcome tree to pair against");
        Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(realTree)),
            "The preview's outcome tree must have the same shape as the tree the real synchronisation recorded (PRD requirement 9)");

        var previewJoined = preview.OutcomeTree.Single(n => n.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Joined);
        var realJoined = realTree.Single(n => n.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Joined);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(realJoined.ObjectMatchingRuleId, Is.EqualTo(matchingRule.Id), "precondition: the real join records its rule");
            Assert.That(previewJoined.ObjectMatchingRuleId, Is.EqualTo(realJoined.ObjectMatchingRuleId));
            Assert.That(previewJoined.ObjectMatchingRuleDescriptionSnapshot, Is.EqualTo(realJoined.ObjectMatchingRuleDescriptionSnapshot));
            Assert.That(previewJoined.MatchedValue, Is.EqualTo(realJoined.MatchedValue));
            Assert.That(preview.Inbound!.WouldJoinObjectMatchingRuleDescription, Is.EqualTo(realJoined.ObjectMatchingRuleDescriptionSnapshot));
            Assert.That(preview.Inbound.WouldJoinMatchedValue, Is.EqualTo("EMP001"));
        }
    }
}
