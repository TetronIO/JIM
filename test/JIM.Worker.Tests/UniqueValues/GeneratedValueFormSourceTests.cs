// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Services;
using JIM.Application.UniqueValues;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Tests.Services;
using NUnit.Framework;
using static JIM.Worker.Tests.Services.DerivedFlowTestModel;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// Which values built from a generated value are checked before it is issued, where, and what each candidate's checks are
/// (Generated Value Forms, #2023, plan Phase 3): every form whose Keep unique switch is on, checked in JIM's records and in
/// each Connected System it is written to unchanged, probed where the target can be, and left alone in a system the
/// generated value excludes.
/// </summary>
[TestFixture]
public class GeneratedValueFormSourceTests
{
    private const int HrSystemId = 1;
    private const int AdSystemId = 2;
    private const int MailSystemId = 3;
    private const int CsAccountNameId = 300;
    private const int CsRegionId = 301;
    private const int CsMailId = 302;
    private const int CsUpnId = 303;

    private const string EmailFromAccountName = "mv[\"Account Name\"] + \"@corp.local\"";

    private DerivedFlowTestModel _model = null!;
    private List<ConnectedSystemObjectType> _objectTypes = null!;
    private ConnectedSystemObjectTypeAttribute _adMail = null!;
    private ConnectedSystemObjectTypeAttribute _adUpn = null!;
    private SyncRule _hr = null!;
    private SyncRuleMapping _accountName = null!;
    private SyncRule _adExport = null!;

    [SetUp]
    public void SetUp()
    {
        _model = new DerivedFlowTestModel();
        _adMail = new ConnectedSystemObjectTypeAttribute { Id = CsMailId, Name = "mail", Type = AttributeDataType.Text };
        _adUpn = new ConnectedSystemObjectTypeAttribute { Id = CsUpnId, Name = "userPrincipalName", Type = AttributeDataType.Text };
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
                    _adMail,
                    _adUpn
                ]
            }
        ];

        _hr = ImportRule(1, "HR Import", HrSystemId);
        _accountName = Generated(_hr, 100, _model.AccountName, "Lower(cs[\"sAMAccountName\"])");
        _adExport = ExportRule(4, "AD Export", AdSystemId);
    }

    // ---- Which values are checked, and where ----

    [Test]
    public void DescribeForms_EmailExportedToAdMail_ChecksItThereAndProbesIt()
    {
        Expression(_hr, 101, _model.Email, EmailFromAccountName);
        ExportDirect(_adExport, 400, _adMail, _model.Email);

        var form = DescribeForms().Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(form.BuiltValue.MetaverseAttributeName, Is.EqualTo("Email"));
            Assert.That(form.ConnectorSpaceAttributeIds, Is.EqualTo(new[] { CsMailId }));
            Assert.That(form.ProbeTargets, Is.EqualTo(new[] { new UniquenessProbeTarget(AdSystemId, CsMailId) }));
        }
    }

    [Test]
    public void DescribeForms_EmailExportedNowhere_IsStillCheckedInJimsRecords()
    {
        Expression(_hr, 101, _model.Email, EmailFromAccountName);

        var form = DescribeForms().Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(form.BuiltValue.MetaverseAttributeName, Is.EqualTo("Email"));
            Assert.That(form.ConnectorSpaceAttributeIds, Is.Empty);
            Assert.That(form.ProbeTargets, Is.Empty);
        }
    }

    [Test]
    public void DescribeForms_ExportExpression_IsCheckedInTheAttributeItWrites()
    {
        ExportExpression(_adExport, 400, _adUpn, EmailFromAccountName);

        var form = DescribeForms().Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(form.BuiltValue.MetaverseAttributeId, Is.Null);
            Assert.That(form.ConnectorSpaceAttributeIds, Is.EqualTo(new[] { CsUpnId }));
            Assert.That(form.ProbeTargets, Is.EqualTo(new[] { new UniquenessProbeTarget(AdSystemId, CsUpnId) }));
        }
    }

    [Test]
    public void DescribeForms_KeepUniqueOff_LeavesTheValueOut()
    {
        Expression(_hr, 101, _model.Email, EmailFromAccountName).KeepUnique = false;
        Expression(_hr, 102, _model.UserPrincipalName, "mv[\"Email\"]");

        Assert.That(DescribeForms().Select(f => f.BuiltValue.MetaverseAttributeName), Is.EqualTo(new[] { "User Principal Name" }),
            "the switch is per value: the User Principal Name built through Email is still checked");
    }

    [Test]
    public void DescribeForms_ValueBuiltOnAnotherRule_IsNotCheckedBeforeIssuing()
    {
        var mailImport = ImportRule(2, "Mail Import", MailSystemId);
        Expression(mailImport, 200, _model.Email, EmailFromAccountName);

        Assert.That(DescribeForms(mailImport), Is.Empty);
    }

    /// <summary>
    /// Exclusions on the generated value turn its checks off in a system for everything built from it (plan decision 2);
    /// JIM's own records are still checked.
    /// </summary>
    [Test]
    public void DescribeForms_SystemExcludedOnTheGeneratedValue_IsNotCheckedThere()
    {
        _accountName.Generation!.Exclusions.Add(new SyncRuleMappingGenerationExclusion { ConnectedSystemId = AdSystemId });
        Expression(_hr, 101, _model.Email, EmailFromAccountName);
        ExportDirect(_adExport, 400, _adMail, _model.Email);
        ExportExpression(_adExport, 401, _adUpn, EmailFromAccountName);

        var form = DescribeForms().Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(form.BuiltValue.MetaverseAttributeName, Is.EqualTo("Email"), "the export expression's only system is excluded");
            Assert.That(form.ConnectorSpaceAttributeIds, Is.Empty);
            Assert.That(form.ProbeTargets, Is.Empty);
        }
    }

    // ---- Retrying a value that waits ----

    /// <summary>
    /// A derived flow's inputs already mark its hosting system when another system changes them; an export expression
    /// built from a generated value is not a derived flow, so its inputs are mapped to the generated value's own system
    /// here, for a value waiting on one of them to be retried once it arrives.
    /// </summary>
    [Test]
    public void DescribeExportFormInputReaders_ExportExpressionReadingRegion_MarksTheGeneratedValuesOwnSystem()
    {
        _hr.MetaverseObjectTypeId = PersonTypeId;
        FromConnectedSystem(_hr, 103, _model.Region, CsRegionId);
        ExportExpression(_adExport, 400, _adUpn, "mv[\"Account Name\"] + \"@\" + mv[\"Region\"]");
        SyncRule[] rules = [_hr, _adExport];
        var builtValues = GeneratedValueBuiltValues.Create(rules, new DerivedFlowGraph(rules, _model.Types, DerivedFlowGraphScope.EnabledMappingsOnly), [_model.Person]);

        var readers = GeneratedValueParticipation.DescribeExportFormInputReaders(builtValues, rules);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(readers[(PersonTypeId, _model.Region.Id)], Is.EqualTo(new[] { HrSystemId }));
            Assert.That(readers[(PersonTypeId, _model.AccountName.Id)], Is.EqualTo(new[] { HrSystemId }));
        }
    }

    [Test]
    public void DescribeExportFormInputReaders_ExportExpressionsKeepUniqueOff_MarksNothing()
    {
        ExportExpression(_adExport, 400, _adUpn, "mv[\"Account Name\"] + \"@\" + mv[\"Region\"]").KeepUnique = false;
        SyncRule[] rules = [_hr, _adExport];
        var builtValues = GeneratedValueBuiltValues.Create(rules, new DerivedFlowGraph(rules, _model.Types, DerivedFlowGraphScope.EnabledMappingsOnly), [_model.Person]);

        Assert.That(GeneratedValueParticipation.DescribeExportFormInputReaders(builtValues, rules), Is.Empty);
    }

    // ---- A candidate's checks ----

    [Test]
    public void Evaluate_Candidate_GivesOneCheckPerFormWithItsValueAndWhereItIsChecked()
    {
        Expression(_hr, 101, _model.Email, EmailFromAccountName);
        ExportDirect(_adExport, 400, _adMail, _model.Email);
        ExportExpression(_adExport, 401, _adUpn, "mv[\"Email\"]");
        var source = CreateSource([.. DescribeForms().Select(f => f with { ProbeExemptValues = ["held@corp.local"] })]);

        var evaluation = source!.Evaluate("jbloggs1", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(evaluation.IsWaiting, Is.False);
            Assert.That(evaluation.Checks.Select(c => (c.Name, c.Value, c.Mode)), Is.EquivalentTo(new[]
            {
                ("Email", "jbloggs1@corp.local", GeneratedValueMode.Import),
                ("userPrincipalName", "jbloggs1@corp.local", GeneratedValueMode.Export)
            }));

            var email = evaluation.Checks.Single(c => c.Name == "Email");
            Assert.That(email.AttributeId, Is.EqualTo(_model.Email.Id));
            Assert.That(email.ConnectorSpaceAttributeIds, Is.EqualTo(new[] { CsMailId }));
            Assert.That(email.ProbeTargets, Is.EqualTo(new[] { new UniquenessProbeTarget(AdSystemId, CsMailId) }));
            Assert.That(email.ProbeExemptValues, Is.EqualTo(new[] { "held@corp.local" }));

            Assert.That(evaluation.Checks.Single(c => c.Name == "userPrincipalName").AttributeId, Is.EqualTo(CsUpnId));
        }
    }

    [Test]
    public void Evaluate_OtherInputMissing_Waits()
    {
        Expression(_hr, 102, _model.UserPrincipalName, "mv[\"Account Name\"] + \"@\" + mv[\"Region\"]");
        var source = CreateSource(DescribeForms());

        var evaluation = source!.Evaluate("jbloggs1", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(evaluation.IsWaiting, Is.True);
            Assert.That(evaluation.Checks, Is.Empty);
            Assert.That(evaluation.Waits, Is.EqualTo(new[] { new GeneratedValueFormWait("Region", "User Principal Name") }));
        }
    }

    [Test]
    public void Create_NoValueToCheck_GivesNoSource()
    {
        Assert.That(CreateSource([]), Is.Null);
    }

    // ---- fixture helpers ----

    private List<GeneratedValueForm> DescribeForms(params SyncRule[] otherRules)
    {
        SyncRule[] rules = [_hr, _adExport, .. otherRules];
        var graph = new DerivedFlowGraph(rules, _model.Types, DerivedFlowGraphScope.EnabledMappingsOnly);
        var builtValues = GeneratedValueBuiltValues.Create(rules, graph, [_model.Person]);
        return GeneratedValueParticipation.DescribeForms(_accountName, builtValues, [_adExport]);
    }

    private GeneratedValueFormSource? CreateSource(IReadOnlyList<GeneratedValueForm> forms)
    {
        var engine = new Application.Servers.SyncEngine();
        var evaluator = new DynamicExpressoEvaluator();
        SyncRule[] rules = [_hr, _adExport];
        var context = new AttributePriorityContext(rules, honourNullAssertions: true,
            derivedFlowGraph: new DerivedFlowGraph(rules, _model.Types, DerivedFlowGraphScope.EnabledMappingsOnly));

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _model.Person };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = HrSystemId, MetaverseObject = mvo };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = CsAccountNameId, StringValue = "JBloggs" });
        engine.FlowInboundAttributes(cso, _hr, _objectTypes, evaluator, priorityContext: context);

        return GeneratedValueFormSource.Create(engine, cso, mvo.PendingGeneratedValues.Single(), forms, [_hr], _objectTypes, evaluator, context);
    }

    private static SyncRuleMapping FromConnectedSystem(SyncRule rule, int id, MetaverseAttribute target, int csAttributeId)
    {
        var mapping = new SyncRuleMapping
        {
            Id = id,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id
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

    private static SyncRuleMapping ExportDirect(SyncRule rule, int id, ConnectedSystemObjectTypeAttribute target, MetaverseAttribute source)
    {
        var mapping = new SyncRuleMapping
        {
            Id = id,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, MetaverseAttribute = source, MetaverseAttributeId = source.Id });
        rule.AttributeFlowRules.Add(mapping);
        return mapping;
    }
}
