using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Tests for ExportEvaluationServer.HasRelevantChangedAttributes — determines whether
/// the current sync's changed MVO attributes are relevant to a given export rule.
/// Used to avoid replacing an existing Create PE on a PendingProvisioning CSO when
/// the triggering sync's changes don't map to the export rule.
/// </summary>
[TestFixture]
public class HasRelevantChangedAttributesTests
{
    private MetaverseAttribute _firstNameAttr = null!;
    private MetaverseAttribute _lastNameAttr = null!;
    private MetaverseAttribute _trainingStatusAttr = null!;
    private MetaverseAttribute _emailAttr = null!;

    [SetUp]
    public void Setup()
    {
        _firstNameAttr = new MetaverseAttribute { Id = 1, Name = "First Name" };
        _lastNameAttr = new MetaverseAttribute { Id = 2, Name = "Last Name" };
        _trainingStatusAttr = new MetaverseAttribute { Id = 3, Name = "Training Status" };
        _emailAttr = new MetaverseAttribute { Id = 4, Name = "Email" };
    }

    [Test]
    public void HasRelevantChangedAttributes_EmptyChangedAttributes_ReturnsFalse()
    {
        var exportRule = CreateExportRuleWithDirectMappings(_firstNameAttr, _lastNameAttr);
        var changedAttributes = new List<MetaverseObjectAttributeValue>();

        var result = ExportEvaluationServer.HasRelevantChangedAttributes(changedAttributes, exportRule);

        Assert.That(result, Is.False);
    }

    [Test]
    public void HasRelevantChangedAttributes_DirectMappingMatches_ReturnsTrue()
    {
        // Export rule maps First Name and Last Name
        var exportRule = CreateExportRuleWithDirectMappings(_firstNameAttr, _lastNameAttr);
        // Changed attributes include First Name
        var changedAttributes = new List<MetaverseObjectAttributeValue>
        {
            new() { AttributeId = _firstNameAttr.Id, Attribute = _firstNameAttr, StringValue = "John" }
        };

        var result = ExportEvaluationServer.HasRelevantChangedAttributes(changedAttributes, exportRule);

        Assert.That(result, Is.True);
    }

    [Test]
    public void HasRelevantChangedAttributes_NoDirectMappingMatches_ReturnsFalse()
    {
        // Export rule maps First Name and Last Name
        var exportRule = CreateExportRuleWithDirectMappings(_firstNameAttr, _lastNameAttr);
        // Changed attributes are Training Status (not mapped by this rule)
        var changedAttributes = new List<MetaverseObjectAttributeValue>
        {
            new() { AttributeId = _trainingStatusAttr.Id, Attribute = _trainingStatusAttr, StringValue = "Pass" }
        };

        var result = ExportEvaluationServer.HasRelevantChangedAttributes(changedAttributes, exportRule);

        Assert.That(result, Is.False);
    }

    [Test]
    public void HasRelevantChangedAttributes_ExpressionMapping_AlwaysReturnsTrue()
    {
        // Export rule has an expression-based mapping (e.g., DN construction)
        var exportRule = CreateExportRuleWithExpressionMapping(
            "\"CN=\" + EscapeDN(mv[\"First Name\"]) + \",OU=Users\"");
        // Changed attributes are Training Status — not directly referenced, but expressions
        // may depend on any attribute, so we conservatively treat as relevant
        var changedAttributes = new List<MetaverseObjectAttributeValue>
        {
            new() { AttributeId = _trainingStatusAttr.Id, Attribute = _trainingStatusAttr, StringValue = "Pass" }
        };

        var result = ExportEvaluationServer.HasRelevantChangedAttributes(changedAttributes, exportRule);

        Assert.That(result, Is.True);
    }

    [Test]
    public void HasRelevantChangedAttributes_MixedMappings_MatchesDirectMapping()
    {
        // Export rule has both direct and expression mappings
        var exportRule = CreateExportRuleWithMixedMappings(_emailAttr, "ToUpper(mv[\"First Name\"])");
        // Changed attribute is Email which matches the direct mapping
        var changedAttributes = new List<MetaverseObjectAttributeValue>
        {
            new() { AttributeId = _emailAttr.Id, Attribute = _emailAttr, StringValue = "test@panoply.org" }
        };

        var result = ExportEvaluationServer.HasRelevantChangedAttributes(changedAttributes, exportRule);

        Assert.That(result, Is.True);
    }

    [Test]
    public void HasRelevantChangedAttributes_MultipleChangedAttributes_OneMatches()
    {
        // Export rule maps First Name only
        var exportRule = CreateExportRuleWithDirectMappings(_firstNameAttr);
        // Changed attributes include Training Status and First Name
        var changedAttributes = new List<MetaverseObjectAttributeValue>
        {
            new() { AttributeId = _trainingStatusAttr.Id, Attribute = _trainingStatusAttr, StringValue = "Pass" },
            new() { AttributeId = _firstNameAttr.Id, Attribute = _firstNameAttr, StringValue = "Jane" }
        };

        var result = ExportEvaluationServer.HasRelevantChangedAttributes(changedAttributes, exportRule);

        Assert.That(result, Is.True);
    }

    [Test]
    public void HasRelevantChangedAttributes_EmptyExportRule_ReturnsFalse()
    {
        // Export rule with no attribute flow rules
        var exportRule = new SyncRule
        {
            Id = 1,
            Name = "Empty Export Rule",
            Direction = SyncRuleDirection.Export,
            AttributeFlowRules = []
        };
        var changedAttributes = new List<MetaverseObjectAttributeValue>
        {
            new() { AttributeId = _firstNameAttr.Id, Attribute = _firstNameAttr, StringValue = "John" }
        };

        var result = ExportEvaluationServer.HasRelevantChangedAttributes(changedAttributes, exportRule);

        Assert.That(result, Is.False);
    }

    #region Test Helpers

    private static SyncRule CreateExportRuleWithDirectMappings(params MetaverseAttribute[] sourceAttributes)
    {
        var rule = new SyncRule
        {
            Id = 1,
            Name = "Test Export Rule",
            Direction = SyncRuleDirection.Export,
            AttributeFlowRules = []
        };

        var csAttrId = 100;
        foreach (var mvAttr in sourceAttributes)
        {
            var mapping = new SyncRuleMapping
            {
                TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = csAttrId++, Name = $"cs_{mvAttr.Name}" }
            };
            mapping.Sources.Add(new SyncRuleMappingSource
            {
                MetaverseAttribute = mvAttr,
                MetaverseAttributeId = mvAttr.Id
            });
            rule.AttributeFlowRules.Add(mapping);
        }

        return rule;
    }

    private static SyncRule CreateExportRuleWithExpressionMapping(string expression)
    {
        var rule = new SyncRule
        {
            Id = 1,
            Name = "Test Export Rule",
            Direction = SyncRuleDirection.Export,
            AttributeFlowRules = []
        };

        var mapping = new SyncRuleMapping
        {
            TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 100, Name = "dn" }
        };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            Expression = expression
        });
        rule.AttributeFlowRules.Add(mapping);

        return rule;
    }

    private static SyncRule CreateExportRuleWithMixedMappings(MetaverseAttribute directAttr, string expression)
    {
        var rule = new SyncRule
        {
            Id = 1,
            Name = "Test Export Rule",
            Direction = SyncRuleDirection.Export,
            AttributeFlowRules = []
        };

        // Direct mapping
        var directMapping = new SyncRuleMapping
        {
            TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 100, Name = "email" }
        };
        directMapping.Sources.Add(new SyncRuleMappingSource
        {
            MetaverseAttribute = directAttr,
            MetaverseAttributeId = directAttr.Id
        });
        rule.AttributeFlowRules.Add(directMapping);

        // Expression mapping
        var exprMapping = new SyncRuleMapping
        {
            TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "displayName" }
        };
        exprMapping.Sources.Add(new SyncRuleMappingSource
        {
            Expression = expression
        });
        rule.AttributeFlowRules.Add(exprMapping);

        return rule;
    }

    #endregion
}
