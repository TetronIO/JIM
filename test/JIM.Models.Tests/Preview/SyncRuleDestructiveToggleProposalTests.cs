// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using NUnit.Framework;

namespace JIM.Models.Tests.Preview;

/// <summary>
/// The proposal a destructive-toggle preview is asked about (#1115): the two settings that decide what a scope exit
/// costs an object. The factory exists so every surface reading the toggles off a rule reads the same two, rather
/// than each naming them itself.
/// </summary>
[TestFixture]
public class SyncRuleDestructiveToggleProposalTests
{
    private static SyncRule ExportRule() => new()
    {
        Id = 7,
        Name = "Cross-Domain Export Users",
        OutboundDeprovisionAction = OutboundDeprovisionAction.Delete,
        InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined
    };

    [Test]
    public void FromCurrentSettings_ReadsBothToggles()
    {
        var proposal = SyncRuleDestructiveToggleProposal.FromCurrentSettings(ExportRule());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.OutboundDeprovisionAction, Is.EqualTo(OutboundDeprovisionAction.Delete));
            Assert.That(proposal.InboundOutOfScopeAction, Is.EqualTo(InboundOutOfScopeAction.RemainJoined));
        }
    }

    [Test]
    public void DescribesSameSettingsAs_RebuiltFromTheSameRule_ReportsNoChange()
    {
        var rule = ExportRule();

        var proposal = SyncRuleDestructiveToggleProposal.FromCurrentSettings(rule);

        Assert.That(SyncRuleDestructiveToggleProposal.FromCurrentSettings(rule).DescribesSameSettingsAs(proposal), Is.True);
    }

    [Test]
    public void DescribesSameSettingsAs_OneToggleChanged_ReportsChange()
    {
        var rule = ExportRule();
        var proposal = SyncRuleDestructiveToggleProposal.FromCurrentSettings(rule);

        rule.InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect;

        Assert.That(SyncRuleDestructiveToggleProposal.FromCurrentSettings(rule).DescribesSameSettingsAs(proposal), Is.False);
    }
}
