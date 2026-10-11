// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Services;
using JIM.Application.UniqueValues;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Worker.Tests.Services;
using NUnit.Framework;
using static JIM.Worker.Tests.Services.DerivedFlowTestModel;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// Working out a generated value candidate's forms (Generated Value Forms, #2023, plan Phase 3): the synchronisation's
/// derived pass replayed on a scratch copy of the Metaverse Object, with the candidate applied as the generated value.
/// Each test that works a value out also issues the candidate for real and runs the derived pass, and asserts the two
/// agree: what JIM checks before issuing is what the pass then writes.
/// </summary>
[TestFixture]
public class GeneratedValueFormReplayTests
{
    private const int HrSystemId = 1;
    private const int AdSystemId = 2;
    private const int CsAccountNameId = 300;
    private const int CsRegionId = 301;
    private const int CsMailId = 302;

    private const string AccountNameFromHr = "Lower(cs[\"sAMAccountName\"])";
    private const string EmailFromAccountName = "mv[\"Account Name\"] + \"@corp.local\"";

    private DerivedFlowTestModel _model = null!;
    private Application.Servers.SyncEngine _engine = null!;
    private DynamicExpressoEvaluator _evaluator = null!;
    private List<ConnectedSystemObjectType> _objectTypes = null!;
    private ConnectedSystemObjectTypeAttribute _adMail = null!;

    [SetUp]
    public void SetUp()
    {
        _model = new DerivedFlowTestModel();
        _engine = new Application.Servers.SyncEngine();
        _evaluator = new DynamicExpressoEvaluator();
        _adMail = new ConnectedSystemObjectTypeAttribute { Id = CsMailId, Name = "mail", Type = AttributeDataType.Text };
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
                    _adMail
                ]
            }
        ];
    }

    // ---- What a form's value is ----

    [Test]
    public void Replay_EmailAndUpnBuiltOnTheGeneratedFlowsRule_WorksOutEachAsTheDerivedPassThenWritesIt()
    {
        var hr = HrImport();
        Expression(hr, 101, _model.Email, EmailFromAccountName);
        Expression(hr, 102, _model.UserPrincipalName, "mv[\"Email\"]");
        var context = ContextWithGraph(hr);
        var (mvo, cso, pending) = ReachGeneration(context, hr);

        var result = Replay(cso, pending, context, [hr], [hr]).Replay("jbloggs1", null);
        Issue(mvo, cso, pending, "jbloggs1", context, hr);

        var worked = Worked(result);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsWaiting, Is.False);
            Assert.That(worked["Email"], Is.EqualTo("jbloggs1@corp.local"));
            Assert.That(worked["User Principal Name"], Is.EqualTo("jbloggs1@corp.local"));
            Assert.That(worked["Email"], Is.EqualTo(AddedText(mvo, _model.Email)), "what was checked is what the derived pass wrote");
            Assert.That(worked["User Principal Name"], Is.EqualTo(AddedText(mvo, _model.UserPrincipalName)));
        }
    }

    [Test]
    public void Replay_FormCaseNormalised_WorksItOutAsTheDerivedPassWritesIt()
    {
        var hr = HrImport();
        Expression(hr, 101, _model.Email, EmailFromAccountName).CaseNormalisation = InboundCaseNormalisation.Lower;
        var context = ContextWithGraph(hr);
        var (mvo, cso, pending) = ReachGeneration(context, hr);

        var result = Replay(cso, pending, context, [hr], [hr]).Replay("JBloggs1", null);
        Issue(mvo, cso, pending, "JBloggs1", context, hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Worked(result)["Email"], Is.EqualTo("jbloggs1@corp.local"));
            Assert.That(Worked(result)["Email"], Is.EqualTo(AddedText(mvo, _model.Email)));
        }
    }

    /// <summary>
    /// A form can read a value that is not built from the generated value but is itself derived, later in the same
    /// pass than the generated value is resolved: the replay works that out too, as the pass will.
    /// </summary>
    [Test]
    public void Replay_FormReadsAnotherDerivedValue_WorksThatOutTooAsThePassWill()
    {
        var hr = HrImport();
        FromConnectedSystem(hr, 103, _model.Region, CsRegionId);
        Expression(hr, 104, _model.MailNickname, "Upper(mv[\"Region\"])");
        Expression(hr, 102, _model.UserPrincipalName, "mv[\"Account Name\"] + \"@\" + mv[\"Mail Nickname\"] + \".corp.local\"");
        var context = ContextWithGraph(hr);
        var (mvo, cso, pending) = ReachGeneration(context, hr, region: "emea");

        var result = Replay(cso, pending, context, [hr], [hr]).Replay("jbloggs1", null);
        Issue(mvo, cso, pending, "jbloggs1", context, hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Worked(result)["User Principal Name"], Is.EqualTo("jbloggs1@EMEA.corp.local"));
            Assert.That(Worked(result)["User Principal Name"], Is.EqualTo(AddedText(mvo, _model.UserPrincipalName)));
            Assert.That(Worked(result).ContainsKey("Mail Nickname"), Is.False, "Mail Nickname is not built from Account Name");
        }
    }

    [Test]
    public void Replay_ExportExpressionBuiltFromTheGeneratedValue_WorksOutWhatExportWouldSend()
    {
        var hr = HrImport();
        var adExport = ExportRule(3, "AD Export", AdSystemId);
        ExportExpression(adExport, 300, _adMail, EmailFromAccountName);
        var context = ContextWithGraph(hr, adExport);
        var (_, cso, pending) = ReachGeneration(context, hr);

        var result = Replay(cso, pending, context, [hr, adExport], [hr]).Replay("jbloggs1", null);

        Assert.That(Worked(result)["mail"], Is.EqualTo("jbloggs1@corp.local"));
    }

    [Test]
    public void Replay_ExportExpressionBuiltThroughEmail_WorksOutWhatExportWouldSend()
    {
        var hr = HrImport();
        Expression(hr, 101, _model.Email, EmailFromAccountName);
        var adExport = ExportRule(3, "AD Export", AdSystemId);
        ExportExpression(adExport, 300, _adMail, "Upper(mv[\"Email\"])");
        var context = ContextWithGraph(hr, adExport);
        var (_, cso, pending) = ReachGeneration(context, hr);

        var result = Replay(cso, pending, context, [hr, adExport], [hr]).Replay("jbloggs1", null);

        Assert.That(Worked(result)["mail"], Is.EqualTo("JBLOGGS1@CORP.LOCAL"));
    }

    [Test]
    public void Replay_LeavesTheRealObjectAsItWas()
    {
        var hr = HrImport();
        Expression(hr, 101, _model.Email, EmailFromAccountName);
        var context = ContextWithGraph(hr);
        var mvo = NewPerson();

        // An Email the object already holds, recorded against another rule: replaying a candidate that builds the same
        // address stamps the replay's rule on it, which must happen to the scratch copy only.
        var held = Persist(mvo, _model.Email, "jbloggs1@corp.local", syncRuleId: 9, systemId: 9);
        var (_, cso, pending) = ReachGeneration(context, hr, mvo);
        var additions = mvo.PendingAttributeValueAdditions.ToList();
        var removals = mvo.PendingAttributeValueRemovals.ToList();
        var values = mvo.AttributeValues.ToList();

        Replay(cso, pending, context, [hr], [hr]).Replay("jbloggs1", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueAdditions, Is.EqualTo(additions));
            Assert.That(mvo.PendingAttributeValueRemovals, Is.EqualTo(removals));
            Assert.That(mvo.AttributeValues, Is.EqualTo(values));
            Assert.That(mvo.PendingGeneratedValues, Is.EqualTo(new[] { pending }));
            Assert.That(held.ContributedBySyncRuleId, Is.EqualTo(9), "the real value's provenance is untouched");
            Assert.That(cso.MetaverseObject, Is.SameAs(mvo));
        }
    }

    // ---- When a form applies ----

    /// <summary>
    /// A form applies unless a contributor with higher Attribute Priority holds its attribute (PRD requirement 2).
    /// Email here comes from the mail system for this person, so it does not move with the candidate; nor does the User
    /// Principal Name built from it.
    /// </summary>
    [Test]
    public void Replay_HigherPriorityContributorHoldsEmail_NeitherEmailNorTheUpnBuiltFromItIsChecked()
    {
        var hr = HrImport();
        Expression(hr, 101, _model.Email, EmailFromAccountName, priority: 2);
        Expression(hr, 102, _model.UserPrincipalName, "mv[\"Email\"]");
        var ad = ImportRule(2, "AD Import", AdSystemId);
        FromConnectedSystem(ad, 200, _model.Email, CsMailId, priority: 1);
        var context = ContextWithGraph(hr, ad);
        var mvo = NewPerson();
        Persist(mvo, _model.Email, "joe@mail.local", syncRuleId: 2, systemId: AdSystemId);
        var (_, cso, pending) = ReachGeneration(context, hr, mvo);

        var result = Replay(cso, pending, context, [hr, ad], [hr]).Replay("jbloggs1", null);
        Issue(mvo, cso, pending, "jbloggs1", context, hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Values, Is.Empty);
            Assert.That(result.IsWaiting, Is.False);
            Assert.That(AddedText(mvo, _model.Email), Is.Null, "the derived pass keeps the mail system's Email too");
            Assert.That(AddedText(mvo, _model.UserPrincipalName), Is.EqualTo("joe@mail.local"), "and its User Principal Name is not built from the candidate");
        }
    }

    // ---- Waiting, and failing closed ----

    [Test]
    public void Replay_OtherInputHasNoValue_WaitsNamingTheInputAndTheValueThatNeedsIt()
    {
        var hr = HrImport();
        FromConnectedSystem(hr, 103, _model.Region, CsRegionId);
        Expression(hr, 102, _model.UserPrincipalName, "mv[\"Account Name\"] + \"@\" + mv[\"Region\"]");
        var context = ContextWithGraph(hr);
        var (_, cso, pending) = ReachGeneration(context, hr);

        var result = Replay(cso, pending, context, [hr], [hr]).Replay("jbloggs1", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsWaiting, Is.True);
            Assert.That(result.Waits, Is.EqualTo(new[] { new GeneratedValueFormWait("Region", "User Principal Name") }));
        }
    }

    /// <summary>
    /// The wait does not depend on the form's Missing Input Behaviour: a value built from a missing input is held back
    /// whatever the flow would otherwise do with it (PRD requirement 3).
    /// </summary>
    [TestCase(MissingInputBehaviour.EvaluateAnyway)]
    [TestCase(MissingInputBehaviour.ContributeNoValue)]
    [TestCase(MissingInputBehaviour.FailMapping)]
    [TestCase(MissingInputBehaviour.FailObject)]
    public void Replay_OtherInputHasNoValue_WaitsWhateverTheFormsMissingInputBehaviour(MissingInputBehaviour behaviour)
    {
        var hr = HrImport();
        FromConnectedSystem(hr, 103, _model.Region, CsRegionId);
        Expression(hr, 102, _model.UserPrincipalName, "mv[\"Account Name\"] + \"@\" + mv[\"Region\"]").Sources[0].MissingInputBehaviour = behaviour;
        var context = ContextWithGraph(hr);
        var (_, cso, pending) = ReachGeneration(context, hr);

        Assert.That(Replay(cso, pending, context, [hr], [hr]).Replay("jbloggs1", null).IsWaiting, Is.True);
    }

    [Test]
    public void Replay_ExportExpressionsOtherInputHasNoValue_Waits()
    {
        var hr = HrImport();
        FromConnectedSystem(hr, 103, _model.Region, CsRegionId);
        var adExport = ExportRule(3, "AD Export", AdSystemId);
        ExportExpression(adExport, 300, _adMail, "mv[\"Account Name\"] + \"@\" + mv[\"Region\"]");
        var context = ContextWithGraph(hr, adExport);
        var (_, cso, pending) = ReachGeneration(context, hr);

        var result = Replay(cso, pending, context, [hr, adExport], [hr]).Replay("jbloggs1", null);

        Assert.That(result.Waits, Is.EqualTo(new[] { new GeneratedValueFormWait("Region", "mail") }));
    }

    [Test]
    public void Replay_FormExpressionFails_FailsClosedNamingTheRuleItIsOn()
    {
        var hr = HrImport();
        Expression(hr, 101, _model.Email, "mv[\"Account Name\"].NoSuchMethod()");
        var context = ContextWithGraph(hr);
        var (_, cso, pending) = ReachGeneration(context, hr);
        var replay = Replay(cso, pending, context, [hr], [hr]);

        var ex = Assert.Throws<SyncExpressionEvaluationException>(() => replay.Replay("jbloggs1", null));

        Assert.That(ex!.SyncRuleName, Is.EqualTo("HR Import"));
    }

    // ---- fixture helpers ----

    private SyncRule HrImport()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        Generated(hr, 100, _model.AccountName, AccountNameFromHr);
        return hr;
    }

    private MetaverseObject NewPerson() => new() { Id = Guid.NewGuid(), Type = _model.Person };

    private AttributePriorityContext ContextWithGraph(params SyncRule[] allRules) =>
        new(allRules, honourNullAssertions: true, derivedFlowGraph: new DerivedFlowGraph(allRules, _model.Types, DerivedFlowGraphScope.EnabledMappingsOnly));

    /// <summary>
    /// The ordinary inbound pass for a new HR person, which leaves the generated Account Name pending: the point at
    /// which the synchronisation resolves it, and so works out its candidates' forms.
    /// </summary>
    private (MetaverseObject Mvo, ConnectedSystemObject Cso, PendingGeneratedValue Pending) ReachGeneration(
        AttributePriorityContext context, SyncRule hr, MetaverseObject? mvo = null, string? region = null)
    {
        mvo ??= NewPerson();
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = HrSystemId, MetaverseObject = mvo };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = CsAccountNameId, StringValue = "JBloggs" });
        if (region != null)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = CsRegionId, StringValue = region });

        _engine.FlowInboundAttributes(cso, hr, _objectTypes, _evaluator, priorityContext: context);
        return (mvo, cso, mvo.PendingGeneratedValues.Single());
    }

    /// <summary>
    /// What the synchronisation does once a candidate is issued: applies it, settles the request, and runs the derived
    /// pass over the rules in scope.
    /// </summary>
    private void Issue(MetaverseObject mvo, ConnectedSystemObject cso, PendingGeneratedValue pending, string value, AttributePriorityContext context, params SyncRule[] inScopeRules)
    {
        _engine.ApplyGeneratedValue(mvo, pending, value, null);
        mvo.PendingGeneratedValues.Clear();
        _engine.EvaluateDerivedLevels(cso, inScopeRules, _objectTypes, _evaluator, context);
    }

    private GeneratedValueFormReplay Replay(ConnectedSystemObject cso, PendingGeneratedValue pending, AttributePriorityContext context, SyncRule[] allRules, SyncRule[] inScopeRules)
    {
        var forms = GeneratedValueBuiltValues.Create(allRules, context.DerivedFlowGraph, [_model.Person])
            .BuiltFrom(pending.Mapping)
            .Where(b => b.Kind == GeneratedValueBuiltValueKind.Form)
            .ToList();
        return new GeneratedValueFormReplay(_engine, cso, pending, forms, inScopeRules, _objectTypes, _evaluator, context);
    }

    private static Dictionary<string, string> Worked(GeneratedValueFormReplayResult result) =>
        result.Values.ToDictionary(v => v.Form.MetaverseAttributeName ?? v.Form.Mapping.TargetConnectedSystemAttribute!.Name, v => v.Value);

    private static string? AddedText(MetaverseObject mvo, MetaverseAttribute attribute) =>
        mvo.PendingAttributeValueAdditions.Where(av => av.AttributeId == attribute.Id && !av.NullValue).Select(av => av.StringValue).SingleOrDefault();

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

    private static SyncRuleMapping ExportExpression(SyncRule rule, int id, ConnectedSystemObjectTypeAttribute target, string expression)
    {
        var mapping = new SyncRuleMapping
        {
            Id = id,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = expression });
        rule.AttributeFlowRules.Add(mapping);
        return mapping;
    }

    private static MetaverseObjectAttributeValue Persist(MetaverseObject mvo, MetaverseAttribute attribute, string value, int syncRuleId, int systemId)
    {
        var persisted = new MetaverseObjectAttributeValue
        {
            MetaverseObject = mvo,
            Attribute = attribute,
            AttributeId = attribute.Id,
            StringValue = value,
            ContributedBySyncRuleId = syncRuleId,
            ContributedBySystemId = systemId
        };
        mvo.AttributeValues.Add(persisted);
        return persisted;
    }
}
