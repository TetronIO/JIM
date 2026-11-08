// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Application.Staging;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Staging;

/// <summary>
/// Which target group member attributes export evaluation loads only for the members a page's changes add or remove
/// (#2832), and the guard that stops evaluation judging anything else against such an attribute.
/// </summary>
[TestFixture]
public class ExportEvaluationMemberLoadPlannerTests
{
    private const int TargetSystemId = 2;

    private MetaverseObjectType _groupType = null!;
    private MetaverseAttribute _mvMember = null!;
    private MetaverseAttribute _mvOwner = null!;
    private ConnectedSystemObjectTypeAttribute _member = null!;
    private MetaverseObject _group = null!;
    private ConnectedSystemObject _targetGroup = null!;

    [SetUp]
    public void SetUp()
    {
        _groupType = new MetaverseObjectType { Id = 5, Name = "Group" };
        _mvMember = new MetaverseAttribute { Id = 50, Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        _mvOwner = new MetaverseAttribute { Id = 51, Name = "Owner", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.SingleValued };
        _member = new ConnectedSystemObjectTypeAttribute { Id = 500, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        _group = new MetaverseObject { Id = Guid.NewGuid(), Type = _groupType };
        _targetGroup = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = TargetSystemId, MetaverseObjectId = _group.Id };
    }

    [Test]
    public void Plan_DirectMemberFlow_AsksAboutTheMembersAddedAndRemoved()
    {
        var cache = Cache(ExportRule(DirectFlow(_mvMember, _member)));
        var added = Member(Guid.NewGuid());
        var removed = Member(Guid.NewGuid());

        var questions = ExportEvaluationMemberLoadPlanner.Plan(cache, [(_group, [added, removed])]);

        Assert.That(questions, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(questions[0].ConnectedSystemObjectId, Is.EqualTo(_targetGroup.Id));
            Assert.That(questions[0].AttributeId, Is.EqualTo(_member.Id));
            Assert.That(questions[0].MetaverseObjectIds, Is.EquivalentTo(new[] { added.ReferenceValueId!.Value, removed.ReferenceValueId!.Value }));
        }
    }

    [Test]
    public void Plan_NoMemberChanged_AsksAboutNoMemberSoNoneIsLoaded()
    {
        var cache = Cache(ExportRule(DirectFlow(_mvMember, _member)));
        var description = new MetaverseObjectAttributeValue { AttributeId = 99, StringValue = "renamed" };

        var questions = ExportEvaluationMemberLoadPlanner.Plan(cache, [(_group, [description])]);

        Assert.That(questions.Single().MetaverseObjectIds, Is.Empty,
            "an evaluation changing no member stages no member change, so it asks about none and the group's members are not loaded");
    }

    [Test]
    public void Plan_ExpressionFlowIntoTheMemberAttribute_LoadsItWhole()
    {
        var expression = new SyncRuleMapping { TargetConnectedSystemAttribute = _member, TargetConnectedSystemAttributeId = _member.Id };
        expression.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"Static Members\"]" });
        var cache = Cache(ExportRule(DirectFlow(_mvMember, _member)), ExportRule(expression));

        var questions = ExportEvaluationMemberLoadPlanner.Plan(cache, [(_group, [Member(Guid.NewGuid())])]);

        Assert.That(questions, Is.Empty, "an expression's result is diffed against the whole held set, so the attribute is loaded whole");
    }

    [Test]
    public void Plan_SingleValuedSourceIntoTheMemberAttribute_LoadsItWhole()
    {
        var cache = Cache(ExportRule(DirectFlow(_mvOwner, _member)));

        var questions = ExportEvaluationMemberLoadPlanner.Plan(cache, [(_group, [Member(Guid.NewGuid(), _mvOwner)])]);

        Assert.That(questions, Is.Empty, "a single value into a multi-valued target is an Update meaning the whole set, so it is loaded whole");
    }

    [Test]
    public void Plan_NoTargetObjectYet_AsksNothing()
    {
        var cache = Cache(ExportRule(DirectFlow(_mvMember, _member)));
        cache.CsoLookup.Clear();

        var questions = ExportEvaluationMemberLoadPlanner.Plan(cache, [(_group, [Member(Guid.NewGuid())])]);

        Assert.That(questions, Is.Empty, "a Create reads no held values");
    }

    [Test]
    public void EnsureJudgedByLoadedValues_ChangeNamingAMemberLoadedFor_DoesNotThrow()
    {
        var member = Guid.NewGuid();
        var cache = Cache();
        cache.PartiallyLoadedCsoAttributeValues[(_targetGroup.Id, _member.Id)] = new HashSet<Guid> { member };

        Assert.DoesNotThrow(() => ExportEvaluationServer.EnsureJudgedByLoadedValues(cache, _targetGroup.Id,
            [Change(PendingExportAttributeChangeType.Add, member), Change(PendingExportAttributeChangeType.Remove, member)]));
    }

    [Test]
    public void EnsureJudgedByLoadedValues_ChangeNamingAMemberNotLoadedFor_Throws()
    {
        var cache = Cache();
        cache.PartiallyLoadedCsoAttributeValues[(_targetGroup.Id, _member.Id)] = new HashSet<Guid> { Guid.NewGuid() };

        Assert.Throws<InvalidOperationException>(() => ExportEvaluationServer.EnsureJudgedByLoadedValues(cache, _targetGroup.Id,
            [Change(PendingExportAttributeChangeType.Add, Guid.NewGuid())]),
            "the values loaded say nothing about a member they were not loaded for");
    }

    [Test]
    public void EnsureJudgedByLoadedValues_WholeSetChange_Throws()
    {
        var member = Guid.NewGuid();
        var cache = Cache();
        cache.PartiallyLoadedCsoAttributeValues[(_targetGroup.Id, _member.Id)] = new HashSet<Guid> { member };

        Assert.Throws<InvalidOperationException>(() => ExportEvaluationServer.EnsureJudgedByLoadedValues(cache, _targetGroup.Id,
            [Change(PendingExportAttributeChangeType.Update, member)]),
            "an Update against a multi-valued attribute is judged against the whole set, which was not loaded");
    }

    #region Helpers

    private ExportEvaluationCache Cache(params SyncRule[] rules) => new(
        new Dictionary<int, List<SyncRule>> { [_groupType.Id] = [.. rules] },
        new Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject> { [(_group.Id, TargetSystemId)] = _targetGroup },
        Array.Empty<ConnectedSystemObjectAttributeValue>().ToLookup(av => (av.ConnectedSystemObject.Id, av.AttributeId)),
        [TargetSystemId]);

    private SyncRule ExportRule(params SyncRuleMapping[] mappings)
    {
        var rule = new SyncRule
        {
            Name = "Groups",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemId = TargetSystemId,
            MetaverseObjectTypeId = _groupType.Id
        };
        rule.AttributeFlowRules.AddRange(mappings);
        return rule;
    }

    private static SyncRuleMapping DirectFlow(MetaverseAttribute source, ConnectedSystemObjectTypeAttribute target)
    {
        var mapping = new SyncRuleMapping { TargetConnectedSystemAttribute = target, TargetConnectedSystemAttributeId = target.Id };
        mapping.Sources.Add(new SyncRuleMappingSource { MetaverseAttribute = source, MetaverseAttributeId = source.Id });
        return mapping;
    }

    private MetaverseObjectAttributeValue Member(Guid referencedMetaverseObjectId, MetaverseAttribute? attribute = null)
    {
        attribute ??= _mvMember;
        return new MetaverseObjectAttributeValue { AttributeId = attribute.Id, Attribute = attribute, ReferenceValueId = referencedMetaverseObjectId };
    }

    private PendingExportAttributeValueChange Change(PendingExportAttributeChangeType changeType, Guid member) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = _member,
        AttributeId = _member.Id,
        ChangeType = changeType,
        UnresolvedReferenceValue = member.ToString()
    };

    #endregion
}
