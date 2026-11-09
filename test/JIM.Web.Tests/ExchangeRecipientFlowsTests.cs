// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Exchange;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// What the Exchange recipients panel on an export Synchronisation Rule's Attribute Flow tab shows (#2021, PRD requirement
/// 4): one row per Exchange attribute the rule's Object Type carries, each with the Attribute Flow that sets it or what
/// happens without one. The panel stores nothing; it reads the rule's own flows, by their target alone.
/// </summary>
[TestFixture]
public class ExchangeRecipientFlowsTests
{
    private int _nextId = 100;

    [Test]
    public void For_ExportRuleWithTheExchangeAttributes_ListsOneRowPerAttributeInTheConnectorsOrder()
    {
        var rule = ExportRule("user", ExchangeRecipientAttributes.All.Reverse().ToArray());

        var flows = ExchangeRecipientFlows.For(rule);

        Assert.That(flows!.Rows.Select(r => r.Attribute.Name), Is.EqualTo(ExchangeRecipientAttributes.All));
    }

    [Test]
    public void For_ObjectTypeCarryingSomeOfThem_ListsOnlyThoseItCarries()
    {
        // The Connector adds no Database or External Address to a group: a group has no mailbox and no external address.
        var rule = ExportRule("group", ExchangeRecipientAttributes.RecipientType, ExchangeRecipientAttributes.Alias,
            ExchangeRecipientAttributes.PrimarySmtpAddress);

        var flows = ExchangeRecipientFlows.For(rule);

        Assert.That(flows!.Rows.Select(r => r.Attribute.Name), Is.EqualTo(new[]
        {
            ExchangeRecipientAttributes.RecipientType, ExchangeRecipientAttributes.Alias, ExchangeRecipientAttributes.PrimarySmtpAddress
        }));
    }

    [Test]
    public void For_ImportRule_IsNotShown()
    {
        var rule = ExportRule("user", [.. ExchangeRecipientAttributes.All]);
        rule.Direction = SyncRuleDirection.Import;

        Assert.That(ExchangeRecipientFlows.For(rule), Is.Null);
    }

    [Test]
    public void For_ObjectTypeWithoutExchangeAttributes_IsNotShown()
    {
        // Schema discovery adds them only while the Connected System manages Exchange recipients.
        Assert.That(ExchangeRecipientFlows.For(ExportRule("user")), Is.Null);
    }

    [Test]
    public void For_RuleWithNoObjectTypeYet_IsNotShown()
    {
        var rule = new SyncRule { Direction = SyncRuleDirection.Export, ConnectedSystemObjectType = null! };

        Assert.That(ExchangeRecipientFlows.For(rule), Is.Null);
    }

    [Test]
    public void For_DirectoryAttributeThatSharesAName_IsNotAnExchangeAttribute()
    {
        var rule = ExportRule("user");
        rule.ConnectedSystemObjectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Id = _nextId++, Name = ExchangeRecipientAttributes.Alias, Type = AttributeDataType.Text, IsConnectorDefined = false
        });

        Assert.That(ExchangeRecipientFlows.For(rule), Is.Null);
    }

    [Test]
    public void For_ConnectorDefinedAttributeThatIsNotAnExchangeOne_IsLeftOut()
    {
        var rule = ExportRule("user", ExchangeRecipientAttributes.RecipientType);
        rule.ConnectedSystemObjectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Id = _nextId++, Name = "Some Other Connector Attribute", Type = AttributeDataType.Text, IsConnectorDefined = true
        });

        var flows = ExchangeRecipientFlows.For(rule);

        Assert.That(flows!.Rows.Select(r => r.Attribute.Name), Is.EqualTo(new[] { ExchangeRecipientAttributes.RecipientType }));
    }

    [Test]
    public void For_NoFlowToAnyOfThem_IsNotSetUpAndEachRowSaysWhatHappensWithoutOne()
    {
        var flows = ExchangeRecipientFlows.For(ExportRule("user", [.. ExchangeRecipientAttributes.All]))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flows.IsSetUp, Is.False);
            Assert.That(flows.Flows, Is.Empty);
            Assert.That(flows.Rows, Has.All.Matches<ExchangeRecipientFlowRow>(r => r.Mapping == null));
            Assert.That(flows.Rows, Has.All.Matches<ExchangeRecipientFlowRow>(r => !string.IsNullOrWhiteSpace(r.WithoutFlow)),
                "every attribute says what happens when no flow sets it");
            Assert.That(flows.Rows.Select(r => r.WithoutFlow).Distinct().Count(), Is.EqualTo(flows.Rows.Count),
                "each says its own thing");
        }
    }

    [Test]
    public void For_FlowToOneOfThem_IsSetUpAndThatRowCarriesTheFlow()
    {
        var rule = ExportRule("user", [.. ExchangeRecipientAttributes.All]);
        var alias = Attribute(rule, ExchangeRecipientAttributes.Alias);
        var flow = new SyncRuleMapping { Id = 7, TargetConnectedSystemAttribute = alias, TargetConnectedSystemAttributeId = alias.Id };
        rule.AttributeFlowRules.Add(flow);
        rule.AttributeFlowRules.Add(new SyncRuleMapping { Id = 8, TargetConnectedSystemAttribute = DirectoryAttribute(rule, "displayName") });

        var flows = ExchangeRecipientFlows.For(rule)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flows.IsSetUp, Is.True);
            Assert.That(flows.Rows.Single(r => r.Attribute == alias).Mapping, Is.SameAs(flow));
            Assert.That(flows.Rows.Where(r => r.Attribute != alias), Has.All.Matches<ExchangeRecipientFlowRow>(r => r.Mapping == null));
            Assert.That(flows.Flows, Is.EqualTo(new[] { flow }), "only the Exchange flows; displayName is not the panel's");
        }
    }

    [Test]
    public void For_FlowAddedThisSessionWithOnlyTheAttributeId_IsFoundByIt()
    {
        var rule = ExportRule("user", [.. ExchangeRecipientAttributes.All]);
        var database = Attribute(rule, ExchangeRecipientAttributes.Database);
        var flow = new SyncRuleMapping { TargetConnectedSystemAttributeId = database.Id };
        rule.AttributeFlowRules.Add(flow);

        var flows = ExchangeRecipientFlows.For(rule)!;

        Assert.That(flows.Rows.Single(r => r.Attribute == database).Mapping, Is.SameAs(flow));
    }

    [Test]
    public void For_DisabledFlow_StillSetsItsAttribute()
    {
        var rule = ExportRule("user", [.. ExchangeRecipientAttributes.All]);
        var recipientType = Attribute(rule, ExchangeRecipientAttributes.RecipientType);
        var flow = new SyncRuleMapping { Id = 9, TargetConnectedSystemAttribute = recipientType, Enabled = false };
        rule.AttributeFlowRules.Add(flow);

        var flows = ExchangeRecipientFlows.For(rule)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flows.Rows.Single(r => r.Attribute == recipientType).Mapping, Is.SameAs(flow));
            Assert.That(flows.IsSetUp, Is.True, "a disabled flow is still this rule's flow, and Stop removes it with the rest");
        }
    }

    [Test]
    public void For_FlowToEveryAttribute_ListsEveryFlowToRemoveOnStop()
    {
        var rule = ExportRule("user", [.. ExchangeRecipientAttributes.All]);
        var all = rule.ConnectedSystemObjectType.Attributes
            .Select(a => new SyncRuleMapping { Id = _nextId++, TargetConnectedSystemAttribute = a, TargetConnectedSystemAttributeId = a.Id })
            .ToList();
        rule.AttributeFlowRules.AddRange(all);

        var flows = ExchangeRecipientFlows.For(rule)!;

        Assert.That(flows.Flows, Is.EquivalentTo(all));
    }

    private SyncRule ExportRule(string objectTypeName, params string[] exchangeAttributes)
    {
        var objectType = new ConnectedSystemObjectType { Id = _nextId++, Name = objectTypeName };
        foreach (var name in exchangeAttributes)
        {
            objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
            {
                Id = _nextId++, Name = name, Type = AttributeDataType.Text, IsConnectorDefined = true, ConnectedSystemObjectType = objectType
            });
        }

        return new SyncRule { Direction = SyncRuleDirection.Export, ConnectedSystemObjectType = objectType, ConnectedSystemObjectTypeId = objectType.Id };
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(SyncRule rule, string name) =>
        rule.ConnectedSystemObjectType.Attributes.Single(a => a.Name == name);

    private ConnectedSystemObjectTypeAttribute DirectoryAttribute(SyncRule rule, string name)
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Id = _nextId++, Name = name, Type = AttributeDataType.Text };
        rule.ConnectedSystemObjectType.Attributes.Add(attribute);
        return attribute;
    }
}
