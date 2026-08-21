// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Logic;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// A disabled mapping (#1485) must be invisible to attribute priority: left in the contributor list it would
/// inflate the contributor count (forcing the slow multi-contributor path) and could be elected as a surviving
/// contributor whose values never actually flow.
/// </summary>
[TestFixture]
public class AttributePriorityContextDisabledMappingTests
{
    private const int MetaverseObjectTypeId = 1;
    private const int AttributeId = 100;

    [Test]
    public void AttributePriorityContext_DisabledMapping_IsNotACountedContributor()
    {
        var context = new AttributePriorityContext(BuildRules(secondMappingEnabled: false));

        Assert.That(context.GetContributorCount(MetaverseObjectTypeId, AttributeId), Is.EqualTo(1),
            "A disabled mapping contributes nothing, so it must not be counted as a contributor.");
    }

    [Test]
    public void AttributePriorityContext_EnabledMappings_AreBothCounted()
    {
        // Regression guard on the same arrangement: the filter must key on Enabled, not thin every list.
        var context = new AttributePriorityContext(BuildRules(secondMappingEnabled: true));

        Assert.That(context.GetContributorCount(MetaverseObjectTypeId, AttributeId), Is.EqualTo(2));
    }

    private static List<SyncRule> BuildRules(bool secondMappingEnabled)
    {
        var mvAttr = new MetaverseAttribute { Id = AttributeId, Name = "displayName", Type = AttributeDataType.Text };

        var firstRule = new SyncRule
        {
            Id = 10,
            Name = "HR Inbound",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            MetaverseObjectTypeId = MetaverseObjectTypeId
        };
        firstRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 1,
            SyncRuleId = firstRule.Id,
            TargetMetaverseAttribute = mvAttr,
            Priority = 1
        });

        var secondRule = new SyncRule
        {
            Id = 20,
            Name = "Directory Inbound",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            MetaverseObjectTypeId = MetaverseObjectTypeId
        };
        secondRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 2,
            SyncRuleId = secondRule.Id,
            TargetMetaverseAttribute = mvAttr,
            Priority = 2,
            Enabled = secondMappingEnabled
        });

        return [firstRule, secondRule];
    }
}
