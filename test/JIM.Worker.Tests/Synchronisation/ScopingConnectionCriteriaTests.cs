// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Application.Servers.Scoping;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Models.Search;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Connection criteria (#2021, Phase 7a): an export rule's scope tests whether the Metaverse Object is joined to an object
/// of a given Object Type in a given Connected System. GAL sync uses them to send a forest's people to the other forest
/// as contacts ("has a user in PANOPLY AD, and none in RESURGAM AD"). Which Object Type the identity is joined to decides
/// it, never how the join was made, so the rule's own contacts can never change its answer.
/// </summary>
[TestFixture]
public class ScopingConnectionCriteriaTests
{
    private static readonly ConnectedSystem Panoply = new() { Id = 1, Name = "PANOPLY AD" };
    private static readonly ConnectedSystem Resurgam = new() { Id = 2, Name = "RESURGAM AD" };
    private static readonly ConnectedSystemObjectType PanoplyUser = new() { Id = 11, Name = "user", ConnectedSystemId = 1, ConnectedSystem = Panoply };
    private static readonly ConnectedSystemObjectType PanoplyContact = new() { Id = 12, Name = "contact", ConnectedSystemId = 1, ConnectedSystem = Panoply };
    private static readonly ConnectedSystemObjectType ResurgamUser = new() { Id = 21, Name = "user", ConnectedSystemId = 2, ConnectedSystem = Resurgam };
    private static readonly ConnectedSystemObjectType ResurgamContact = new() { Id = 22, Name = "contact", ConnectedSystemId = 2, ConnectedSystem = Resurgam };

    private ScopingEvaluationServer _scoping = null!;

    [SetUp]
    public void SetUp() => _scoping = new ScopingEvaluationServer();

    [Test]
    public void IsMvoInScopeForExportRule_HasAndJoinedToThatType_IsInScope()
    {
        var mvo = Mvo();

        var inScope = _scoping.IsMvoInScopeForExportRule(mvo, ExportRule(Has(PanoplyUser)), connections: Connections(mvo, PanoplyUser));

        Assert.That(inScope, Is.True);
    }

    [Test]
    public void IsMvoInScopeForExportRule_HasButJoinedOnlyToAnotherTypeInTheSameSystem_IsNotInScope()
    {
        // A PANOPLY contact is not a PANOPLY user: the type decides, not the system alone.
        var mvo = Mvo();

        var inScope = _scoping.IsMvoInScopeForExportRule(mvo, ExportRule(Has(PanoplyUser)), connections: Connections(mvo, PanoplyContact));

        Assert.That(inScope, Is.False);
    }

    [Test]
    public void IsMvoInScopeForExportRule_HasNoAndNotJoined_IsInScope()
    {
        var mvo = Mvo();

        var inScope = _scoping.IsMvoInScopeForExportRule(mvo, ExportRule(HasNo(ResurgamUser)), connections: Connections(mvo));

        Assert.That(inScope, Is.True);
    }

    [Test]
    public void IsMvoInScopeForExportRule_HasNoAndJoinedToThatType_IsNotInScope()
    {
        var mvo = Mvo();

        var inScope = _scoping.IsMvoInScopeForExportRule(mvo, ExportRule(HasNo(ResurgamUser)), connections: Connections(mvo, ResurgamUser));

        Assert.That(inScope, Is.False);
    }

    [TestCase(new[] { 11 }, true, TestName = "GalRule_PanoplyPersonOnly_GetsAContact")]
    [TestCase(new[] { 11, 22 }, true, TestName = "GalRule_PanoplyPersonWithTheRulesOwnContact_StaysInScope")]
    [TestCase(new[] { 11, 21 }, false, TestName = "GalRule_PersonWithAnAccountInBothForests_GetsNoContact")]
    [TestCase(new[] { 21, 12 }, false, TestName = "GalRule_ResurgamPersonWithAPanoplyContact_NeverComesBack")]
    [TestCase(new int[0], false, TestName = "GalRule_LeaverWithNoAccountLeft_LeavesScope")]
    public void IsMvoInScopeForExportRule_GalSyncRule_ScopesToPeopleFromTheOtherForest(int[] joinedTypeIds, bool expected)
    {
        var mvo = Mvo();
        var types = new[] { PanoplyUser, PanoplyContact, ResurgamUser, ResurgamContact };
        var rule = ExportRule(Has(PanoplyUser), HasNo(ResurgamUser));

        var inScope = _scoping.IsMvoInScopeForExportRule(mvo, rule,
            connections: Connections(mvo, types.Where(type => joinedTypeIds.Contains(type.Id)).ToArray()));

        Assert.That(inScope, Is.EqualTo(expected));
    }

    [Test]
    public void IsMvoInScopeForExportRule_ConnectionsNotKnownForTheObject_StopsTheObjectRatherThanGuess()
    {
        // Deciding "has no user in RESURGAM AD" from connections nobody read would bring the object into scope, or take
        // it out and delete its contact, on a guess.
        var mvo = Mvo();

        Assert.That(() => _scoping.IsMvoInScopeForExportRule(mvo, ExportRule(HasNo(ResurgamUser)), connections: null),
            Throws.InstanceOf<ScopingCriterionFailedException>());
    }

    [Test]
    public void IsMvoInScopeForExportRule_ConnectionsReadForOtherObjectsOnly_StopsTheObjectRatherThanGuess()
    {
        var mvo = Mvo();
        var someoneElse = Mvo();

        Assert.That(() => _scoping.IsMvoInScopeForExportRule(mvo, ExportRule(Has(PanoplyUser)), connections: Connections(someoneElse, PanoplyUser)),
            Throws.InstanceOf<ScopingCriterionFailedException>());
    }

    [Test]
    public void IsMvoInScopeForExportRule_ConnectionCriterionWithAValueOperator_ThrowsAsAnInvalidCriterion()
    {
        var mvo = Mvo();
        var criterion = Has(PanoplyUser);
        criterion.ComparisonType = SearchComparisonType.Equals;

        Assert.That(() => _scoping.IsMvoInScopeForExportRule(mvo, ExportRule(criterion), connections: Connections(mvo, PanoplyUser)),
            Throws.InvalidOperationException);
    }

    [Test]
    public void ExplainMvoForExportRule_ConnectionCriteria_ReportsEachOutcomeAndTheRuleOutcome()
    {
        var mvo = Mvo();

        var explanation = _scoping.ExplainMvoForExportRule(mvo, ExportRule(Has(PanoplyUser), HasNo(ResurgamUser)),
            connections: Connections(mvo, PanoplyUser, ResurgamUser));

        var criteria = explanation.Groups.Single().Criteria;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(explanation.Outcome, Is.EqualTo(ScopingRuleOutcome.OutOfScope));
            Assert.That(criteria[0].Outcome, Is.EqualTo(ScopingCriterionOutcome.Met));
            Assert.That(criteria[1].Outcome, Is.EqualTo(ScopingCriterionOutcome.NotMet));
            Assert.That(criteria[0].ConnectedObjectTypeId, Is.EqualTo(PanoplyUser.Id));
            Assert.That(criteria[0].AttributeName, Is.EqualTo("user in PANOPLY AD"));
        }
    }

    [Test]
    public void ExplainMvoForExportRule_ConnectionCriteria_AreWordedAsConnectionsNotValues()
    {
        // "user in PANOPLY AD has a value" would read as an attribute: a connection is had, or not had, and the Object
        // Type's name is an identifier, so it takes no article.
        var mvo = Mvo();

        var explanation = _scoping.ExplainMvoForExportRule(mvo, ExportRule(Has(PanoplyUser), HasNo(ResurgamUser), Has(ResurgamContact)),
            connections: Connections(mvo, PanoplyUser, ResurgamUser));

        var criteria = explanation.Groups.Single().Criteria;
        var bullets = ScopingExplanationSummariser.ToComeIntoScope(explanation);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(criteria.Select(c => c.Description), Is.EqualTo(new[]
            {
                "Has user in PANOPLY AD", "Has no user in RESURGAM AD", "Has contact in RESURGAM AD"
            }));
            Assert.That(criteria.Select(c => c.ActualDescription), Is.EqualTo(new[] { "has one", "has one", "has none" }));
            Assert.That(bullets.Select(b => b.PlainText), Is.EqualTo(new[]
            {
                "Needs no user in RESURGAM AD (currently has one)", "Needs contact in RESURGAM AD (currently has none)"
            }));
        }
    }

    [Test]
    public void ExplainMvoForExportRule_ConnectionsNotKnown_IsUndeterminedAndSaysWhy()
    {
        var mvo = Mvo();

        var explanation = _scoping.ExplainMvoForExportRule(mvo, ExportRule(Has(PanoplyUser)), connections: null);

        var criterion = explanation.Groups.Single().Criteria.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(explanation.Outcome, Is.EqualTo(ScopingRuleOutcome.Undetermined));
            Assert.That(criterion.Outcome, Is.EqualTo(ScopingCriterionOutcome.Invalid));
            Assert.That(criterion.FailureReason, Is.Not.Null.And.Contains("connected to"));
        }
    }

    [Test]
    public void MetaverseObjectConnections_From_ReadsOnlyJoinedObjects()
    {
        // A Connected System Object whose Metaverse Object was deleted keeps a stale join type (the deletion clears only
        // the reference), so only the reference says whether it is joined.
        var mvo = Mvo();
        mvo.ConnectedSystemObjects.Add(new ConnectedSystemObject { TypeId = PanoplyUser.Id, MetaverseObjectId = mvo.Id });
        mvo.ConnectedSystemObjects.Add(new ConnectedSystemObject { TypeId = ResurgamUser.Id, MetaverseObjectId = null, JoinType = ConnectedSystemObjectJoinType.Joined });

        var connections = MetaverseObjectConnections.From(mvo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connections.Has(mvo.Id, PanoplyUser.Id), Is.True);
            Assert.That(connections.Has(mvo.Id, ResurgamUser.Id), Is.False);
            Assert.That(connections.Has(Guid.NewGuid(), PanoplyUser.Id), Is.Null);
        }
    }

    [Test]
    public void MetaverseObjectConnections_From_ObsoleteObjectIsNotAConnection()
    {
        // An Obsolete object is one its Connected System no longer holds: the person has left PANOPLY, and stays joined
        // only until synchronisation removes the object. Their contact in RESURGAM must go with it.
        var mvo = Mvo();
        mvo.ConnectedSystemObjects.Add(new ConnectedSystemObject
        {
            TypeId = PanoplyUser.Id, MetaverseObjectId = mvo.Id, Status = ConnectedSystemObjectStatus.Obsolete
        });
        mvo.ConnectedSystemObjects.Add(new ConnectedSystemObject
        {
            TypeId = ResurgamContact.Id, MetaverseObjectId = mvo.Id, Status = ConnectedSystemObjectStatus.PendingProvisioning
        });

        var connections = MetaverseObjectConnections.From(mvo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connections.Has(mvo.Id, PanoplyUser.Id), Is.False);
            Assert.That(connections.Has(mvo.Id, ResurgamContact.Id), Is.True, "an object being provisioned is joined, so it is a connection");
        }
    }

    [Test]
    public void CanFailToEvaluate_ExportRuleWithConnectionCriterion_IsTrue()
    {
        // Callers that read connections only for some objects must catch the failure for these rules.
        Assert.That(ScopingEvaluationServer.CanFailToEvaluate(ExportRule(Has(PanoplyUser))), Is.True);
    }

    [Test]
    public void ConnectedObjectTypeIds_NestedGroups_NamesEveryTypeOnce()
    {
        var rule = ExportRule(Has(PanoplyUser), HasNo(ResurgamUser));
        rule.ObjectScopingCriteriaGroups[0].ChildGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.Any, Criteria = [Has(PanoplyContact), HasNo(PanoplyUser)]
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ScopingEvaluationServer.ConnectedObjectTypeIds(rule),
                Is.EquivalentTo(new[] { PanoplyUser.Id, ResurgamUser.Id, PanoplyContact.Id }));
            Assert.That(ScopingEvaluationServer.ConnectedObjectTypeIds(ExportRule()), Is.Empty);
        }
    }

    [Test]
    public void MetaverseObjectConnections_WithObject_ReplacesOneObjectsJoinsAndKeepsTheRest()
    {
        var ada = Guid.NewGuid();
        var ben = Guid.NewGuid();
        var read = new MetaverseObjectConnections([ada, ben], [(ada, PanoplyUser.Id), (ben, PanoplyUser.Id)], [PanoplyUser.Id, ResurgamUser.Id]);
        var cat = Guid.Empty;

        var previewed = read.WithObject(ada, [ResurgamUser.Id]).WithObject(cat, [PanoplyUser.Id]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(previewed.JoinedObjectTypeIds(ada), Is.EquivalentTo(new[] { ResurgamUser.Id }));
            Assert.That(previewed.Has(ben, PanoplyUser.Id), Is.True, "another object's joins are untouched");
            Assert.That(previewed.Has(cat, PanoplyUser.Id), Is.True, "a projection not yet saved is answered for");
            Assert.That(previewed.Has(cat, ResurgamUser.Id), Is.False);
            Assert.That(previewed.Has(ada, PanoplyContact.Id), Is.Null, "a type the read never covered stays unknown");
            Assert.That(read.Has(ada, PanoplyUser.Id), Is.True, "the original is not changed");
        }
    }

    private static MetaverseObject Mvo() => new() { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 1, Name = "User" } };

    private static MetaverseObjectConnections Connections(MetaverseObject mvo, params ConnectedSystemObjectType[] joinedTypes) =>
        new([mvo.Id], joinedTypes.Select(type => (mvo.Id, type.Id)));

    private static SyncRuleScopingCriteria Has(ConnectedSystemObjectType type) => new()
    {
        ConnectedObjectType = type, ConnectedObjectTypeId = type.Id, ComparisonType = SearchComparisonType.HasValue
    };

    private static SyncRuleScopingCriteria HasNo(ConnectedSystemObjectType type) => new()
    {
        ConnectedObjectType = type, ConnectedObjectTypeId = type.Id, ComparisonType = SearchComparisonType.HasNoValue
    };

    private static SyncRule ExportRule(params SyncRuleScopingCriteria[] criteria)
    {
        var rule = new SyncRule { Name = "GAL: PANOPLY people into RESURGAM", Direction = SyncRuleDirection.Export };
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All, Criteria = [.. criteria] });
        return rule;
    }
}
