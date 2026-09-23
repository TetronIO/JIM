// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Pure unit tests for inbound Attribute Flow's generated-mapping path (Unique Value Generation, #242, Phase 2
/// work package F): a <see cref="SyncRuleMapping"/> whose <see cref="SyncRuleMapping.Generation"/> row is set
/// records a <see cref="PendingGeneratedValue"/> in place of a value, never performs I/O, and never calls
/// <c>ApplyNoValueOutcome</c> (a committed generated value must never be cleared or recomputed by its inputs,
/// FR 10, 29). The worker package that resolves a request into a real value is out of scope here; these tests
/// only cover what the synchronous engine records.
/// </summary>
public class SyncEngineGeneratedMappingTests
{
    private const int MvoTypeId = 10;
    private Application.Servers.SyncEngine _engine = null!;
    private DynamicExpressoEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
        _evaluator = new DynamicExpressoEvaluator();
    }

    private static MetaverseAttribute TargetAttr() => new() { Id = 100, Name = "Account Name", Type = AttributeDataType.Text };

    private static ConnectedSystemObjectType CsoType(int csoAttrId = 200, string name = "givenName") => new()
    {
        Id = 1,
        Attributes = [new ConnectedSystemObjectTypeAttribute { Id = csoAttrId, Name = name, Type = AttributeDataType.Text }]
    };

    private static ConnectedSystemObject JoinedCso(MetaverseObject mvo, string? givenName, int connectedSystemId = 5, int csoAttrId = 200, int typeId = 1)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = typeId, ConnectedSystemId = connectedSystemId, MetaverseObject = mvo };
        if (givenName != null)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = csoAttrId, StringValue = givenName });
        return cso;
    }

    /// <summary>A generated mapping targeting <paramref name="target"/>, with a base expression unless <paramref name="baseExpression"/> is null.</summary>
    private static SyncRuleMapping GeneratedMapping(
        MetaverseAttribute target,
        int syncRuleId = 1,
        int priority = int.MaxValue,
        string? baseExpression = "Lower(cs[\"givenName\"])",
        MissingInputBehaviour missingInputBehaviour = MissingInputBehaviour.ContributeNoValue,
        bool enabled = true,
        InboundCaseNormalisation caseNormalisation = InboundCaseNormalisation.None,
        InboundValueProcessing? inboundValueProcessing = null)
    {
        var mapping = new SyncRuleMapping
        {
            Id = syncRuleId,
            SyncRuleId = syncRuleId,
            TargetMetaverseAttribute = target,
            Priority = priority,
            Enabled = enabled,
            Generation = new SyncRuleMappingGeneration(),
            CaseNormalisation = caseNormalisation
        };
        if (inboundValueProcessing.HasValue)
            mapping.InboundValueProcessing = inboundValueProcessing.Value;
        if (baseExpression != null)
            mapping.Sources.Add(new SyncRuleMappingSource { Expression = baseExpression, Order = 1, MissingInputBehaviour = missingInputBehaviour });
        return mapping;
    }

    private static SyncRule GeneratedRule(SyncRuleMapping mapping, int syncRuleId = 1) => new()
    {
        Id = syncRuleId,
        MetaverseObjectTypeId = MvoTypeId,
        Direction = SyncRuleDirection.Import,
        Enabled = true,
        AttributeFlowRules = [mapping]
    };

    /// <summary>An ordinary (non-generated) attribute mapping/rule pair, for the priority-interaction tests.</summary>
    private static SyncRule OrdinaryAttributeRule(MetaverseAttribute target, int syncRuleId, int priority, int csoAttrId)
    {
        var mapping = new SyncRuleMapping { Id = syncRuleId, SyncRuleId = syncRuleId, TargetMetaverseAttribute = target, Priority = priority };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = csoAttrId, Order = 1 });
        return new SyncRule
        {
            Id = syncRuleId,
            MetaverseObjectTypeId = MvoTypeId,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            AttributeFlowRules = [mapping]
        };
    }

    [Test]
    public void FlowInboundAttributes_GeneratedMappingWithBaseExpression_RecordsPendingGenerationAndStagesNoAttributeValue()
    {
        var target = TargetAttr();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = JoinedCso(mvo, "Ada");
        var mapping = GeneratedMapping(target);
        var syncRule = GeneratedRule(mapping);

        _engine.FlowInboundAttributes(cso, syncRule, [CsoType()], _evaluator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingGeneratedValues, Has.Count.EqualTo(1));
            var request = mvo.PendingGeneratedValues.Single();
            Assert.That(request.Mapping, Is.SameAs(mapping));
            Assert.That(request.AttributeId, Is.EqualTo(target.Id));
            Assert.That(request.ContributedBySystemId, Is.EqualTo(cso.ConnectedSystemId));
            Assert.That(request.ContributedBySyncRuleId, Is.EqualTo(mapping.SyncRuleId));
            Assert.That(request.SourceConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(request.BaseValue, Is.EqualTo("ada"));
            Assert.That(request.BaseUnavailable, Is.False);
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty, "a generated mapping must never stage an ordinary attribute value");
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
        }
    }

    [Test]
    public void FlowInboundAttributes_GeneratedMappingWithNoBaseExpression_RecordsNullBaseValue()
    {
        // Valid for Sequence and Random tokens (SyncRuleMappingGenerationValidator rule 3): the mapping has zero
        // sources, so GetSourceType() still reports GeneratedMapping (checked before the empty-Sources case).
        var target = TargetAttr();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = JoinedCso(mvo, givenName: null);
        var mapping = GeneratedMapping(target, baseExpression: null);
        var syncRule = GeneratedRule(mapping);

        Assert.That(mapping.GetSourceType(), Is.EqualTo(SyncRuleMappingSourcesType.GeneratedMapping));

        _engine.FlowInboundAttributes(cso, syncRule, [CsoType()], _evaluator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingGeneratedValues, Has.Count.EqualTo(1));
            var request = mvo.PendingGeneratedValues.Single();
            Assert.That(request.BaseValue, Is.Null);
            Assert.That(request.BaseUnavailable, Is.False);
        }
    }

    [Test]
    public void FlowInboundAttributes_MissingInputContributeNoValue_RecordsBaseUnavailableAndStagesNoRemoval()
    {
        var target = TargetAttr();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = JoinedCso(mvo, givenName: null); // no value for cs["givenName"]: an absent input
        var mapping = GeneratedMapping(target, missingInputBehaviour: MissingInputBehaviour.ContributeNoValue);
        var syncRule = GeneratedRule(mapping);

        _engine.FlowInboundAttributes(cso, syncRule, [CsoType()], _evaluator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingGeneratedValues, Has.Count.EqualTo(1));
            var request = mvo.PendingGeneratedValues.Single();
            Assert.That(request.BaseUnavailable, Is.True);
            Assert.That(request.BaseValue, Is.Null);
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        }
    }

    [Test]
    public void FlowInboundAttributes_MissingInputContributeNoValueWithExistingCommittedValue_NeverAppliesNoValueOutcomeSoExistingValueSurvives()
    {
        // FR 10, 29: "contribute no value" for a generated mapping means wait, not withdraw. Seed an existing
        // (previously committed) value; an ordinary expression mapping in this same situation would call
        // ApplyNoValueOutcome and clear it (it is the sole contributor). Proving it survives untouched proves
        // ApplyNoValueOutcome was never reached for the generated path.
        var target = TargetAttr();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var existingValue = new MetaverseObjectAttributeValue { Attribute = target, AttributeId = target.Id, StringValue = "ada123" };
        mvo.AttributeValues.Add(existingValue);
        var cso = JoinedCso(mvo, givenName: null);
        var mapping = GeneratedMapping(target, missingInputBehaviour: MissingInputBehaviour.ContributeNoValue);
        var syncRule = GeneratedRule(mapping);

        _engine.FlowInboundAttributes(cso, syncRule, [CsoType()], _evaluator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.AttributeValues, Contains.Item(existingValue), "the committed value must never be recomputed from its inputs");
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty, "ApplyNoValueOutcome must never be called for a generated mapping");
            Assert.That(mvo.PendingGeneratedValues.Single().BaseUnavailable, Is.True);
        }
    }

    [Test]
    public void FlowInboundAttributes_FailMapping_RecordsErrorAndRecordsNoGeneration()
    {
        var target = TargetAttr();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = JoinedCso(mvo, givenName: null);
        var mapping = GeneratedMapping(target, missingInputBehaviour: MissingInputBehaviour.FailMapping);
        var syncRule = GeneratedRule(mapping);

        var errors = _engine.FlowInboundAttributes(cso, syncRule, [CsoType()], _evaluator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0].Kind, Is.EqualTo(AttributeFlowErrorKind.ExpressionMissingInput));
            Assert.That(errors[0].TargetAttributeName, Is.EqualTo("Account Name"));
            Assert.That(mvo.PendingGeneratedValues, Is.Empty);
        }
    }

    [Test]
    public void FlowInboundAttributes_FailObject_ThrowsSoTheWholeObjectIsLeftUntouched()
    {
        var target = TargetAttr();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = JoinedCso(mvo, givenName: null);
        var mapping = GeneratedMapping(target, missingInputBehaviour: MissingInputBehaviour.FailObject);
        var syncRule = GeneratedRule(mapping);

        Assert.That(() => _engine.FlowInboundAttributes(cso, syncRule, [CsoType()], _evaluator),
            Throws.TypeOf<SyncExpressionMissingInputException>());

        Assert.That(mvo.PendingGeneratedValues, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_ExpressionReturnsNull_RecordsBaseUnavailable()
    {
        var target = TargetAttr();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = JoinedCso(mvo, "Ada");
        var mapping = GeneratedMapping(target, missingInputBehaviour: MissingInputBehaviour.EvaluateAnyway);
        var syncRule = GeneratedRule(mapping);

        var mockEvaluator = new Mock<IExpressionEvaluator>();
        mockEvaluator.Setup(e => e.Evaluate(It.IsAny<string>(), It.IsAny<ExpressionContext>())).Returns((object?)null);

        _engine.FlowInboundAttributes(cso, syncRule, [CsoType()], mockEvaluator.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingGeneratedValues, Has.Count.EqualTo(1));
            var request = mvo.PendingGeneratedValues.Single();
            Assert.That(request.BaseUnavailable, Is.True);
            Assert.That(request.BaseValue, Is.Null);
        }
    }

    [Test]
    public void FlowInboundAttributes_ExpressionReturnsArray_RecordsGeneratedBaseNotSingleValueErrorAndNoRequest()
    {
        var target = TargetAttr();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = JoinedCso(mvo, "Ada");
        var mapping = GeneratedMapping(target, missingInputBehaviour: MissingInputBehaviour.EvaluateAnyway);
        var syncRule = GeneratedRule(mapping);

        var mockEvaluator = new Mock<IExpressionEvaluator>();
        mockEvaluator.Setup(e => e.Evaluate(It.IsAny<string>(), It.IsAny<ExpressionContext>())).Returns(new[] { "one", "two" });

        var errors = _engine.FlowInboundAttributes(cso, syncRule, [CsoType()], mockEvaluator.Object);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0].Kind, Is.EqualTo(AttributeFlowErrorKind.GeneratedBaseNotSingleValue));
            Assert.That(errors[0].TargetAttributeName, Is.EqualTo("Account Name"));
            Assert.That(errors[0].Expression, Is.EqualTo(mapping.Sources.Single().Expression));
            Assert.That(mvo.PendingGeneratedValues, Is.Empty);
        }
    }

    [Test]
    public void FlowInboundAttributes_SecondEvaluationInSamePass_ReplacesRatherThanDuplicates()
    {
        var target = TargetAttr();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var mapping = GeneratedMapping(target);
        var syncRule = GeneratedRule(mapping);

        var firstCso = JoinedCso(mvo, "Ada");
        _engine.FlowInboundAttributes(firstCso, syncRule, [CsoType()], _evaluator);
        Assert.That(mvo.PendingGeneratedValues, Has.Count.EqualTo(1));

        // Re-evaluate the same mapping again in the same pass (e.g. a re-flow), with a different base value.
        var secondCso = JoinedCso(mvo, "Bob", connectedSystemId: 5);
        _engine.FlowInboundAttributes(secondCso, syncRule, [CsoType()], _evaluator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingGeneratedValues, Has.Count.EqualTo(1), "the second evaluation must replace, not duplicate");
            Assert.That(mvo.PendingGeneratedValues.Single().BaseValue, Is.EqualTo("bob"));
            Assert.That(mvo.PendingGeneratedValues.Single().SourceConnectedSystemObjectId, Is.EqualTo(secondCso.Id));
        }
    }

    [Test]
    public void FlowInboundAttributes_DisabledGeneratedMapping_RecordsNothing()
    {
        var target = TargetAttr();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = JoinedCso(mvo, "Ada");
        var mapping = GeneratedMapping(target, enabled: false);
        var syncRule = GeneratedRule(mapping);

        var errors = _engine.FlowInboundAttributes(cso, syncRule, [CsoType()], _evaluator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(mvo.PendingGeneratedValues, Is.Empty);
        }
    }

    [Test]
    public void FlowInboundAttributes_InboundValueProcessing_TrimsAndCaseNormalisesTheBaseValue()
    {
        var target = TargetAttr();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = JoinedCso(mvo, "Ada");
        var mapping = GeneratedMapping(target,
            missingInputBehaviour: MissingInputBehaviour.EvaluateAnyway,
            caseNormalisation: InboundCaseNormalisation.Upper,
            inboundValueProcessing: InboundValueProcessing.TrimWhitespace | InboundValueProcessing.TreatWhitespaceAsNoValue);
        var syncRule = GeneratedRule(mapping);

        var mockEvaluator = new Mock<IExpressionEvaluator>();
        mockEvaluator.Setup(e => e.Evaluate(It.IsAny<string>(), It.IsAny<ExpressionContext>())).Returns("  ada  ");

        _engine.FlowInboundAttributes(cso, syncRule, [CsoType()], mockEvaluator.Object);

        Assert.That(mvo.PendingGeneratedValues.Single().BaseValue, Is.EqualTo("ADA"));
    }

    [Test]
    public void FlowInboundAttributes_HigherPriorityOrdinaryContributionInSamePass_RemovesThePendingGeneration()
    {
        // FR 6: a higher-priority ordinary contribution evaluated after a lower-priority generated mapping, in
        // the same pass, visibly supersedes the pending generation.
        var target = TargetAttr();
        var generatedRule = GeneratedRule(GeneratedMapping(target, syncRuleId: 2, priority: 2), syncRuleId: 2);
        var ordinaryRule = OrdinaryAttributeRule(target, syncRuleId: 1, priority: 1, csoAttrId: 201);
        var context = new AttributePriorityContext([generatedRule, ordinaryRule]);

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var generatedCso = JoinedCso(mvo, "Ada", connectedSystemId: 5);
        _engine.FlowInboundAttributes(generatedCso, generatedRule, [CsoType()], _evaluator, priorityContext: context);
        Assert.That(mvo.PendingGeneratedValues, Has.Count.EqualTo(1));

        var ordinaryCsoType = new ConnectedSystemObjectType { Id = 2, Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 201, Name = "accountName", Type = AttributeDataType.Text }] };
        var ordinaryCso = JoinedCso(mvo, "IT", connectedSystemId: 6, csoAttrId: 201, typeId: 2);
        _engine.FlowInboundAttributes(ordinaryCso, ordinaryRule, [ordinaryCsoType], priorityContext: context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingGeneratedValues, Is.Empty, "the higher-priority ordinary contribution supersedes the pending generation");
            Assert.That(mvo.PendingAttributeValueAdditions.Single().StringValue, Is.EqualTo("IT"));
        }
    }

    [Test]
    public void FlowInboundAttributes_LowerPriorityOrdinaryContributionEvaluatedAfterGeneratedMapping_IsBlockedByTheGate()
    {
        // The generated mapping is the higher-priority incumbent (via FindEffectiveIncumbentSyncRuleId reading
        // the pending generation), so a lower-priority ordinary contribution evaluated later in the same pass
        // must lose the gate, exactly as it would against an ordinary higher-priority incumbent value.
        var target = TargetAttr();
        var generatedRule = GeneratedRule(GeneratedMapping(target, syncRuleId: 1, priority: 1), syncRuleId: 1);
        var ordinaryRule = OrdinaryAttributeRule(target, syncRuleId: 2, priority: 2, csoAttrId: 201);
        var context = new AttributePriorityContext([generatedRule, ordinaryRule]);

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var generatedCso = JoinedCso(mvo, "Ada", connectedSystemId: 5);
        _engine.FlowInboundAttributes(generatedCso, generatedRule, [CsoType()], _evaluator, priorityContext: context);
        Assert.That(mvo.PendingGeneratedValues, Has.Count.EqualTo(1));

        var ordinaryCsoType = new ConnectedSystemObjectType { Id = 2, Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 201, Name = "accountName", Type = AttributeDataType.Text }] };
        var ordinaryCso = JoinedCso(mvo, "IT", connectedSystemId: 6, csoAttrId: 201, typeId: 2);
        _engine.FlowInboundAttributes(ordinaryCso, ordinaryRule, [ordinaryCsoType], priorityContext: context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty, "the lower-priority ordinary contribution must not be written");
            Assert.That(mvo.PendingGeneratedValues, Has.Count.EqualTo(1), "the pending generation is untouched by a blocked contribution");
        }
    }
}
