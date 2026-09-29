// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Worker.Tests.Services;
using NUnit.Framework;
using static JIM.Worker.Tests.Services.DerivedFlowTestModel;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// The derived pass of Metaverse-Derived Attribute Flows (#1750, plan Phase 2): an import expression reading
/// <c>mv["..."]</c> is excluded from the ordinary inbound pass and evaluated afterwards, level by level, against the
/// Metaverse Object's effective values as of this pass, through the ordinary Attribute Flow path (priority, "Null is
/// a value", Missing Input Behaviour, generation requests).
/// </summary>
/// <remarks>
/// The real expression evaluator is used throughout: what is under test includes what an expression actually read.
/// </remarks>
[TestFixture]
public class SyncEngineDerivedFlowTests
{
    private const int HrSystemId = 1;
    private const int AdSystemId = 2;
    private const int CsAccountNameId = 300;
    private const int CsRegionId = 301;
    private const int CsMailId = 302;

    private const string EmailFromAccountName = "mv[\"Account Name\"] + \"@corp.local\"";
    private const string EmailFromMaybeAbsentAccountName = "Lower(mv[\"Account Name\"]) + \"@corp.local\"";

    private DerivedFlowTestModel _model = null!;
    private Application.Servers.SyncEngine _engine = null!;
    private DynamicExpressoEvaluator _evaluator = null!;
    private List<ConnectedSystemObjectType> _objectTypes = null!;

    [SetUp]
    public void SetUp()
    {
        _model = new DerivedFlowTestModel();
        _engine = new Application.Servers.SyncEngine();
        _evaluator = new DynamicExpressoEvaluator();
        _objectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 1,
                Name = "user",
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute { Id = CsAccountNameId, Name = "sAMAccountName", Type = AttributeDataType.Text },
                    new ConnectedSystemObjectTypeAttribute { Id = CsRegionId, Name = "region", Type = AttributeDataType.Text },
                    new ConnectedSystemObjectTypeAttribute { Id = CsMailId, Name = "mail", Type = AttributeDataType.Text }
                ]
            }
        ];
    }

    // ---- fixture helpers ----

    private MetaverseObject NewPerson() => new() { Id = Guid.NewGuid(), Type = _model.Person };

    private static ConnectedSystemObject JoinedCso(MetaverseObject mvo, int connectedSystemId, string? accountName = null, string? region = null, string? mail = null)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = connectedSystemId, MetaverseObject = mvo };
        if (accountName != null)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = CsAccountNameId, StringValue = accountName });
        if (region != null)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = CsRegionId, StringValue = region });
        if (mail != null)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = CsMailId, StringValue = mail });
        return cso;
    }

    /// <summary>
    /// An ordinary import mapping flowing a Connected System attribute straight to <paramref name="target"/>.
    /// </summary>
    private static SyncRuleMapping FromConnectedSystem(SyncRule rule, int id, MetaverseAttribute target, int csAttributeId, int priority = int.MaxValue)
    {
        var mapping = new SyncRuleMapping
        {
            Id = id,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Priority = priority
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, ConnectedSystemAttributeId = csAttributeId });
        rule.AttributeFlowRules.Add(mapping);
        return mapping;
    }

    private static SyncRuleMapping WithMissingInputBehaviour(SyncRuleMapping mapping, MissingInputBehaviour behaviour)
    {
        mapping.Sources[0].MissingInputBehaviour = behaviour;
        return mapping;
    }

    private AttributePriorityContext ContextWithGraph(params SyncRule[] allRules) =>
        new(allRules, honourNullAssertions: true, derivedFlowGraph: new DerivedFlowGraph(allRules, _model.Types, DerivedFlowGraphScope.EnabledMappingsOnly));

    /// <summary>
    /// The engine's share of one object's synchronisation: the ordinary inbound pass over each in-scope rule, then
    /// the derived pass over the same rules.
    /// </summary>
    private List<AttributeFlowError> Synchronise(ConnectedSystemObject cso, AttributePriorityContext context, params SyncRule[] inScopeRules)
    {
        var errors = new List<AttributeFlowError>();
        foreach (var rule in inScopeRules)
            errors.AddRange(_engine.FlowInboundAttributes(cso, rule, _objectTypes, _evaluator, priorityContext: context));

        errors.AddRange(_engine.EvaluateDerivedLevels(cso, inScopeRules, _objectTypes, _evaluator, context));
        return errors;
    }

    private static List<MetaverseObjectAttributeValue> Added(MetaverseObject mvo, MetaverseAttribute attribute) =>
        mvo.PendingAttributeValueAdditions.Where(av => av.AttributeId == attribute.Id).ToList();

    private static string? AddedText(MetaverseObject mvo, MetaverseAttribute attribute) =>
        Added(mvo, attribute).Where(av => !av.NullValue).Select(av => av.StringValue).SingleOrDefault();

    private static MetaverseObjectAttributeValue Persist(MetaverseObject mvo, MetaverseAttribute attribute, string? value,
        int? syncRuleId = null, int? systemId = null, bool nullValue = false)
    {
        var persisted = new MetaverseObjectAttributeValue
        {
            MetaverseObject = mvo,
            Attribute = attribute,
            AttributeId = attribute.Id,
            StringValue = value,
            NullValue = nullValue,
            ContributedBySyncRuleId = syncRuleId,
            ContributedBySystemId = systemId
        };
        mvo.AttributeValues.Add(persisted);
        return persisted;
    }

    // ---- exclusion from the ordinary pass ----

    [Test]
    public void FlowInboundAttributes_GraphPresent_SkipsDerivedMappings()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        FromConnectedSystem(hr, 100, _model.AccountName, CsAccountNameId);
        Expression(hr, 101, _model.Email, EmailFromAccountName);
        var mvo = NewPerson();
        var cso = JoinedCso(mvo, HrSystemId, accountName: "jbloggs");

        var errors = _engine.FlowInboundAttributes(cso, hr, _objectTypes, _evaluator, priorityContext: ContextWithGraph(hr));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(AddedText(mvo, _model.AccountName), Is.EqualTo("jbloggs"), "the ordinary mapping still flows");
            Assert.That(Added(mvo, _model.Email), Is.Empty, "a derived mapping belongs to the derived pass, never the ordinary one");
        }
    }

    [Test]
    public void FlowInboundAttributes_GraphPresentReferenceOnlyPass_SkipsDerivedMappings()
    {
        // Expression mappings are evaluated in the reference-only pass too, so without the skip a derived flow would
        // run there with no Metaverse view at all and stage the value of an expression over absent inputs.
        var hr = ImportRule(1, "HR Import", HrSystemId);
        Expression(hr, 101, _model.Email, EmailFromMaybeAbsentAccountName);
        var mvo = NewPerson();
        Persist(mvo, _model.AccountName, "jbloggs", 1, HrSystemId);
        var cso = JoinedCso(mvo, HrSystemId);

        _engine.FlowInboundAttributes(cso, hr, _objectTypes, _evaluator, onlyReferenceAttributes: true, priorityContext: ContextWithGraph(hr));

        Assert.That(Added(mvo, _model.Email), Is.Empty);
    }

    // ---- Scenario 1: ordering ----

    [Test]
    public void DerivedPass_Scenario1_AccountNameEmailAndUpnAllReachTheMetaverseInOnePass()
    {
        // Configured out of dependency order on purpose: JIM orders derived flows itself.
        var hr = ImportRule(1, "HR Import", HrSystemId);
        Expression(hr, 102, _model.UserPrincipalName, "mv[\"Email\"]");
        Expression(hr, 101, _model.Email, EmailFromAccountName);
        FromConnectedSystem(hr, 100, _model.AccountName, CsAccountNameId);
        var mvo = NewPerson();
        var cso = JoinedCso(mvo, HrSystemId, accountName: "jbloggs");

        var errors = Synchronise(cso, ContextWithGraph(hr), hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(AddedText(mvo, _model.AccountName), Is.EqualTo("jbloggs"));
            Assert.That(AddedText(mvo, _model.Email), Is.EqualTo("jbloggs@corp.local"));
            Assert.That(AddedText(mvo, _model.UserPrincipalName), Is.EqualTo("jbloggs@corp.local"));
            Assert.That(mvo.PendingAttributeValueAdditions.Select(av => av.ContributedBySyncRuleId), Is.All.EqualTo(1),
                "a derived value is an ordinary contribution, stamped with its hosting Synchronisation Rule");
            Assert.That(mvo.PendingAttributeValueAdditions.Select(av => av.ContributedBySystemId), Is.All.EqualTo(HrSystemId));
        }
    }

    [Test]
    public void EvaluateDerivedLevel_OneLevelAtATime_EvaluatesOnlyThatLevel()
    {
        // The worker interleaves generation resolution between levels, so each level must be callable on its own.
        var hr = ImportRule(1, "HR Import", HrSystemId);
        FromConnectedSystem(hr, 100, _model.AccountName, CsAccountNameId);
        Expression(hr, 101, _model.Email, EmailFromAccountName);
        Expression(hr, 102, _model.UserPrincipalName, "mv[\"Email\"]");
        var mvo = NewPerson();
        var cso = JoinedCso(mvo, HrSystemId, accountName: "jbloggs");
        var context = ContextWithGraph(hr);
        _engine.FlowInboundAttributes(cso, hr, _objectTypes, _evaluator, priorityContext: context);

        _engine.EvaluateDerivedLevel(cso, 1, [hr], _objectTypes, _evaluator, context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AddedText(mvo, _model.Email), Is.EqualTo("jbloggs@corp.local"));
            Assert.That(Added(mvo, _model.UserPrincipalName), Is.Empty, "level 2 has not been evaluated yet");
        }

        _engine.EvaluateDerivedLevel(cso, 2, [hr], _objectTypes, _evaluator, context);

        Assert.That(AddedText(mvo, _model.UserPrincipalName), Is.EqualTo("jbloggs@corp.local"));
    }

    [Test]
    public void EvaluateDerivedLevel_DerivedFlowOnARuleNotInScope_IsNotEvaluated()
    {
        // A derived flow runs only in its hosting system's own synchronisation.
        var hr = ImportRule(1, "HR Import", HrSystemId);
        FromConnectedSystem(hr, 100, _model.AccountName, CsAccountNameId);
        var ad = ImportRule(2, "AD Import", AdSystemId);
        Expression(ad, 201, _model.Email, EmailFromAccountName);
        var mvo = NewPerson();
        var cso = JoinedCso(mvo, HrSystemId, accountName: "jbloggs");

        Synchronise(cso, ContextWithGraph(hr, ad), hr);

        Assert.That(Added(mvo, _model.Email), Is.Empty);
    }

    // ---- same-pass visibility ----

    [Test]
    public void DerivedPass_InputReplacedEarlierInThisPass_ReadsTheUncommittedValue()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        FromConnectedSystem(hr, 100, _model.AccountName, CsAccountNameId);
        Expression(hr, 101, _model.Email, EmailFromAccountName);
        var mvo = NewPerson();
        Persist(mvo, _model.AccountName, "jbloggs", 1, HrSystemId);
        var oldEmail = Persist(mvo, _model.Email, "jbloggs@corp.local", 1, HrSystemId);
        var cso = JoinedCso(mvo, HrSystemId, accountName: "jbloggs1");

        Synchronise(cso, ContextWithGraph(hr), hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AddedText(mvo, _model.Email), Is.EqualTo("jbloggs1@corp.local"),
                "the derived flow must read the value contributed in this pass, not the persisted one");
            Assert.That(mvo.PendingAttributeValueRemovals, Does.Contain(oldEmail));
        }
    }

    [Test]
    public void DerivedPass_InputRemovedEarlierInThisPass_ReadsItAsAbsent()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        FromConnectedSystem(hr, 100, _model.AccountName, CsAccountNameId);
        Expression(hr, 101, _model.Email, "IIF(IsNullOrEmpty(mv[\"Account Name\"]), \"no-account\", \"has-account\")");
        var mvo = NewPerson();
        Persist(mvo, _model.AccountName, "jbloggs", 1, HrSystemId);
        var cso = JoinedCso(mvo, HrSystemId);

        Synchronise(cso, ContextWithGraph(hr), hr);

        Assert.That(AddedText(mvo, _model.Email), Is.EqualTo("no-account"),
            "a value pending removal is gone as far as this pass is concerned");
    }

    // ---- Attribute Priority ----

    [Test]
    public void DerivedPass_DerivedFlowOutranksTheIncumbent_WinsAndTakesTheAttribute()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        FromConnectedSystem(hr, 100, _model.AccountName, CsAccountNameId);
        Expression(hr, 101, _model.Email, EmailFromAccountName, priority: 1);
        var ad = ImportRule(2, "AD Import", AdSystemId);
        FromConnectedSystem(ad, 201, _model.Email, CsMailId, priority: 2);
        var mvo = NewPerson();
        var adEmail = Persist(mvo, _model.Email, "joe@ad.local", 2, AdSystemId);
        var cso = JoinedCso(mvo, HrSystemId, accountName: "jbloggs");

        Synchronise(cso, ContextWithGraph(hr, ad), hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AddedText(mvo, _model.Email), Is.EqualTo("jbloggs@corp.local"));
            Assert.That(Added(mvo, _model.Email).Single().ContributedBySyncRuleId, Is.EqualTo(1));
            Assert.That(mvo.PendingAttributeValueRemovals, Does.Contain(adEmail));
        }
    }

    [Test]
    public void DerivedPass_DerivedFlowOutrankedByTheIncumbent_LosesAndWritesNothing()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        FromConnectedSystem(hr, 100, _model.AccountName, CsAccountNameId);
        Expression(hr, 101, _model.Email, EmailFromAccountName, priority: 2);
        var ad = ImportRule(2, "AD Import", AdSystemId);
        FromConnectedSystem(ad, 201, _model.Email, CsMailId, priority: 1);
        var mvo = NewPerson();
        var adEmail = Persist(mvo, _model.Email, "joe@ad.local", 2, AdSystemId);
        var cso = JoinedCso(mvo, HrSystemId, accountName: "jbloggs");

        Synchronise(cso, ContextWithGraph(hr, ad), hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Added(mvo, _model.Email), Is.Empty);
            Assert.That(mvo.PendingAttributeValueRemovals, Does.Not.Contain(adEmail));
            Assert.That(adEmail.ContributedBySyncRuleId, Is.EqualTo(2), "the incumbent keeps its provenance");
        }
    }

    [Test]
    public void DerivedPass_NullIsValueWithAnAbsentInput_AssertsNullOverALowerPriorityContributor()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        var email = WithMissingInputBehaviour(Expression(hr, 101, _model.Email, EmailFromAccountName, priority: 1), MissingInputBehaviour.ContributeNoValue);
        email.NullIsValue = true;
        var ad = ImportRule(2, "AD Import", AdSystemId);
        FromConnectedSystem(ad, 201, _model.Email, CsMailId, priority: 2);
        var mvo = NewPerson();
        var adEmail = Persist(mvo, _model.Email, "joe@ad.local", 2, AdSystemId);
        var cso = JoinedCso(mvo, HrSystemId);

        var errors = Synchronise(cso, ContextWithGraph(hr, ad), hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(mvo.PendingAttributeValueRemovals, Does.Contain(adEmail));
            var marker = Added(mvo, _model.Email).Single();
            Assert.That(marker.NullValue, Is.True);
            Assert.That(marker.ContributedBySyncRuleId, Is.EqualTo(1));
        }
    }

    // ---- Missing Input Behaviour on mv inputs (#1361) ----

    [Test]
    public void MissingInput_EvaluateAnyway_EvaluatesWithTheInputAbsent()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        WithMissingInputBehaviour(Expression(hr, 101, _model.Email, EmailFromMaybeAbsentAccountName), MissingInputBehaviour.EvaluateAnyway);
        var mvo = NewPerson();
        var cso = JoinedCso(mvo, HrSystemId);

        var errors = Synchronise(cso, ContextWithGraph(hr), hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(AddedText(mvo, _model.Email), Is.EqualTo("@corp.local"));
        }
    }

    [Test]
    public void MissingInput_ContributeNoValue_Scenario4_ContributesNothingThenDerivesOnceTheInputArrives()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        FromConnectedSystem(hr, 100, _model.AccountName, CsAccountNameId);
        WithMissingInputBehaviour(Expression(hr, 101, _model.Email, EmailFromMaybeAbsentAccountName, priority: 1), MissingInputBehaviour.ContributeNoValue);
        var ad = ImportRule(2, "AD Import", AdSystemId);
        FromConnectedSystem(ad, 201, _model.Email, CsMailId, priority: 2);
        var context = ContextWithGraph(hr, ad);
        var mvo = NewPerson();
        var adEmail = Persist(mvo, _model.Email, "joe@ad.local", 2, AdSystemId);
        var cso = JoinedCso(mvo, HrSystemId);

        var firstErrors = Synchronise(cso, context, hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstErrors, Is.Empty, "declining to contribute is not a fault");
            Assert.That(Added(mvo, _model.Email), Is.Empty);
            Assert.That(mvo.PendingAttributeValueRemovals, Does.Not.Contain(adEmail),
                "Email is resolved by priority: the lower-priority contributor's value stands");
        }

        _engine.ApplyPendingAttributeChanges(mvo);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = CsAccountNameId, StringValue = "jbloggs" });

        Synchronise(cso, context, hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AddedText(mvo, _model.Email), Is.EqualTo("jbloggs@corp.local"));
            Assert.That(mvo.PendingAttributeValueRemovals, Does.Contain(adEmail));
        }
    }

    [Test]
    public void MissingInput_FailMapping_RecordsAnErrorNamingTheMetaverseInput()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        WithMissingInputBehaviour(Expression(hr, 101, _model.Email, EmailFromMaybeAbsentAccountName), MissingInputBehaviour.FailMapping);
        var mvo = NewPerson();
        var existing = Persist(mvo, _model.Email, "old@corp.local", 1, HrSystemId);
        var cso = JoinedCso(mvo, HrSystemId);

        var errors = Synchronise(cso, ContextWithGraph(hr), hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0].Kind, Is.EqualTo(AttributeFlowErrorKind.ExpressionMissingInput));
            Assert.That(errors[0].TargetAttributeName, Is.EqualTo("Email"));
            Assert.That(errors[0].MissingInputs, Is.EquivalentTo(new[] { "mv[\"Account Name\"]" }));
            Assert.That(Added(mvo, _model.Email), Is.Empty);
            Assert.That(mvo.PendingAttributeValueRemovals, Does.Not.Contain(existing), "the attribute keeps what it held");
        }
    }

    [Test]
    public void MissingInput_FailObject_ThrowsNamingTheMetaverseInput()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        WithMissingInputBehaviour(Expression(hr, 101, _model.Email, EmailFromMaybeAbsentAccountName), MissingInputBehaviour.FailObject);
        var mvo = NewPerson();
        var cso = JoinedCso(mvo, HrSystemId);

        var thrown = Assert.Throws<SyncExpressionMissingInputException>(() => Synchronise(cso, ContextWithGraph(hr), hr));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown!.MissingInputs, Is.EquivalentTo(new[] { "mv[\"Account Name\"]" }));
            Assert.That(thrown!.TargetAttributeName, Is.EqualTo("Email"));
        }
    }

    [Test]
    public void MissingInput_BothSidesAbsent_ReportsEveryMissingInput()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        WithMissingInputBehaviour(Expression(hr, 101, _model.Email, "Lower(mv[\"Account Name\"]) + \"@\" + Lower(cs[\"region\"]) + \".corp\""),
            MissingInputBehaviour.FailMapping);
        var mvo = NewPerson();
        var cso = JoinedCso(mvo, HrSystemId);

        var errors = Synchronise(cso, ContextWithGraph(hr), hr);

        Assert.That(errors.Single().MissingInputs, Is.EquivalentTo(new[] { "mv[\"Account Name\"]", "cs[\"region\"]" }));
    }

    [Test]
    public void MissingInput_MetaverseInputPresentButConnectedSystemInputAbsent_ReportsOnlyTheConnectedSystemInput()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        WithMissingInputBehaviour(Expression(hr, 101, _model.Email, "Lower(mv[\"Account Name\"]) + \"@\" + Lower(cs[\"region\"]) + \".corp\""),
            MissingInputBehaviour.FailMapping);
        var mvo = NewPerson();
        Persist(mvo, _model.AccountName, "jbloggs", 1, HrSystemId);
        var cso = JoinedCso(mvo, HrSystemId);

        var errors = Synchronise(cso, ContextWithGraph(hr), hr);

        Assert.That(errors.Single().MissingInputs, Is.EquivalentTo(new[] { "cs[\"region\"]" }));
    }

    [Test]
    public void MissingInput_NullValueMarkerOnTheInput_ReadsAsAbsent()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        WithMissingInputBehaviour(Expression(hr, 101, _model.Email, EmailFromMaybeAbsentAccountName), MissingInputBehaviour.FailMapping);
        var mvo = NewPerson();
        Persist(mvo, _model.AccountName, null, 5, AdSystemId, nullValue: true);
        var cso = JoinedCso(mvo, HrSystemId);

        var errors = Synchronise(cso, ContextWithGraph(hr), hr);

        Assert.That(errors.Single().MissingInputs, Is.EquivalentTo(new[] { "mv[\"Account Name\"]" }),
            "an asserted null is no value, exactly as an export expression sees it");
    }

    // ---- generated mappings (#242) ----

    [Test]
    public void DerivedPass_GeneratedMappingReadingMv_RecordsAPendingGenerationAtItsLevel()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        FromConnectedSystem(hr, 100, _model.AccountName, CsAccountNameId);
        var email = Generated(hr, 101, _model.Email, EmailFromAccountName);
        var mvo = NewPerson();
        var cso = JoinedCso(mvo, HrSystemId, accountName: "jbloggs");
        var context = ContextWithGraph(hr);

        _engine.FlowInboundAttributes(cso, hr, _objectTypes, _evaluator, priorityContext: context);
        Assert.That(mvo.PendingGeneratedValues, Is.Empty, "a generated mapping reading mv is derived, so not part of the ordinary pass");

        _engine.EvaluateDerivedLevel(cso, 1, [hr], _objectTypes, _evaluator, context);

        using (Assert.EnterMultipleScope())
        {
            var request = mvo.PendingGeneratedValues.Single();
            Assert.That(request.Mapping, Is.SameAs(email));
            Assert.That(request.BaseValue, Is.EqualTo("jbloggs@corp.local"));
            Assert.That(request.BaseUnavailable, Is.False);
            Assert.That(request.ContributedBySyncRuleId, Is.EqualTo(1));
        }
    }

    // ---- determinism (FR 4) ----

    [Test]
    public void DerivedPass_PermutedConfigurationOrder_ProducesIdenticalResults()
    {
        var first = RunDeterminismScenario(reverse: false);
        var second = RunDeterminismScenario(reverse: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.EqualTo(first));
            Assert.That(first, Does.Contain((_model.Email.Id, "jbloggs@alt.local", 3)), "the priority 1 derived contributor wins Email");
            Assert.That(first, Does.Contain((_model.DisplayName.Id, "jbloggs@alt.local", 1)), "and the level 2 flow reads the winner");
        }
    }

    private List<(int AttributeId, string? Value, int? SyncRuleId)> RunDeterminismScenario(bool reverse)
    {
        _model = new DerivedFlowTestModel();
        var hr = ImportRule(1, "HR Import", HrSystemId);
        var hrAlternative = ImportRule(3, "HR Import (alternative)", HrSystemId);
        FromConnectedSystem(hr, 100, _model.AccountName, CsAccountNameId);
        Expression(hr, 101, _model.Email, EmailFromAccountName, priority: 2);
        Expression(hr, 102, _model.DisplayName, "mv[\"Email\"]");
        Expression(hrAlternative, 301, _model.Email, "mv[\"Account Name\"] + \"@alt.local\"", priority: 1);

        if (reverse)
        {
            hr.AttributeFlowRules.Reverse();
            hrAlternative.AttributeFlowRules.Reverse();
        }

        SyncRule[] rules = reverse ? [hrAlternative, hr] : [hr, hrAlternative];
        var mvo = NewPerson();
        var cso = JoinedCso(mvo, HrSystemId, accountName: "jbloggs");

        Synchronise(cso, ContextWithGraph(rules), rules);

        return mvo.PendingAttributeValueAdditions
            .Select(av => (av.AttributeId, av.StringValue, av.ContributedBySyncRuleId))
            .OrderBy(t => t.AttributeId).ThenBy(t => t.StringValue, StringComparer.Ordinal)
            .ToList();
    }

    // ---- flag off: graph null reproduces today's behaviour ----

    [Test]
    public void GraphNull_LegacyImportExpressionReadingMv_FlowsInTheOrdinaryPassAndReadsNothing()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        Expression(hr, 101, _model.Email, EmailFromMaybeAbsentAccountName);
        var mvo = NewPerson();
        Persist(mvo, _model.AccountName, "jbloggs", 1, HrSystemId);
        var cso = JoinedCso(mvo, HrSystemId);
        var legacyContext = new AttributePriorityContext([hr], honourNullAssertions: true);

        var errors = Synchronise(cso, legacyContext, hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(AddedText(mvo, _model.Email), Is.EqualTo("@corp.local"),
                "flag off, mv in an import expression reads nothing, exactly as before the feature");
            Assert.That(Added(mvo, _model.Email), Has.Count.EqualTo(1), "and the derived pass is a no-op, so it flows exactly once");
        }
    }

    [Test]
    public void EvaluateDerivedLevel_NoGraphOnTheContext_Throws()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        var mvo = NewPerson();
        var cso = JoinedCso(mvo, HrSystemId);

        Assert.That(() => _engine.EvaluateDerivedLevel(cso, 1, [hr], _objectTypes, _evaluator, new AttributePriorityContext([hr])),
            Throws.ArgumentException);
    }
}
