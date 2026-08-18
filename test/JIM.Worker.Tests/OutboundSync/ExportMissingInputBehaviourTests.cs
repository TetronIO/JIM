// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Missing Input Behaviour on outbound (export) Expression-based Attribute Flow (#1361).
/// </summary>
/// <remarks>
/// Export is where a broken Expression output does the most damage: the value is handed to a Connected System
/// that will either reject it or, worse, accept it. A Distinguished Name or a User Principal Name composed from
/// an absent input is still a syntactically valid string, so nothing between the Expression and the directory can
/// tell it apart from a correct one. The behaviour is the administrator's choice per mapping source, and this fixture pins all four
/// on the export path, using the real evaluator so that "was the Expression evaluated at all" is observable.
/// </remarks>
[TestFixture]
public class ExportMissingInputBehaviourTests
{
    /// <summary>
    /// A User Principal Name built from the Metaverse Object's Display Name. With no Display Name this still
    /// evaluates, to "@corp.local", which is exactly the shape of value this feature exists to stop reaching a
    /// Connected System: syntactically a string, semantically nothing, and indistinguishable from a good value
    /// to every layer downstream.
    /// </summary>
    private const string UserPrincipalNameExpression = "Lower(mv[\"Display Name\"]) + \"@corp.local\"";

    private JimApplication Jim { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private List<MetaverseObjectType> MetaverseObjectTypesData { get; set; } = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();

        // CreateAttributeValueChanges never queries the database, so a bare mocked context suffices.
        var mockJimDbContext = new Mock<JimDbContext>();
        Jim = new JimApplication(new PostgresDataRepository(mockJimDbContext.Object), syncRepository: new SyncRepository());

        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MetaverseObjectTypesData = TestUtilities.GetMetaverseObjectTypeData();
    }

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
    }

    [Test]
    public void EvaluateAnyway_InputMissing_StagesTheBrokenValue()
    {
        // Today's behaviour, and the reason the setting exists.
        var (mvo, exportRule) = BuildScenario(MissingInputBehaviour.EvaluateAnyway, withDisplayName: false);
        var flowErrors = new List<AttributeFlowError>();

        var changes = Evaluate(mvo, exportRule, flowErrors);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Has.Count.EqualTo(1), "EvaluateAnyway must evaluate the Expression regardless of its inputs");
            Assert.That(changes[0].StringValue, Is.EqualTo("@corp.local"),
                "The Expression composes a structurally broken User Principal Name that no later layer can detect");
            Assert.That(flowErrors, Is.Empty, "EvaluateAnyway reports nothing; the administrator has accepted the risk");
        }
    }

    [Test]
    public void AllBehaviours_InputsPresent_StageTheValueAndReportNothing()
    {
        // The setting must be inert whenever the Expression has everything it reads.
        foreach (var behaviour in Enum.GetValues<MissingInputBehaviour>())
        {
            var (mvo, exportRule) = BuildScenario(behaviour, withDisplayName: true);
            var flowErrors = new List<AttributeFlowError>();

            var changes = Evaluate(mvo, exportRule, flowErrors);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(changes, Has.Count.EqualTo(1), $"{behaviour} must stage the value when every input is present");
                Assert.That(changes[0].StringValue, Is.EqualTo("ada.lovelace@corp.local"));
                Assert.That(flowErrors, Is.Empty, $"{behaviour} must report nothing when every input is present");
            }
        }
    }

    [Test]
    public void ContributeNoValue_InputMissing_StagesNothingAndReportsNothing()
    {
        // For an Expression already guarded with IIF, or an attribute the Connected System does not require,
        // an absent input is routine: contribute nothing and let the next contributor, or the existing value, stand.
        var (mvo, exportRule) = BuildScenario(MissingInputBehaviour.ContributeNoValue, withDisplayName: false);
        var flowErrors = new List<AttributeFlowError>();

        var changes = Evaluate(mvo, exportRule, flowErrors);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Is.Empty, "ContributeNoValue must stage no change for the target attribute");
            Assert.That(flowErrors, Is.Empty, "ContributeNoValue is a deliberate no-op, not an error");
        }
    }

    [Test]
    public void FailMapping_InputMissing_StagesNothingAndReportsTheMissingInput()
    {
        // Everything else on the Synchronisation Rule still exports; only this attribute is withheld and reported.
        var (mvo, exportRule) = BuildScenario(MissingInputBehaviour.FailMapping, withDisplayName: false);
        var flowErrors = new List<AttributeFlowError>();

        var changes = Evaluate(mvo, exportRule, flowErrors);

        Assert.That(changes, Is.Empty, "FailMapping must not stage a value built from an absent input");
        Assert.That(flowErrors, Has.Count.EqualTo(1), "FailMapping must report the mapping as errored");

        var error = flowErrors[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.Kind, Is.EqualTo(AttributeFlowErrorKind.ExpressionMissingInput));
            Assert.That(error.TargetAttributeName, Is.EqualTo(MockTargetSystemAttributeNames.UserPrincipalName.ToString()));
            Assert.That(error.Expression, Is.EqualTo(UserPrincipalNameExpression));
            Assert.That(error.MissingInputs, Is.EqualTo(new[] { "mv[\"Display Name\"]" }),
                "The report must name the input as the Expression addresses it, so the administrator can find it");
        }
    }

    [Test]
    public void FailObject_InputMissing_ThrowsSoTheWholeObjectIsErrored()
    {
        // For a critical attribute (a User Principal Name, on which signing in depends) a half-built object is worse
        // than no object: the whole export evaluation fails and the Metaverse Object is recorded as errored.
        var (mvo, exportRule) = BuildScenario(MissingInputBehaviour.FailObject, withDisplayName: false);

        var exception = Assert.Throws<SyncExpressionMissingInputException>(() => Evaluate(mvo, exportRule, []));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.TargetAttributeName, Is.EqualTo(MockTargetSystemAttributeNames.UserPrincipalName.ToString()));
            Assert.That(exception.MissingInputs, Is.EqualTo(new[] { "mv[\"Display Name\"]" }));
            Assert.That(exception.Expression, Is.EqualTo(UserPrincipalNameExpression));
            Assert.That(exception.Message, Does.Not.Contain("Lower("),
                "The message must not carry the raw Expression; it reaches logs, and the Expression is administrator input");
        }
    }

    [Test]
    public void FailMapping_InputPresentButEmpty_TreatsEmptyStringAsNoValue()
    {
        // An empty string concatenates into exactly the same broken value an absent attribute produces, so the
        // two must be treated identically. This matches the inbound side.
        var (mvo, exportRule) = BuildScenario(MissingInputBehaviour.FailMapping, withDisplayName: true, displayName: string.Empty);
        var flowErrors = new List<AttributeFlowError>();

        var changes = Evaluate(mvo, exportRule, flowErrors);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Is.Empty);
            Assert.That(flowErrors, Has.Count.EqualTo(1), "An empty string is no value, not a value");
        }
    }

    [Test]
    public void FailMapping_SiblingMappingUnaffected_KeepsExportingWhatItCan()
    {
        // The whole point of FailMapping as a sibling to FailObject: the object still exports, minus the one
        // attribute whose Expression could not be evaluated safely.
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var emailMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.Email);
        var targetMailAttr = GetTargetAttribute(MockTargetSystemAttributeNames.Mail);

        var (mvo, exportRule) = BuildScenario(MissingInputBehaviour.FailMapping, withDisplayName: false);
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 101,
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetMailAttr,
            TargetConnectedSystemAttributeId = targetMailAttr.Id,
            Sources = { new SyncRuleMappingSource { Id = 201, Order = 0, MetaverseAttribute = emailMvAttr, MetaverseAttributeId = emailMvAttr.Id } }
        });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = emailMvAttr.Id,
            Attribute = emailMvAttr,
            MetaverseObject = mvo,
            StringValue = "ada@corp.local"
        });

        var flowErrors = new List<AttributeFlowError>();
        var changes = Evaluate(mvo, exportRule, flowErrors);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Has.Count.EqualTo(1), "The sibling mapping must still export");
            Assert.That(changes[0].AttributeId, Is.EqualTo(targetMailAttr.Id));
            Assert.That(flowErrors, Has.Count.EqualTo(1), "Only the Expression mapping is reported as errored");
        }
    }

    #region helpers
    private List<PendingExportAttributeValueChange> Evaluate(MetaverseObject mvo, SyncRule exportRule, List<AttributeFlowError> flowErrors) =>
        Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _, flowErrors: flowErrors);

    private ConnectedSystemObjectTypeAttribute GetTargetAttribute(MockTargetSystemAttributeNames name)
    {
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        return targetUserType.Attributes.Single(a => a.Name == name.ToString());
    }

    /// <summary>
    /// A Metaverse Object with an Expression mapping composing a User Principal Name from its Display Name,
    /// where <paramref name="withDisplayName"/> decides whether that input has a value at all.
    /// </summary>
    private (MetaverseObject Mvo, SyncRule ExportRule) BuildScenario(
        MissingInputBehaviour behaviour, bool withDisplayName, string displayName = "Ada.Lovelace")
    {
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var displayNameMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.DisplayName);
        var targetUpnAttr = GetTargetAttribute(MockTargetSystemAttributeNames.UserPrincipalName);

        var exportRule = new SyncRule { Id = 1, Name = "Missing Input Behaviour Export Rule", Direction = SyncRuleDirection.Export };
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 100,
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetUpnAttr,
            TargetConnectedSystemAttributeId = targetUpnAttr.Id,
            Sources =
            {
                new SyncRuleMappingSource
                {
                    Id = 200,
                    Order = 0,
                    Expression = UserPrincipalNameExpression,
                    MissingInputBehaviour = behaviour
                }
            }
        });

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvUserType };
        if (withDisplayName)
        {
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = displayNameMvAttr.Id,
                Attribute = displayNameMvAttr,
                MetaverseObject = mvo,
                StringValue = displayName
            });
        }

        return (mvo, exportRule);
    }
    #endregion
}
