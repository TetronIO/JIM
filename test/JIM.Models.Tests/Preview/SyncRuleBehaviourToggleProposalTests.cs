// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Preview;

/// <summary>
/// The proposal a behaviour-toggle preview is asked about (#1462): the five settings that decide whether a
/// Synchronisation Rule runs at all, which way it runs, and what it is allowed to create.
///
/// Every toggle is carried resolved, never optional, because two of them are nullable on the rule itself and a
/// null there means "off" at synchronisation time. An adapter left to interpret an absence would have to decide
/// whether the administrator meant "unchanged" or "off", and those are opposite answers about whether thousands of
/// accounts get created.
/// </summary>
[TestFixture]
public class SyncRuleBehaviourToggleProposalTests
{
    private static SyncRule ImportRule() => new()
    {
        Id = 42,
        Name = "HR Import",
        Direction = SyncRuleDirection.Import,
        Enabled = true,
        ProjectToMetaverse = true,
        ProvisionToConnectedSystem = null,
        EnforceState = true
    };

    [Test]
    public void FromCurrentSettings_ReadsEveryToggle()
    {
        var proposal = SyncRuleBehaviourToggleProposal.FromCurrentSettings(ImportRule());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.Enabled, Is.True);
            Assert.That(proposal.Direction, Is.EqualTo(SyncRuleDirection.Import));
            Assert.That(proposal.ProjectToMetaverse, Is.True);
            Assert.That(proposal.EnforceState, Is.True);
        }
    }

    [Test]
    public void FromCurrentSettings_UnsetNullableToggle_ReadsAsOff()
    {
        // Null means off at synchronisation time, so the proposal says off. Carrying the null forward would leave
        // an adapter deciding what an absence meant, and the two readings differ by every account the rule would
        // otherwise create.
        var proposal = SyncRuleBehaviourToggleProposal.FromCurrentSettings(ImportRule());

        Assert.That(proposal.ProvisionToConnectedSystem, Is.False);
    }

    [Test]
    public void DescribesSameSettingsAs_IdenticalSettingsRebuilt_ReportsNoChange()
    {
        var stored = SyncRuleBehaviourToggleProposal.FromCurrentSettings(ImportRule());
        var rebuilt = SyncRuleBehaviourToggleProposal.FromCurrentSettings(ImportRule());

        Assert.That(stored.DescribesSameSettingsAs(rebuilt), Is.True);
    }

    [Test]
    public void DescribesSameSettingsAs_Null_ReportsAChange()
    {
        Assert.That(SyncRuleBehaviourToggleProposal.FromCurrentSettings(ImportRule()).DescribesSameSettingsAs(null),
            Is.False);
    }

    [TestCase(nameof(SyncRuleBehaviourToggleProposal.Enabled))]
    [TestCase(nameof(SyncRuleBehaviourToggleProposal.Direction))]
    [TestCase(nameof(SyncRuleBehaviourToggleProposal.ProjectToMetaverse))]
    [TestCase(nameof(SyncRuleBehaviourToggleProposal.ProvisionToConnectedSystem))]
    [TestCase(nameof(SyncRuleBehaviourToggleProposal.EnforceState))]
    public void DescribesSameSettingsAs_AnyToggleFlipped_ReportsAChange(string toggle)
    {
        // Every one of the five is load-bearing; a comparison that missed one would leave a preview looking fresh
        // while describing a rule that no longer exists.
        var stored = SyncRuleBehaviourToggleProposal.FromCurrentSettings(ImportRule());
        var proposed = toggle switch
        {
            nameof(SyncRuleBehaviourToggleProposal.Enabled) => stored with { Enabled = !stored.Enabled },
            nameof(SyncRuleBehaviourToggleProposal.Direction) => stored with { Direction = SyncRuleDirection.Export },
            nameof(SyncRuleBehaviourToggleProposal.ProjectToMetaverse) => stored with { ProjectToMetaverse = !stored.ProjectToMetaverse },
            nameof(SyncRuleBehaviourToggleProposal.ProvisionToConnectedSystem) => stored with { ProvisionToConnectedSystem = !stored.ProvisionToConnectedSystem },
            _ => stored with { EnforceState = !stored.EnforceState }
        };

        Assert.That(stored.DescribesSameSettingsAs(proposed), Is.False);
    }
}
