// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Logic;
using NUnit.Framework;
using static JIM.Worker.Tests.Services.DerivedFlowTestModel;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// The Metaverse-Derived Attribute Flow dependency graph (#1750, plan Phase 1): which import mappings are derived
/// (their expression reads <c>mv["..."]</c>), the level each Metaverse attribute is evaluated at, the canonical order
/// within a level, cycle reporting, and which Connected Systems host a derived flow reading an attribute.
/// </summary>
[TestFixture]
public class DerivedFlowGraphTests
{
    private DerivedFlowTestModel _model = null!;

    [SetUp]
    public void SetUp()
    {
        _model = new DerivedFlowTestModel();
    }

    private DerivedFlowGraph Build(DerivedFlowGraphScope scope, params SyncRule[] rules) => new(rules, _model.Types, scope);

    private DerivedFlowGraph Build(params SyncRule[] rules) => Build(DerivedFlowGraphScope.AllMappings, rules);

    // ---- what is derived ----

    [Test]
    public void IsDerived_ImportExpressionReadingOnlyConnectedSystem_IsFalse()
    {
        var rule = ImportRule(1, "HR Import", connectedSystemId: 1);
        var fromCs = Expression(rule, 100, _model.Email, "cs[\"mail\"]");
        var direct = Direct(rule, 101, _model.AccountName, "sAMAccountName");

        var graph = Build(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(graph.IsDerived(fromCs), Is.False);
            Assert.That(graph.IsDerived(direct), Is.False);
            Assert.That(graph.GetMetaverseInputIds(fromCs), Is.Empty);
            Assert.That(graph.MaxLevel(PersonTypeId), Is.EqualTo(0));
        }
    }

    [Test]
    public void IsDerived_ImportExpressionReadingMetaverse_IsTrueWithResolvedInputsInOrder()
    {
        var rule = ImportRule(1, "HR Import", connectedSystemId: 1);
        var displayName = Expression(rule, 100, _model.DisplayName, "mv[\"Last Name\"] + \", \" + mv[\"First Name\"] + cs[\"suffix\"]");

        var graph = Build(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(graph.IsDerived(displayName), Is.True);
            Assert.That(graph.GetMetaverseInputIds(displayName), Is.EqualTo(new[] { _model.LastName.Id, _model.FirstName.Id }));
        }
    }

    [Test]
    public void IsDerived_ExportExpressionReadingMetaverse_IsNotPartOfTheGraph()
    {
        var export = ExportRule(1, "AD Export", connectedSystemId: 2);
        var mapping = new SyncRuleMapping { Id = 100, SyncRule = export, SyncRuleId = export.Id, TargetConnectedSystemAttributeId = 500 };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"Email\"]" });
        export.AttributeFlowRules.Add(mapping);

        var graph = Build(export);

        Assert.That(graph.IsDerived(mapping), Is.False, "export expressions have always read mv; only import mappings can be derived");
    }

    [Test]
    public void IsDerived_MetaverseNamesResolveCaseInsensitively()
    {
        var rule = ImportRule(1, "HR Import", connectedSystemId: 1);
        var email = Expression(rule, 100, _model.Email, "mv[\"account NAME\"] + \"@corp.local\"");

        var graph = Build(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(graph.GetMetaverseInputIds(email), Is.EqualTo(new[] { _model.AccountName.Id }));
            Assert.That(graph.UnknownInputs, Is.Empty);
            Assert.That(graph.GetLevel(PersonTypeId, _model.Email.Id), Is.EqualTo(1));
        }
    }

    [Test]
    public void UnknownMetaverseName_IsRecordedNotAnEdge()
    {
        var rule = ImportRule(1, "HR Import", connectedSystemId: 1);
        var email = Expression(rule, 100, _model.Email, "mv[\"Acount Name\"] + \"@corp.local\"");

        var graph = Build(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(graph.IsDerived(email), Is.True, "reading mv at all is what makes a flow derived, whether or not the name resolves");
            Assert.That(graph.GetMetaverseInputIds(email), Is.Empty);
            Assert.That(graph.UnknownInputs, Has.Count.EqualTo(1));
            Assert.That(graph.UnknownInputs[0].Mapping, Is.SameAs(email));
            Assert.That(graph.UnknownInputs[0].AttributeName, Is.EqualTo("Acount Name"));
            Assert.That(graph.UnknownInputs[0].MetaverseObjectTypeName, Is.EqualTo("Person"));
            Assert.That(graph.HasCycle, Is.False);
        }
    }

    // ---- levels ----

    [Test]
    public void Levels_ChainOfDerivedFlows_AreAssignedInDependencyOrder()
    {
        var rule = ImportRule(1, "HR Import", connectedSystemId: 1);
        Direct(rule, 100, _model.AccountName, "sAMAccountName");
        var email = Expression(rule, 101, _model.Email, "mv[\"Account Name\"] + \"@corp.local\"");
        var upn = Expression(rule, 102, _model.UserPrincipalName, "mv[\"Email\"]");

        var graph = Build(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(graph.GetLevel(PersonTypeId, _model.AccountName.Id), Is.EqualTo(0));
            Assert.That(graph.GetLevel(PersonTypeId, _model.Email.Id), Is.EqualTo(1));
            Assert.That(graph.GetLevel(PersonTypeId, _model.UserPrincipalName.Id), Is.EqualTo(2));
            Assert.That(graph.GetLevel(PersonTypeId, _model.Region.Id), Is.EqualTo(0), "an attribute nothing derives is level 0");
            Assert.That(graph.MaxLevel(PersonTypeId), Is.EqualTo(2));
            Assert.That(graph.GetDerivedMappings(PersonTypeId, 0), Is.Empty);
            Assert.That(graph.GetDerivedMappings(PersonTypeId, 1), Is.EqualTo(new[] { email }));
            Assert.That(graph.GetDerivedMappings(PersonTypeId, 2), Is.EqualTo(new[] { upn }));
        }
    }

    [Test]
    public void Levels_AttributeReadingSeveralInputs_TakesOneMoreThanTheDeepest()
    {
        var rule = ImportRule(1, "HR Import", connectedSystemId: 1);
        Expression(rule, 101, _model.Email, "mv[\"Account Name\"] + \"@corp.local\"");
        Expression(rule, 102, _model.DisplayName, "mv[\"First Name\"] + \" <\" + mv[\"Email\"] + \">\"");

        var graph = Build(rule);

        Assert.That(graph.GetLevel(PersonTypeId, _model.DisplayName.Id), Is.EqualTo(2));
    }

    [Test]
    public void Levels_EveryDerivedContributorOfAnAttribute_RunsAtTheAttributesLevel()
    {
        // Email has two derived contributors on different rules: one reads a level 0 attribute, the other a level 1
        // attribute. Both must run at Email's level (2), or a dependant could read Email before its contenders resolve.
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var training = ImportRule(2, "Training Import", connectedSystemId: 2);
        Expression(hr, 101, _model.DisplayName, "mv[\"First Name\"] + \" \" + mv[\"Last Name\"]");
        var emailFromAccount = Expression(hr, 102, _model.Email, "mv[\"Account Name\"] + \"@corp.local\"", priority: 1);
        var emailFromDisplay = Expression(training, 103, _model.Email, "mv[\"Display Name\"] + \"@corp.local\"", priority: 2);

        var graph = Build(hr, training);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(graph.GetLevel(PersonTypeId, _model.Email.Id), Is.EqualTo(2));
            Assert.That(graph.GetDerivedMappings(PersonTypeId, 2), Is.EqualTo(new[] { emailFromAccount, emailFromDisplay }));
        }
    }

    [Test]
    public void GetDerivedMappings_WithinALevel_OrdersByPriorityThenMappingId()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var ad = ImportRule(2, "AD Import", connectedSystemId: 2);
        var fifth = Expression(hr, 5, _model.Email, "mv[\"Account Name\"]", priority: 2);
        var third = Expression(ad, 3, _model.Email, "mv[\"First Name\"]", priority: 2);
        var ninth = Expression(hr, 9, _model.DisplayName, "mv[\"First Name\"]", priority: 1);

        var graph = Build(hr, ad);

        Assert.That(graph.GetDerivedMappings(PersonTypeId, 1), Is.EqualTo(new[] { ninth, third, fifth }));
    }

    [Test]
    public void Levels_PermutedRuleAndMappingOrder_AreIdentical()
    {
        SyncRule[] Compose(bool reversed)
        {
            var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
            var ad = ImportRule(2, "AD Import", connectedSystemId: 2);
            var builders = new List<Action>
            {
                () => Expression(hr, 101, _model.Email, "mv[\"Account Name\"] + \"@corp.local\"", priority: 1),
                () => Expression(ad, 102, _model.UserPrincipalName, "mv[\"Email\"]"),
                () => Expression(hr, 103, _model.DisplayName, "mv[\"First Name\"] + \" \" + mv[\"Last Name\"]"),
                () => Expression(ad, 104, _model.Email, "mv[\"Display Name\"] + \"@ad.local\"", priority: 2),
                () => Direct(ad, 105, _model.AccountName, "sAMAccountName")
            };
            if (reversed)
                builders.Reverse();
            builders.ForEach(build => build());
            return reversed ? [ad, hr] : [hr, ad];
        }

        var forward = Build(Compose(reversed: false));
        var backward = Build(Compose(reversed: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(forward.MaxLevel(PersonTypeId), Is.EqualTo(backward.MaxLevel(PersonTypeId)));
            foreach (var attribute in _model.Person.Attributes)
                Assert.That(forward.GetLevel(PersonTypeId, attribute.Id), Is.EqualTo(backward.GetLevel(PersonTypeId, attribute.Id)), attribute.Name);
            for (var level = 0; level <= forward.MaxLevel(PersonTypeId); level++)
            {
                Assert.That(forward.GetDerivedMappings(PersonTypeId, level).Select(m => m.Id),
                    Is.EqualTo(backward.GetDerivedMappings(PersonTypeId, level).Select(m => m.Id)), $"level {level}");
            }
        }
    }

    [Test]
    public void Levels_AreKeyedPerMetaverseObjectType()
    {
        // Email is an attribute of both types. A Group rule deriving Email from the group's Name must not change
        // Person's graph, and vice versa.
        var groups = ImportRule(1, "Group Import", connectedSystemId: 1, metaverseObjectTypeId: GroupTypeId);
        var people = ImportRule(2, "HR Import", connectedSystemId: 2);
        var groupEmail = Expression(groups, 101, _model.Email, "mv[\"Name\"] + \"@groups.local\"");
        var personUpn = Expression(people, 102, _model.UserPrincipalName, "mv[\"Email\"]");

        var graph = Build(groups, people);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(graph.GetLevel(GroupTypeId, _model.Email.Id), Is.EqualTo(1));
            Assert.That(graph.GetLevel(PersonTypeId, _model.Email.Id), Is.EqualTo(0), "Person's Email has no derived contributor");
            Assert.That(graph.GetLevel(PersonTypeId, _model.UserPrincipalName.Id), Is.EqualTo(1));
            Assert.That(graph.MaxLevel(GroupTypeId), Is.EqualTo(1));
            Assert.That(graph.GetDerivedMappings(GroupTypeId, 1), Is.EqualTo(new[] { groupEmail }));
            Assert.That(graph.GetDerivedMappings(PersonTypeId, 1), Is.EqualTo(new[] { personUpn }));
        }
    }

    // ---- generated mappings (#242) ----

    [Test]
    public void GeneratedMapping_WithMetaverseBaseExpression_IsADerivedNode()
    {
        var rule = ImportRule(1, "HR Import", connectedSystemId: 1);
        var generated = Generated(rule, 101, _model.AccountName, "Lower(mv[\"First Name\"] + \".\" + mv[\"Last Name\"])");
        var email = Expression(rule, 102, _model.Email, "mv[\"Account Name\"] + \"@corp.local\"");

        var graph = Build(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(graph.IsDerived(generated), Is.True);
            Assert.That(graph.GetLevel(PersonTypeId, _model.AccountName.Id), Is.EqualTo(1));
            Assert.That(graph.GetLevel(PersonTypeId, _model.Email.Id), Is.EqualTo(2));
            Assert.That(graph.GetDerivedMappings(PersonTypeId, 1), Is.EqualTo(new[] { generated }));
            Assert.That(graph.GetDerivedMappings(PersonTypeId, 2), Is.EqualTo(new[] { email }));
        }
    }

    [Test]
    public void GeneratedMapping_WithoutMetaverseInputs_IsALevelZeroNodeOthersCanRead()
    {
        var rule = ImportRule(1, "HR Import", connectedSystemId: 1);
        var generatedFromCs = Generated(rule, 101, _model.AccountName, "Lower(cs[\"givenName\"])");
        var generatedNoBase = Generated(rule, 102, _model.Region, baseExpression: null);
        var email = Expression(rule, 103, _model.Email, "mv[\"Account Name\"] + \"@\" + mv[\"Region\"]");

        var graph = Build(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(graph.IsDerived(generatedFromCs), Is.False);
            Assert.That(graph.IsDerived(generatedNoBase), Is.False);
            Assert.That(graph.GetLevel(PersonTypeId, _model.AccountName.Id), Is.EqualTo(0));
            Assert.That(graph.GetLevel(PersonTypeId, _model.Email.Id), Is.EqualTo(1));
            Assert.That(graph.IsDerived(email), Is.True);
        }
    }

    // ---- cycles ----

    [Test]
    public void SelfReference_IsACycleOfOne()
    {
        var rule = ImportRule(1, "HR Import", connectedSystemId: 1);
        var displayName = Expression(rule, 101, _model.DisplayName, "mv[\"Display Name\"] + cs[\"suffix\"]");

        var graph = Build(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(graph.HasCycle, Is.True);
            Assert.That(graph.Cycles, Has.Count.EqualTo(1));
            Assert.That(graph.Cycles[0].Members, Has.Count.EqualTo(1));
            Assert.That(graph.Cycles[0].Members[0].Mapping, Is.SameAs(displayName));
            Assert.That(graph.Cycles[0].Members[0].MetaverseAttributeName, Is.EqualTo("Display Name"));
            Assert.That(graph.Cycles[0].Members[0].ReadsMetaverseAttributeName, Is.EqualTo("Display Name"));
            Assert.That(graph.GetLevel(PersonTypeId, _model.DisplayName.Id), Is.Null);
        }
    }

    [Test]
    public void TwoRuleCycle_ReportsBothAttributesMappingsAndRules()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var ad = ImportRule(2, "AD Import", connectedSystemId: 2);
        var displayName = Expression(hr, 101, _model.DisplayName, "mv[\"Mail Nickname\"]");
        var mailNickname = Expression(ad, 102, _model.MailNickname, "mv[\"Display Name\"]");

        var graph = Build(hr, ad);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(graph.Cycles, Has.Count.EqualTo(1));
            var members = graph.Cycles[0].Members;
            Assert.That(members.Select(m => m.Mapping), Is.EquivalentTo(new[] { displayName, mailNickname }));
            Assert.That(members.Select(m => m.SyncRule), Is.EquivalentTo(new[] { hr, ad }));
            Assert.That(members.Select(m => m.MetaverseAttributeName), Is.EquivalentTo(new[] { "Display Name", "Mail Nickname" }));
            Assert.That(graph.GetLevel(PersonTypeId, _model.DisplayName.Id), Is.Null);
            Assert.That(graph.GetLevel(PersonTypeId, _model.MailNickname.Id), Is.Null);
        }
    }

    [Test]
    public void ThreeNodeCycle_MembersFormAPathThatReadsBackToTheStart()
    {
        var rule = ImportRule(1, "HR Import", connectedSystemId: 1);
        Expression(rule, 101, _model.Email, "mv[\"Display Name\"]");
        Expression(rule, 102, _model.DisplayName, "mv[\"User Principal Name\"]");
        Expression(rule, 103, _model.UserPrincipalName, "mv[\"Email\"]");

        var graph = Build(rule);

        Assert.That(graph.Cycles, Has.Count.EqualTo(1));
        var members = graph.Cycles[0].Members;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(members, Has.Count.EqualTo(3));
            for (var i = 0; i < members.Count; i++)
            {
                var next = members[(i + 1) % members.Count];
                Assert.That(members[i].ReadsMetaverseAttributeId, Is.EqualTo(next.MetaverseAttributeId),
                    "each member reads the attribute the next member derives, closing the loop");
            }
        }
    }

    [Test]
    public void AttributeDownstreamOfACycle_HasNoLevelButIsNotACycleMember()
    {
        var rule = ImportRule(1, "HR Import", connectedSystemId: 1);
        Expression(rule, 101, _model.DisplayName, "mv[\"Mail Nickname\"]");
        Expression(rule, 102, _model.MailNickname, "mv[\"Display Name\"]");
        Expression(rule, 103, _model.Email, "mv[\"Display Name\"] + \"@corp.local\"");

        var graph = Build(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(graph.Cycles, Has.Count.EqualTo(1));
            Assert.That(graph.Cycles[0].Members.Select(m => m.MetaverseAttributeName), Does.Not.Contain("Email"));
            Assert.That(graph.GetLevel(PersonTypeId, _model.Email.Id), Is.Null, "Email cannot be ordered while its input is on a cycle");
        }
    }

    // ---- scope: validation includes disabled, run time excludes them (plan decision 2) ----

    [Test]
    public void DisabledMapping_IsInTheValidationGraphAndAbsentFromTheRunTimeGraph()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var ad = ImportRule(2, "AD Import", connectedSystemId: 2, enabled: false);
        var disabledMapping = Expression(hr, 101, _model.DisplayName, "mv[\"Mail Nickname\"]", enabled: false);
        var onDisabledRule = Expression(ad, 102, _model.MailNickname, "mv[\"Display Name\"]");

        var validation = Build(DerivedFlowGraphScope.AllMappings, hr, ad);
        var runTime = Build(DerivedFlowGraphScope.EnabledMappingsOnly, hr, ad);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validation.HasCycle, Is.True, "enabling either flow later must never be able to introduce a cycle");
            Assert.That(validation.IsDerived(disabledMapping), Is.True);
            Assert.That(runTime.HasCycle, Is.False);
            Assert.That(runTime.IsDerived(disabledMapping), Is.False);
            Assert.That(runTime.IsDerived(onDisabledRule), Is.False);
            Assert.That(runTime.MaxLevel(PersonTypeId), Is.EqualTo(0));
        }
    }

    // ---- hosting systems (drives marking in Phase 3) ----

    [Test]
    public void GetHostingSystemsReading_FollowsDerivedLevelsTransitively()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 10);
        var ad = ImportRule(2, "AD Import", connectedSystemId: 20);
        var training = ImportRule(3, "Training Import", connectedSystemId: 30);
        Expression(hr, 101, _model.Email, "mv[\"Account Name\"] + \"@corp.local\"");
        Expression(ad, 102, _model.UserPrincipalName, "mv[\"Email\"]");
        Expression(training, 103, _model.Region, "cs[\"region\"]");

        var graph = Build(hr, ad, training);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(graph.GetHostingSystemsReading(PersonTypeId, _model.AccountName.Id), Is.EqualTo(new[] { 10, 20 }));
            Assert.That(graph.GetHostingSystemsReading(PersonTypeId, _model.Email.Id), Is.EqualTo(new[] { 20 }));
            Assert.That(graph.GetHostingSystemsReading(PersonTypeId, _model.UserPrincipalName.Id), Is.Empty);
            Assert.That(graph.GetHostingSystemsReading(PersonTypeId, _model.Region.Id), Is.Empty);
            Assert.That(graph.GetHostingSystemsReading(GroupTypeId, _model.Email.Id), Is.Empty, "hosting systems are per type");
        }
    }

    [Test]
    public void GetHostingSystemsReading_TerminatesOnACycle()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 10);
        var ad = ImportRule(2, "AD Import", connectedSystemId: 20);
        Expression(hr, 101, _model.DisplayName, "mv[\"Mail Nickname\"]");
        Expression(ad, 102, _model.MailNickname, "mv[\"Display Name\"]");

        var graph = Build(hr, ad);

        Assert.That(graph.GetHostingSystemsReading(PersonTypeId, _model.DisplayName.Id), Is.EqualTo(new[] { 10, 20 }));
    }
}
