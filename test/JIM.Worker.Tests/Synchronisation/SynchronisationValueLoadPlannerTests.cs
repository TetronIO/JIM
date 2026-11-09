// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Staging;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// #2818: which multi-valued reference attributes a Delta Synchronisation may load without their values. Only where an
/// Enforce State export rule's direct mapping is the only reader: anything on the import side that reads the attribute
/// (Attribute Flow, direct or by Expression, a Scoping Criterion, an Object Matching Rule), or a drift comparison that reads
/// the held values themselves (an Expression or generated mapping), keeps it whole, and says why.
/// </summary>
[TestFixture]
public class SynchronisationValueLoadPlannerTests
{
    private const int SystemId = 3;

    private ConnectedSystemObjectType _groupType = null!;
    private ConnectedSystemObjectTypeAttribute _member = null!;
    private ConnectedSystemObjectTypeAttribute _name = null!;
    private MetaverseObjectType _mvGroup = null!;
    private MetaverseAttribute _staticMembers = null!;
    private MetaverseAttribute _displayName = null!;

    [SetUp]
    public void SetUp()
    {
        _groupType = new ConnectedSystemObjectType { Id = 30, Name = "group", ConnectedSystemId = SystemId };
        _member = new ConnectedSystemObjectTypeAttribute { Id = 301, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        _name = new ConnectedSystemObjectTypeAttribute { Id = 302, Name = "cn", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        _groupType.Attributes.AddRange([_member, _name]);
        _mvGroup = new MetaverseObjectType { Id = 40, Name = "Group", PluralName = "Groups" };
        _staticMembers = new MetaverseAttribute { Id = 401, Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        _displayName = new MetaverseAttribute { Id = 402, Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
    }

    [Test]
    public void Plan_EnforcedDirectMappingIsTheOnlyReader_LeavesTheAttributeOut()
    {
        var (plan, fallbacks) = Plan([EnforcedExport(_member, _staticMembers), ImportReading(_name)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.AttributesByObjectTypeId[_groupType.Id].Select(a => a.Id), Is.EqualTo(new[] { _member.Id }));
            Assert.That(fallbacks, Is.Empty);
            Assert.That(plan.MinimumHeldValueCount, Is.EqualTo(5));
        }
    }

    [Test]
    public void Plan_NoEnforcedMapping_LeavesNothingOut()
    {
        var notEnforced = EnforcedExport(_member, _staticMembers);
        notEnforced.EnforceState = false;
        var disabledMapping = EnforcedExport(_member, _staticMembers);
        disabledMapping.AttributeFlowRules[0].Enabled = false;
        var disabledRule = EnforcedExport(_member, _staticMembers);
        disabledRule.Enabled = false;

        var (plan, _) = Plan([notEnforced, disabledMapping, disabledRule]);

        Assert.That(plan.AttributesByObjectTypeId, Is.Empty);
    }

    [TestCase(SynchronisationValueLoadFallbackReason.ReadByImportAttributeFlow)]
    [TestCase(SynchronisationValueLoadFallbackReason.ReadByImportExpression)]
    [TestCase(SynchronisationValueLoadFallbackReason.ReadByScopingCriterion)]
    [TestCase(SynchronisationValueLoadFallbackReason.ReadByObjectMatchingRule)]
    [TestCase(SynchronisationValueLoadFallbackReason.ReadByDriftExpression)]
    [TestCase(SynchronisationValueLoadFallbackReason.ReadByGeneratedValueDrift)]
    [TestCase(SynchronisationValueLoadFallbackReason.ExpectedValuesNotReferences)]
    public void Plan_AnotherReader_KeepsTheAttributeWholeAndSaysWhy(SynchronisationValueLoadFallbackReason reason)
    {
        var rules = new List<SyncRule> { EnforcedExport(_member, _staticMembers) };
        switch (reason)
        {
            case SynchronisationValueLoadFallbackReason.ReadByImportAttributeFlow:
                rules.Add(ImportReading(_member));
                break;
            case SynchronisationValueLoadFallbackReason.ReadByImportExpression:
                rules.Add(ImportExpression("Count(cs[\"MEMBER\"])"));
                break;
            case SynchronisationValueLoadFallbackReason.ReadByScopingCriterion:
                var scoped = ImportReading(_name);
                scoped.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
                {
                    ChildGroups = { new SyncRuleScopingCriteriaGroup { Criteria = { new SyncRuleScopingCriteria { ConnectedSystemAttribute = _member, ConnectedSystemAttributeId = _member.Id } } } }
                });
                rules.Add(scoped);
                break;
            case SynchronisationValueLoadFallbackReason.ReadByObjectMatchingRule:
                _groupType.ObjectMatchingRules.Add(new ObjectMatchingRule
                {
                    Sources = { new ObjectMatchingRuleSource { ConnectedSystemAttributeId = _member.Id, ConnectedSystemAttribute = _member } }
                });
                break;
            case SynchronisationValueLoadFallbackReason.ReadByDriftExpression:
                var expression = EnforcedExport(_member, _staticMembers);
                expression.AttributeFlowRules[0].Sources.Clear();
                expression.AttributeFlowRules[0].Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"Static Members\"]" });
                rules.Add(expression);
                break;
            case SynchronisationValueLoadFallbackReason.ReadByGeneratedValueDrift:
                var generated = EnforcedExport(_member, _staticMembers);
                generated.AttributeFlowRules[0].Generation = new SyncRuleMappingGeneration();
                rules.Add(generated);
                break;
            case SynchronisationValueLoadFallbackReason.ExpectedValuesNotReferences:
                rules.Add(EnforcedExport(_member, _displayName));
                break;
        }

        var (plan, fallbacks) = Plan(rules);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.AttributesByObjectTypeId, Is.Empty);
            Assert.That(fallbacks.Select(f => (f.ObjectTypeId, f.AttributeId, f.Reason)), Does.Contain((_groupType.Id, _member.Id, reason)));
        }
    }

    /// <summary>
    /// An Expression that reaches the object's attributes other than by a written-out name could read any of them.
    /// </summary>
    [Test]
    public void Plan_ImportExpressionReadingAttributesByComputedName_KeepsTheAttributeWhole()
    {
        var (plan, fallbacks) = Plan([EnforcedExport(_member, _staticMembers), ImportExpression("cs[\"cn\"] + cs[Name]")]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.AttributesByObjectTypeId, Is.Empty);
            Assert.That(fallbacks.Single().Reason, Is.EqualTo(SynchronisationValueLoadFallbackReason.ReadByImportExpression));
        }
    }

    /// <summary>
    /// A disabled import rule or mapping reads nothing this run, but is kept to the same terms: leaving the attribute out
    /// is an optimisation, and loading it whole is always correct.
    /// </summary>
    [Test]
    public void Plan_DisabledImportMappingReadingTheAttribute_StillKeepsItWhole()
    {
        var import = ImportReading(_member);
        import.Enabled = false;

        var (plan, _) = Plan([EnforcedExport(_member, _staticMembers), import]);

        Assert.That(plan.AttributesByObjectTypeId, Is.Empty);
    }

    [Test]
    public void Plan_ExternalIdOrNamingAttribute_IsNeverLeftOut()
    {
        _member.IsExternalId = true;

        var (plan, _) = Plan([EnforcedExport(_member, _staticMembers)]);

        Assert.That(plan.AttributesByObjectTypeId, Is.Empty);
    }

    private (SynchronisationValueLoadPlan Plan, IReadOnlyList<SynchronisationValueLoadFallback> Fallbacks) Plan(List<SyncRule> rules) =>
        SynchronisationValueLoadPlanner.Plan([_groupType], rules, minimumHeldValueCount: 5);

    private SyncRule EnforcedExport(ConnectedSystemObjectTypeAttribute target, MetaverseAttribute source)
    {
        var rule = new SyncRule
        {
            Id = 1, Name = "Export", Enabled = true, EnforceState = true, Direction = SyncRuleDirection.Export,
            ConnectedSystemId = SystemId, ConnectedSystemObjectTypeId = _groupType.Id, ConnectedSystemObjectType = _groupType,
            MetaverseObjectTypeId = _mvGroup.Id, MetaverseObjectType = _mvGroup
        };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            TargetConnectedSystemAttribute = target, TargetConnectedSystemAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
        });
        return rule;
    }

    private SyncRule ImportReading(ConnectedSystemObjectTypeAttribute source)
    {
        var rule = new SyncRule
        {
            Id = 2, Name = "Import", Enabled = true, Direction = SyncRuleDirection.Import,
            ConnectedSystemId = SystemId, ConnectedSystemObjectTypeId = _groupType.Id, ConnectedSystemObjectType = _groupType,
            MetaverseObjectTypeId = _mvGroup.Id, MetaverseObjectType = _mvGroup
        };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            TargetMetaverseAttribute = _displayName,
            Sources = { new SyncRuleMappingSource { ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        });
        return rule;
    }

    private SyncRule ImportExpression(string expression)
    {
        var rule = ImportReading(_name);
        rule.AttributeFlowRules[0].Sources.Clear();
        rule.AttributeFlowRules[0].Sources.Add(new SyncRuleMappingSource { Expression = expression });
        return rule;
    }
}
