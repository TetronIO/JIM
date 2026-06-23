// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Tests for per-mapping inbound value processing (#843): whitespace-as-no-value, trim,
/// collapse-internal-whitespace and case normalisation applied to text attribute values as they
/// flow from a Connected System Object to a Metaverse Object. Exercised through the public
/// SyncEngine.FlowInboundAttributes entry point (direct attribute flow and expression flow), plus
/// direct unit tests of the pure ApplyInboundTextProcessing helper.
/// </summary>
public class SyncEngineValueProcessingTests
{
    private Application.Servers.SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
    }

    // ─── Direct attribute flow ───

    /// <summary>
    /// Builds a single-valued text import mapping from CS attribute 200 to MV attribute 100, with the CSO
    /// carrying the supplied source values and (optionally) an existing MVO value.
    /// </summary>
    private static (ConnectedSystemObject Cso, MetaverseObject Mvo, SyncRule SyncRule, ConnectedSystemObjectType CsoType, MetaverseObjectAttributeValue? Existing)
        BuildDirectScenario(
            string?[] sourceValues,
            InboundValueProcessing processing,
            InboundCaseNormalisation caseNormalisation,
            AttributePlurality targetPlurality = AttributePlurality.SingleValued,
            string? existingMvoValue = null)
    {
        var mvoAttr = new MetaverseAttribute { Id = 100, Name = "displayName", Type = AttributeDataType.Text, AttributePlurality = targetPlurality };
        var csoAttr = new ConnectedSystemObjectTypeAttribute { Id = 200, Name = "cn", Type = AttributeDataType.Text };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttr] };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        MetaverseObjectAttributeValue? existing = null;
        if (existingMvoValue != null)
        {
            existing = new MetaverseObjectAttributeValue { Attribute = mvoAttr, AttributeId = 100, StringValue = existingMvoValue };
            mvo.AttributeValues.Add(existing);
        }

        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo };
        foreach (var v in sourceValues)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, StringValue = v });

        var mapping = new SyncRuleMapping
        {
            TargetMetaverseAttribute = mvoAttr,
            InboundValueProcessing = processing,
            CaseNormalisation = caseNormalisation
        };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = 200, ConnectedSystemAttribute = csoAttr, Order = 1 });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        return (cso, mvo, syncRule, csoType, existing);
    }

    [Test]
    public void FlowInbound_WhitespaceOnly_DefaultTreatAsNoValue_DoesNotFlowAndClearsExisting()
    {
        var (cso, mvo, syncRule, csoType, existing) = BuildDirectScenario(
            ["   "], InboundValueProcessing.TreatWhitespaceAsNoValue, InboundCaseNormalisation.None, existingMvoValue: "old");

        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty, "Whitespace-only value must not flow.");
        Assert.That(mvo.PendingAttributeValueRemovals, Contains.Item(existing), "Existing value must be cleared when the source collapses to no value.");
    }

    [Test]
    public void FlowInbound_WhitespaceOnly_ProcessingNone_FlowsAsLiteral()
    {
        var (cso, mvo, syncRule, csoType, _) = BuildDirectScenario(
            ["  "], InboundValueProcessing.None, InboundCaseNormalisation.None);

        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().StringValue, Is.EqualTo("  "), "With no processing, whitespace flows as a literal value.");
    }

    [Test]
    public void FlowInbound_EmptyString_DefaultTreatAsNoValue_DoesNotFlow()
    {
        var (cso, mvo, syncRule, csoType, _) = BuildDirectScenario(
            [""], InboundValueProcessing.TreatWhitespaceAsNoValue, InboundCaseNormalisation.None);

        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty, "Empty string is treated as no value too.");
    }

    [Test]
    public void FlowInbound_Trim_RemovesLeadingTrailingWhitespace()
    {
        var (cso, mvo, syncRule, csoType, _) = BuildDirectScenario(
            [" John "], InboundValueProcessing.TreatWhitespaceAsNoValue | InboundValueProcessing.TrimWhitespace, InboundCaseNormalisation.None);

        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions.Single().StringValue, Is.EqualTo("John"));
    }

    [Test]
    public void FlowInbound_CollapseInternalWhitespace_CollapsesRuns()
    {
        var (cso, mvo, syncRule, csoType, _) = BuildDirectScenario(
            ["John   Smith"], InboundValueProcessing.CollapseInternalWhitespace, InboundCaseNormalisation.None);

        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions.Single().StringValue, Is.EqualTo("John Smith"));
    }

    [Test]
    public void FlowInbound_CaseLower_NormalisesToLowerCase()
    {
        var (cso, mvo, syncRule, csoType, _) = BuildDirectScenario(
            ["ALICE@Example.COM"], InboundValueProcessing.None, InboundCaseNormalisation.Lower);

        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions.Single().StringValue, Is.EqualTo("alice@example.com"));
    }

    [Test]
    public void FlowInbound_CaseTitle_NormalisesToTitleCase()
    {
        var (cso, mvo, syncRule, csoType, _) = BuildDirectScenario(
            ["alICE smith"], InboundValueProcessing.None, InboundCaseNormalisation.Title);

        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions.Single().StringValue, Is.EqualTo("Alice Smith"));
    }

    [Test]
    public void FlowInbound_CombinedTransforms_AppliedInCanonicalOrder()
    {
        // trim -> collapse internal -> title case -> (not empty, so flows)
        var (cso, mvo, syncRule, csoType, _) = BuildDirectScenario(
            ["  aLICE   SMITH  "],
            InboundValueProcessing.TrimWhitespace | InboundValueProcessing.CollapseInternalWhitespace | InboundValueProcessing.TreatWhitespaceAsNoValue,
            InboundCaseNormalisation.Title);

        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions.Single().StringValue, Is.EqualTo("Alice Smith"));
    }

    [Test]
    public void FlowInbound_RealValue_DefaultProcessing_FlowsUnchanged()
    {
        var (cso, mvo, syncRule, csoType, _) = BuildDirectScenario(
            ["John Doe"], InboundValueProcessing.TreatWhitespaceAsNoValue, InboundCaseNormalisation.None);

        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions.Single().StringValue, Is.EqualTo("John Doe"));
    }

    [Test]
    public void FlowInbound_Mva_DropsWhitespaceEntriesAndTrimsTheRest()
    {
        var (cso, mvo, syncRule, csoType, _) = BuildDirectScenario(
            [" a ", "   ", "b"],
            InboundValueProcessing.TreatWhitespaceAsNoValue | InboundValueProcessing.TrimWhitespace,
            InboundCaseNormalisation.None,
            targetPlurality: AttributePlurality.MultiValued);

        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        var added = mvo.PendingAttributeValueAdditions.Select(v => v.StringValue).OrderBy(v => v).ToList();
        Assert.That(added, Is.EqualTo(new[] { "a", "b" }), "Whitespace-only MVA entries are dropped; the rest are trimmed.");
    }

    [Test]
    public void FlowInbound_NumberAttribute_UnaffectedByValueProcessing()
    {
        // Value processing is text-only; a number mapping must behave identically regardless of the flags.
        var mvoAttr = new MetaverseAttribute { Id = 100, Name = "employeeNumber", Type = AttributeDataType.Number };
        var csoAttr = new ConnectedSystemObjectTypeAttribute { Id = 200, Name = "num", Type = AttributeDataType.Number };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttr] };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, IntValue = 42 });

        var mapping = new SyncRuleMapping
        {
            TargetMetaverseAttribute = mvoAttr,
            InboundValueProcessing = InboundValueProcessing.TreatWhitespaceAsNoValue | InboundValueProcessing.TrimWhitespace,
            CaseNormalisation = InboundCaseNormalisation.Upper
        };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = 200, ConnectedSystemAttribute = csoAttr, Order = 1 });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions.Single().IntValue, Is.EqualTo(42));
    }

    // ─── Expression flow ───

    private static (ConnectedSystemObject Cso, MetaverseObject Mvo, SyncRule SyncRule, ConnectedSystemObjectType CsoType, MetaverseObjectAttributeValue Existing)
        BuildExpressionScenario(InboundValueProcessing processing, InboundCaseNormalisation caseNormalisation)
    {
        var mvoAttr = new MetaverseAttribute { Id = 100, Name = "displayName", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };
        var csoAttr = new ConnectedSystemObjectTypeAttribute { Id = 200, Name = "cn", Type = AttributeDataType.Text };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttr] };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var existing = new MetaverseObjectAttributeValue { Attribute = mvoAttr, AttributeId = 100, StringValue = "Original" };
        mvo.AttributeValues.Add(existing);

        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, StringValue = "ignored" });

        var mapping = new SyncRuleMapping
        {
            TargetMetaverseAttribute = mvoAttr,
            InboundValueProcessing = processing,
            CaseNormalisation = caseNormalisation
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "anything", Order = 1 });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        return (cso, mvo, syncRule, csoType, existing);
    }

    [Test]
    public void FlowInbound_ExpressionScalarWhitespace_DefaultTreatAsNoValue_ClearsExisting()
    {
        var (cso, mvo, syncRule, csoType, existing) = BuildExpressionScenario(InboundValueProcessing.TreatWhitespaceAsNoValue, InboundCaseNormalisation.None);
        var evaluator = new Mock<IExpressionEvaluator>();
        evaluator.Setup(e => e.Evaluate(It.IsAny<string>(), It.IsAny<ExpressionContext>())).Returns("   ");

        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType }, evaluator.Object);

        Assert.That(mvo.PendingAttributeValueRemovals, Contains.Item(existing), "A whitespace expression result clears the existing value when treat-as-no-value is on.");
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
    }

    [Test]
    public void FlowInbound_ExpressionScalar_TrimAndLower()
    {
        var (cso, mvo, syncRule, csoType, _) = BuildExpressionScenario(InboundValueProcessing.TrimWhitespace, InboundCaseNormalisation.Lower);
        var evaluator = new Mock<IExpressionEvaluator>();
        evaluator.Setup(e => e.Evaluate(It.IsAny<string>(), It.IsAny<ExpressionContext>())).Returns(" BOB ");

        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType }, evaluator.Object);

        Assert.That(mvo.PendingAttributeValueAdditions.Single().StringValue, Is.EqualTo("bob"));
    }

    [Test]
    public void FlowInbound_ExpressionArray_DropsWhitespaceEntries()
    {
        var (cso, mvo, syncRule, csoType, existing) = BuildExpressionScenario(InboundValueProcessing.TreatWhitespaceAsNoValue, InboundCaseNormalisation.None);
        var evaluator = new Mock<IExpressionEvaluator>();
        evaluator.Setup(e => e.Evaluate(It.IsAny<string>(), It.IsAny<ExpressionContext>())).Returns(new[] { "x", "  ", "y" });

        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType }, evaluator.Object);

        var added = mvo.PendingAttributeValueAdditions.Select(v => v.StringValue).OrderBy(v => v).ToList();
        Assert.That(added, Is.EqualTo(new[] { "x", "y" }));
        Assert.That(mvo.PendingAttributeValueRemovals, Contains.Item(existing), "The original 'Original' value is not in the new set, so it is removed.");
    }

    // ─── Pure helper unit tests ───

    [TestCase("  ", InboundValueProcessing.TreatWhitespaceAsNoValue, InboundCaseNormalisation.None, null)]
    [TestCase("", InboundValueProcessing.TreatWhitespaceAsNoValue, InboundCaseNormalisation.None, null)]
    [TestCase("  ", InboundValueProcessing.None, InboundCaseNormalisation.None, "  ")]
    [TestCase(" John ", InboundValueProcessing.TrimWhitespace, InboundCaseNormalisation.None, "John")]
    [TestCase("John   Smith", InboundValueProcessing.CollapseInternalWhitespace, InboundCaseNormalisation.None, "John Smith")]
    [TestCase(" John  Smith ", InboundValueProcessing.TrimWhitespace | InboundValueProcessing.CollapseInternalWhitespace, InboundCaseNormalisation.None, "John Smith")]
    [TestCase("AlIcE", InboundValueProcessing.None, InboundCaseNormalisation.Lower, "alice")]
    [TestCase("alice", InboundValueProcessing.None, InboundCaseNormalisation.Upper, "ALICE")]
    [TestCase("alice smith", InboundValueProcessing.None, InboundCaseNormalisation.Title, "Alice Smith")]
    [TestCase("   ", InboundValueProcessing.TrimWhitespace | InboundValueProcessing.TreatWhitespaceAsNoValue, InboundCaseNormalisation.None, null)]
    public void ApplyInboundTextProcessing_ProducesExpectedResult(
        string input, InboundValueProcessing processing, InboundCaseNormalisation caseNormalisation, string? expected)
    {
        var result = Application.Servers.SyncEngine.ApplyInboundTextProcessing(input, processing, caseNormalisation);
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void ApplyInboundTextProcessing_NullInput_ReturnsNull()
    {
        Assert.That(Application.Servers.SyncEngine.ApplyInboundTextProcessing(null, InboundValueProcessing.None, InboundCaseNormalisation.None), Is.Null);
    }
}
