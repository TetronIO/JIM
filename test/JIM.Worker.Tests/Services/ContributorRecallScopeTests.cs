// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Pins the recall scope semantics the surviving-contributor re-election core
/// (<see cref="ContributorReElectionService"/>) depends on: which contributing Synchronisation Rules a
/// recall may re-elect, and which joined Connected System Objects count as survivors, per factory.
/// The system-scoped factory (#809) drives the Connected System deletion deprovisioning run, where the
/// whole system is leaving: none of its rules may be re-elected and none of its objects can survive.
/// </summary>
[TestFixture]
public class ContributorRecallScopeTests
{
    private const int DeletedSystemId = 11;
    private const int OtherSystemId = 22;

    #region ForDeletedConnectedSystem (#809)

    [Test]
    public void ForDeletedConnectedSystem_RuleBelongingToDeletedSystem_IsNotEligibleForReElection()
    {
        var scope = ContributorRecallScope.ForDeletedConnectedSystem(DeletedSystemId);

        var deletedSystemRule = new SyncRule { Id = 1, ConnectedSystemId = DeletedSystemId };

        Assert.That(scope.IsEligibleContributorRule(deletedSystemRule), Is.False,
            "every contributing Synchronisation Rule of the deleted Connected System is leaving with it and must not be re-elected");
    }

    [Test]
    public void ForDeletedConnectedSystem_RuleBelongingToAnotherSystem_IsEligibleForReElection()
    {
        var scope = ContributorRecallScope.ForDeletedConnectedSystem(DeletedSystemId);

        var otherSystemRule = new SyncRule { Id = 2, ConnectedSystemId = OtherSystemId };

        Assert.That(scope.IsEligibleContributorRule(otherSystemRule), Is.True);
    }

    [Test]
    public void ForDeletedConnectedSystem_CsoBelongingToDeletedSystem_IsNotASurvivor()
    {
        var scope = ContributorRecallScope.ForDeletedConnectedSystem(DeletedSystemId);

        var deletedSystemCso = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = DeletedSystemId };

        Assert.That(scope.IsEligibleSurvivor(deletedSystemCso), Is.False,
            "no Connected System Object of the deleted Connected System can survive its deletion");
    }

    [Test]
    public void ForDeletedConnectedSystem_CsoBelongingToAnotherSystem_IsASurvivor()
    {
        var scope = ContributorRecallScope.ForDeletedConnectedSystem(DeletedSystemId);

        var otherSystemCso = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = OtherSystemId };

        Assert.That(scope.IsEligibleSurvivor(otherSystemCso), Is.True);
    }

    #endregion

    #region ForObsoletingConnectedSystemObject (#91) characterisation

    [Test]
    public void ForObsoletingConnectedSystemObject_LeaversWholeSystemIsExcluded_AndLeaverIsNeverASurvivor()
    {
        var leaver = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = DeletedSystemId };
        var scope = ContributorRecallScope.ForObsoletingConnectedSystemObject(leaver);

        var siblingCso = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = DeletedSystemId };
        var otherSystemCso = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = OtherSystemId };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.IsEligibleContributorRule(new SyncRule { Id = 1, ConnectedSystemId = DeletedSystemId }), Is.False);
            Assert.That(scope.IsEligibleContributorRule(new SyncRule { Id = 2, ConnectedSystemId = OtherSystemId }), Is.True);
            Assert.That(scope.IsEligibleSurvivor(leaver), Is.False);
            Assert.That(scope.IsEligibleSurvivor(siblingCso), Is.True,
                "a sibling Connected System Object of the leaver's own system is still a legitimate survivor here; only the whole-system scope excludes it");
            Assert.That(scope.IsEligibleSurvivor(otherSystemCso), Is.True);
        }
    }

    #endregion

    #region ForDeletedSyncRule (#1537) characterisation

    [Test]
    public void ForDeletedSyncRule_OnlyTheDeletedRuleIsExcluded_AndEveryCsoIsASurvivor()
    {
        var scope = ContributorRecallScope.ForDeletedSyncRule(5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scope.IsEligibleContributorRule(new SyncRule { Id = 5, ConnectedSystemId = DeletedSystemId }), Is.False);
            Assert.That(scope.IsEligibleContributorRule(new SyncRule { Id = 6, ConnectedSystemId = DeletedSystemId }), Is.True);
            Assert.That(scope.IsEligibleSurvivor(new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = DeletedSystemId }), Is.True);
        }
    }

    #endregion
}
