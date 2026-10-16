// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
/// The values built from a generated value (Generated Value Forms, #2023): every derived Metaverse value and export
/// expression whose inputs lead back to one generated value. A value is a form when every derived step on the way is on
/// the generated flow's own Synchronisation Rule; built on another rule, it is corrected after a refusal only; built from
/// several generated values, it belongs to none of them.
/// </summary>
[TestFixture]
public class GeneratedValueBuiltValuesTests
{
    private const int HrSystemId = 1;
    private const int DirectorySystemId = 2;
    private const int MailSystemId = 3;

    private DerivedFlowTestModel _model = null!;
    private SyncRule _hrImport = null!;
    private SyncRule _directoryExport = null!;
    private SyncRuleMapping _accountName = null!;

    [SetUp]
    public void SetUp()
    {
        _model = new DerivedFlowTestModel();
        _hrImport = ImportRule(1, "HR Import", HrSystemId);
        _hrImport.MetaverseObjectType = _model.Person;
        _accountName = Generated(_hrImport, 100, _model.AccountName, "Lower(cs[\"first\"]) + \".\" + Lower(cs[\"last\"])");

        _directoryExport = ExportRule(2, "Directory Export", DirectorySystemId);
        _directoryExport.MetaverseObjectType = _model.Person;
    }

    [Test]
    public void BuiltFrom_ReferenceSetup_ListsEmailAndUserPrincipalNameAsForms()
    {
        var email = Expression(_hrImport, 101, _model.Email, "mv[\"Account Name\"] + \"@panoply.local\"");
        var upn = Expression(_hrImport, 102, _model.UserPrincipalName, "mv[\"Email\"]");

        var built = Resolve().BuiltFrom(_accountName);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(built.Select(b => b.Mapping), Is.EquivalentTo(new[] { email, upn }));
            Assert.That(built.Select(b => b.Kind), Is.All.EqualTo(GeneratedValueBuiltValueKind.Form));
            Assert.That(built.Single(b => b.Mapping == email).Via, Is.Empty, "Email reads Account Name itself");
            Assert.That(built.Single(b => b.Mapping == upn).Via, Is.EqualTo(new[] { "Email" }), "User Principal Name is built via Email");
            Assert.That(built.Single(b => b.Mapping == upn).MetaverseAttributeId, Is.EqualTo(_model.UserPrincipalName.Id));
        }
    }

    [Test]
    public void BuiltFrom_OtherInputsThatAreNotGenerated_StillBuiltFromTheGeneratedValueAlone()
    {
        var upn = Expression(_hrImport, 101, _model.UserPrincipalName, "mv[\"Account Name\"] + \"@\" + mv[\"Region\"]");
        Expression(ImportRule(3, "Directory Import", DirectorySystemId), 150, _model.Region, "cs[\"region\"]");

        var built = Resolve().BuiltFrom(_accountName);

        Assert.That(built.Single().Mapping, Is.SameAs(upn), "an input that is not a generated value (a UPN suffix, a region) is allowed");
    }

    [Test]
    public void BuiltFrom_DerivedOnAnotherRule_IsBuiltOnAnotherRule()
    {
        var mailImport = ImportRule(4, "Mail Import", MailSystemId);
        mailImport.MetaverseObjectType = _model.Person;
        var email = Expression(mailImport, 120, _model.Email, "mv[\"Account Name\"] + \"@panoply.local\"");

        var built = Resolve(mailImport).BuiltFrom(_accountName);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(built.Single().Mapping, Is.SameAs(email));
            Assert.That(built.Single().Kind, Is.EqualTo(GeneratedValueBuiltValueKind.BuiltOnAnotherRule));
            Assert.That(built.Single().SyncRule, Is.SameAs(mailImport));
        }
    }

    [Test]
    public void BuiltFrom_ChainThroughAnotherRule_IsBuiltOnAnotherRuleEvenWhenTheLastStepIsOnTheOwnRule()
    {
        var mailImport = ImportRule(4, "Mail Import", MailSystemId);
        mailImport.MetaverseObjectType = _model.Person;
        Expression(mailImport, 120, _model.Email, "mv[\"Account Name\"] + \"@panoply.local\"");
        var upn = Expression(_hrImport, 102, _model.UserPrincipalName, "mv[\"Email\"]");

        var built = Resolve(mailImport).BuiltFrom(_accountName);

        Assert.That(built.Single(b => b.Mapping == upn).Kind, Is.EqualTo(GeneratedValueBuiltValueKind.BuiltOnAnotherRule),
            "Email is worked out on Mail Import, so User Principal Name cannot be worked out on HR Import before Account Name is issued");
    }

    [Test]
    public void BuiltFrom_TwoGeneratedInputs_IsBuiltFromSeveralGeneratedValues()
    {
        Generated(_hrImport, 103, _model.MailNickname, "Lower(cs[\"first\"])");
        var upn = Expression(_hrImport, 102, _model.UserPrincipalName, "mv[\"Account Name\"] + mv[\"Mail Nickname\"]");

        var built = Resolve().BuiltFrom(_accountName);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(built.Single().Mapping, Is.SameAs(upn));
            Assert.That(built.Single().Kind, Is.EqualTo(GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues));
        }
    }

    [Test]
    public void BuiltFrom_ExportExpressionReadingTheGeneratedValue_IsAFormInTheSystemItWritesTo()
    {
        var sAMAccountName = new ConnectedSystemObjectTypeAttribute { Id = 500, Name = "sAMAccountName", Type = AttributeDataType.Text };
        var exportExpression = ExportExpression(_directoryExport, 200, sAMAccountName, "Left(mv[\"Account Name\"], 20)");

        var built = Resolve().BuiltFrom(_accountName);

        using (Assert.EnterMultipleScope())
        {
            var value = built.Single();
            Assert.That(value.Mapping, Is.SameAs(exportExpression));
            Assert.That(value.Kind, Is.EqualTo(GeneratedValueBuiltValueKind.Form));
            Assert.That(value.MetaverseAttributeId, Is.Null);
            Assert.That(value.ExportedTo, Is.EqualTo(new[] { (DirectorySystemId, sAMAccountName.Id) }));
        }
    }

    [Test]
    public void BuiltFrom_ExportExpressionReadingADerivedValueBuiltOnAnotherRule_IsBuiltOnAnotherRule()
    {
        var mailImport = ImportRule(4, "Mail Import", MailSystemId);
        mailImport.MetaverseObjectType = _model.Person;
        Expression(mailImport, 120, _model.Email, "mv[\"Account Name\"] + \"@panoply.local\"");
        var mail = new ConnectedSystemObjectTypeAttribute { Id = 501, Name = "mail", Type = AttributeDataType.Text };
        var exportExpression = ExportExpression(_directoryExport, 200, mail, "Lower(mv[\"Email\"])");

        var built = Resolve(mailImport).BuiltFrom(_accountName);

        Assert.That(built.Single(b => b.Mapping == exportExpression).Kind, Is.EqualTo(GeneratedValueBuiltValueKind.BuiltOnAnotherRule));
    }

    [Test]
    public void BuiltFrom_DerivedValue_ListsWhereItIsExportedUnchanged()
    {
        Expression(_hrImport, 101, _model.Email, "mv[\"Account Name\"] + \"@panoply.local\"");
        var mail = new ConnectedSystemObjectTypeAttribute { Id = 501, Name = "mail", Type = AttributeDataType.Text };
        ExportDirect(_directoryExport, 200, mail, _model.Email);

        var built = Resolve().BuiltFrom(_accountName);

        Assert.That(built.Single().ExportedTo, Is.EqualTo(new[] { (DirectorySystemId, mail.Id) }));
    }

    [Test]
    public void BuiltFrom_DisabledDerivedFlow_IsNotListed()
    {
        Expression(_hrImport, 101, _model.Email, "mv[\"Account Name\"] + \"@panoply.local\"", enabled: false);

        var built = Resolve().BuiltFrom(_accountName);

        Assert.That(built, Is.Empty, "a disabled flow builds nothing");
    }

    [Test]
    public void BuiltFrom_ReadsAnotherObjectTypesAttributeOfTheSameName_IsNotListed()
    {
        // Group's Email is derived from the group's Name, which is not generated: nothing on Group is built from Account Name.
        var groupImport = ImportRule(5, "Group Import", HrSystemId, GroupTypeId);
        groupImport.MetaverseObjectType = _model.Group;
        Expression(groupImport, 130, _model.Email, "mv[\"Name\"] + \"@panoply.local\"");

        var built = Resolve(groupImport).BuiltFrom(_accountName);

        Assert.That(built, Is.Empty);
    }

    [Test]
    public void BuiltFrom_CarriesEachValuesKeepUniqueSwitch()
    {
        var email = Expression(_hrImport, 101, _model.Email, "mv[\"Account Name\"] + \"@panoply.local\"");
        email.KeepUnique = false;

        var built = Resolve().BuiltFrom(_accountName);

        Assert.That(built.Single().KeepUnique, Is.False);
    }

    [Test]
    public void GeneratedValuesBuilding_DerivedMapping_NamesTheGeneratedMappingItIsBuiltFrom()
    {
        Expression(_hrImport, 101, _model.Email, "mv[\"Account Name\"] + \"@panoply.local\"");
        var upn = Expression(_hrImport, 102, _model.UserPrincipalName, "mv[\"Email\"]");

        var building = Resolve().GeneratedValuesBuilding(upn);

        Assert.That(building, Is.EqualTo(new[] { _accountName }));
    }

    [Test]
    public void GeneratedValuesBuilding_MappingReadingNoGeneratedValue_IsEmpty()
    {
        var displayName = Expression(_hrImport, 101, _model.DisplayName, "cs[\"first\"] + \" \" + cs[\"last\"]");

        var building = Resolve().GeneratedValuesBuilding(displayName);

        Assert.That(building, Is.Empty);
    }

    [Test]
    public void BuiltValueOf_DerivedValue_DescribesItRelativeToTheGeneratedValue()
    {
        Expression(_hrImport, 101, _model.Email, "mv[\"Account Name\"] + \"@panoply.local\"");
        var upn = Expression(_hrImport, 102, _model.UserPrincipalName, "mv[\"Email\"]");

        var value = Resolve().BuiltValueOf(upn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(value, Is.Not.Null);
            Assert.That(value!.GeneratedMapping, Is.SameAs(_accountName));
            Assert.That(value.Kind, Is.EqualTo(GeneratedValueBuiltValueKind.Form));
            Assert.That(value.Via, Is.EqualTo(new[] { "Email" }));
            Assert.That(value.BuiltFromAttributeNames, Is.EqualTo(new[] { "Account Name" }));
        }
    }

    [Test]
    public void BuiltValueOf_FlowBeingEditedThatIsNotSavedYet_IsDescribed()
    {
        // The portal describes the Attribute Flow as it is being edited, before it has an id.
        var proposal = new SyncRuleMapping { SyncRule = _hrImport, SyncRuleId = _hrImport.Id, TargetMetaverseAttribute = _model.Email, TargetMetaverseAttributeId = _model.Email.Id };
        proposal.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "mv[\"Account Name\"] + \"@panoply.local\"" });

        var value = Resolve().BuiltValueOf(proposal);

        Assert.That(value?.GeneratedMapping, Is.SameAs(_accountName));
    }

    [Test]
    public void BuiltValueOf_BuiltFromTwoGeneratedValues_NamesBoth()
    {
        Generated(_hrImport, 103, _model.MailNickname, "Lower(cs[\"first\"])");
        var upn = Expression(_hrImport, 102, _model.UserPrincipalName, "mv[\"Account Name\"] + mv[\"Mail Nickname\"]");

        var value = Resolve().BuiltValueOf(upn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(value!.Kind, Is.EqualTo(GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues));
            Assert.That(value.BuiltFromAttributeNames, Is.EquivalentTo(new[] { "Account Name", "Mail Nickname" }));
        }
    }

    [Test]
    public void BuiltValueOf_FlowReadingNoGeneratedValue_IsNull()
    {
        var displayName = Expression(_hrImport, 101, _model.DisplayName, "cs[\"first\"] + \" \" + cs[\"last\"]");

        Assert.That(Resolve().BuiltValueOf(displayName), Is.Null);
    }

    // ---- Helpers ----

    private GeneratedValueBuiltValues Resolve(params SyncRule[] otherRules)
    {
        SyncRule[] rules = [_hrImport, _directoryExport, .. otherRules];
        var graph = DerivedFlowGraphFactory.Create(rules, _model.Types);
        return GeneratedValueBuiltValues.Create(rules, graph);
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
