// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Core;
using JIM.Models.Sync;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Components.Tests;

/// <summary>
/// Covers the not-deleted explanation rendered from the decision-time policy snapshot (#119): when a
/// disconnection did not trigger Metaverse Object deletion, the explanation names the recorded reason in
/// the trigger mode vocabulary rather than describing the object type's current configuration.
/// </summary>
[TestFixture]
public class DeletionNotTriggeredExplanationTests : JimComponentTestContext
{
    [Test]
    public void DeletionNotTriggeredExplanation_AllModeWithRemainingSources_ExplainsTheHold()
    {
        var snapshot = new MvoDeletionPolicySnapshot
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            TriggerMode = AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            SelectedSourceSystemIds = [1, 2],
            SelectedSourceSystemNames = ["HR (Workday)", "Active Directory"],
            TriggeringSystemId = 1,
            TriggeringSystemName = "HR (Workday)",
            RemainingConnectedSourceSystemIds = [2],
            RemainingConnectedSourceSystemNames = ["Active Directory"]
        };

        var cut = Render<DeletionNotTriggeredExplanation>(p => p
            .Add(c => c.Snapshot, snapshot)
            .Add(c => c.ObjectTypeName, "Person"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("All sources disconnect"));
            Assert.That(cut.Markup, Does.Contain("HR (Workday)"));
            Assert.That(cut.Markup, Does.Contain("1 of 2"));
            Assert.That(cut.Markup, Does.Contain("Active Directory"));
            Assert.That(cut.Markup, Does.Contain("recorded when this Activity ran"));
        }
    }

    [Test]
    public void DeletionNotTriggeredExplanation_TriggeringSystemNotASource_SaysSo()
    {
        var snapshot = new MvoDeletionPolicySnapshot
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            TriggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            SelectedSourceSystemIds = [1, 2],
            SelectedSourceSystemNames = ["HR (Workday)", "Active Directory"],
            TriggeringSystemId = 3,
            TriggeringSystemName = "Payroll"
        };

        var cut = Render<DeletionNotTriggeredExplanation>(p => p.Add(c => c.Snapshot, snapshot));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Payroll"));
            Assert.That(cut.Markup, Does.Contain("was not configured as an authoritative source"));
        }
    }

    [Test]
    public void DeletionNotTriggeredExplanation_ManualRule_ExplainsAutomaticDeletionWasDisabled()
    {
        var snapshot = new MvoDeletionPolicySnapshot
        {
            DeletionRule = MetaverseObjectDeletionRule.Manual
        };

        var cut = Render<DeletionNotTriggeredExplanation>(p => p.Add(c => c.Snapshot, snapshot));

        Assert.That(cut.Markup, Does.Contain("manual deletion only"));
    }

    [Test]
    public void DeletionNotTriggeredExplanation_WhenLastConnectorDisconnectedRule_ExplainsConnectorsRemained()
    {
        var snapshot = new MvoDeletionPolicySnapshot
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            TriggeringSystemId = 1,
            TriggeringSystemName = "HR (Workday)"
        };

        var cut = Render<DeletionNotTriggeredExplanation>(p => p.Add(c => c.Snapshot, snapshot));

        Assert.That(cut.Markup, Does.Contain("all connectors"));
    }

    [Test]
    public void DeletionNotTriggeredExplanation_WithObjectTypeName_NamesTheObjectType()
    {
        var snapshot = new MvoDeletionPolicySnapshot
        {
            DeletionRule = MetaverseObjectDeletionRule.Manual
        };

        var cut = Render<DeletionNotTriggeredExplanation>(p => p
            .Add(c => c.Snapshot, snapshot)
            .Add(c => c.ObjectTypeName, "Person"));

        Assert.That(cut.Markup, Does.Contain("Person"));
    }
}
