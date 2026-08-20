// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Missing Input Behaviour on inbound Expression-based Attribute Flow (#1361).
/// </summary>
/// <remarks>
/// An Expression whose input is absent does not fail: it evaluates cleanly and returns a structurally broken
/// value ("ada.@corp.local" for a person with no surname) that no layer downstream can tell from a good one.
/// Whether that matters belongs to the administrator, because the same absent input is routine for an Expression
/// built on IIF and a corruption incident for one building a Distinguished Name.
///
/// The real evaluator is used throughout rather than a mock: what is under test includes whether the Expression
/// was evaluated at all, and a mock returning a canned value cannot show that.
/// </remarks>
[TestFixture]
public class SyncEngineMissingInputBehaviourTests
{
    private const string EmailExpression = "Lower(cs[\"givenName\"]) + \".\" + Lower(cs[\"sn\"]) + \"@corp.local\"";

    private Application.Servers.SyncEngine _engine = null!;
    private DynamicExpressoEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
        _evaluator = new DynamicExpressoEvaluator();
    }

    /// <summary>
    /// A Connected System Object holding a given name and, when <paramref name="withSurname"/> is set, a surname,
    /// joined to a Metaverse Object that already holds an Email value, with one Expression mapping composing the
    /// address from both.
    /// </summary>
    private static (ConnectedSystemObject Cso, MetaverseObject Mvo, SyncRule SyncRule, ConnectedSystemObjectType CsoType)
        BuildScenario(MissingInputBehaviour behaviour, bool withSurname)
    {
        var mvEmail = new MetaverseAttribute { Id = 100, Name = "Email", Type = AttributeDataType.Text };
        var givenName = new ConnectedSystemObjectTypeAttribute { Id = 200, Name = "givenName", Type = AttributeDataType.Text };
        var surname = new ConnectedSystemObjectTypeAttribute { Id = 201, Name = "sn", Type = AttributeDataType.Text };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [givenName, surname] };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = mvEmail, AttributeId = 100, StringValue = "existing@corp.local" });

        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, StringValue = "Ada" });
        if (withSurname)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 201, StringValue = "Lovelace" });

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvEmail };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = EmailExpression, Order = 1, MissingInputBehaviour = behaviour });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        return (cso, mvo, syncRule, csoType);
    }

    private List<AttributeFlowError> Flow(ConnectedSystemObject cso, SyncRule syncRule, ConnectedSystemObjectType csoType) =>
        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType }, _evaluator);

    [Test]
    public void EvaluateAnyway_InputMissing_ContributesTheBrokenValue()
    {
        // The behaviour JIM has always had, and the reason the setting exists: this is a syntactically fine string
        // that is not an email address, and nothing downstream can tell it from a good one.
        var (cso, mvo, syncRule, csoType) = BuildScenario(MissingInputBehaviour.EvaluateAnyway, withSurname: false);

        var errors = Flow(cso, syncRule, csoType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(mvo.PendingAttributeValueAdditions.Single().StringValue, Is.EqualTo("ada.@corp.local"));
        }
    }

    [Test]
    public void AnyBehaviour_EveryInputPresent_FlowsTheValue()
    {
        // The setting must not change anything for an object that has the values, whatever it is set to.
        foreach (var behaviour in Enum.GetValues<MissingInputBehaviour>())
        {
            var (cso, mvo, syncRule, csoType) = BuildScenario(behaviour, withSurname: true);

            var errors = Flow(cso, syncRule, csoType);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(errors, Is.Empty, $"{behaviour} must not report an error when nothing is missing");
                Assert.That(mvo.PendingAttributeValueAdditions.Single().StringValue, Is.EqualTo("ada.lovelace@corp.local"),
                    $"{behaviour} must flow the value when nothing is missing");
            }
        }
    }

    [Test]
    public void ContributeNoValue_InputMissing_ContributesNothingAndReportsNothing()
    {
        var (cso, mvo, syncRule, csoType) = BuildScenario(MissingInputBehaviour.ContributeNoValue, withSurname: false);

        var errors = Flow(cso, syncRule, csoType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty, "declining to contribute is a legitimate outcome, not a fault");
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty, "the broken value must not reach the Metaverse Object");
        }
    }

    [Test]
    public void FailMapping_InputMissing_RecordsAnErrorNamingTheMissingInput()
    {
        var (cso, mvo, syncRule, csoType) = BuildScenario(MissingInputBehaviour.FailMapping, withSurname: false);

        var errors = Flow(cso, syncRule, csoType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0].Kind, Is.EqualTo(AttributeFlowErrorKind.ExpressionMissingInput));
            Assert.That(errors[0].TargetAttributeName, Is.EqualTo("Email"));
            Assert.That(errors[0].MissingInputs, Is.EquivalentTo(new[] { "cs[\"sn\"]" }),
                "the administrator needs to know which input was absent, not merely that one was");
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        }
    }

    [Test]
    public void FailObject_InputMissing_ThrowsSoTheWholeObjectIsLeftUntouched()
    {
        // The worker catches this at the object boundary and discards the object's pending changes, exactly as it
        // does for an Expression that threw.
        var (cso, _, syncRule, csoType) = BuildScenario(MissingInputBehaviour.FailObject, withSurname: false);

        var thrown = Assert.Throws<SyncExpressionMissingInputException>(() => Flow(cso, syncRule, csoType));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown!.MissingInputs, Is.EquivalentTo(new[] { "cs[\"sn\"]" }));
            Assert.That(thrown.TargetAttributeName, Is.EqualTo("Email"));
            Assert.That(thrown.Message, Does.Contain("cs[\"sn\"]"));
        }
    }

    [Test]
    public void FailObject_InputPresentButEmpty_IsTreatedAsMissing()
    {
        // An attribute present but blank is no value everywhere else in Attribute Flow, and it concatenates into
        // exactly the same broken output as one that is absent.
        var (cso, _, syncRule, csoType) = BuildScenario(MissingInputBehaviour.FailObject, withSurname: false);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 201, StringValue = string.Empty });

        Assert.That(() => Flow(cso, syncRule, csoType), Throws.TypeOf<SyncExpressionMissingInputException>());
    }

    [Test]
    public void FailObject_MetaverseAccessorInAnInboundExpression_IsNotTreatedAsAMissingInput()
    {
        // Inbound Attribute Flow evaluates against the Connected System Object alone, so an mv[...] accessor is a
        // mapping misconfigured in a different way entirely. Reading it as a missing input would fail every object
        // on the rule and point the administrator at the wrong problem.
        var (cso, _, syncRule, csoType) = BuildScenario(MissingInputBehaviour.FailObject, withSurname: true);
        // Null-aware, so the Expression itself evaluates cleanly: what is under test is whether the mv[...] input
        // is counted as missing, not what happens when an Expression is malformed.
        syncRule.AttributeFlowRules[0].Sources[0].Expression =
            "IIF(IsNullOrEmpty(mv[\"Nothing Here\"]), cs[\"givenName\"], \"other\")";

        Assert.That(() => Flow(cso, syncRule, csoType), Throws.Nothing);
    }
}
