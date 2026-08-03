// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Core;
using JIM.Models.Sync;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Components.Tests;

/// <summary>
/// Covers the decision-time deletion policy panel (#119): the Activity Run Profile Execution Item detail
/// page renders deletion rule context from the recorded policy snapshot so the explanation stays accurate
/// after an administrator edits the object type's configuration.
/// </summary>
[TestFixture]
public class DeletionPolicySnapshotViewTests : JimComponentTestContext
{
    private static MvoDeletionPolicySnapshot BuildAuthoritativeSourceSnapshot(AuthoritativeSourceTriggerMode mode)
    {
        return new MvoDeletionPolicySnapshot
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            TriggerMode = mode,
            SelectedSourceSystemIds = [1, 2],
            SelectedSourceSystemNames = ["HR (Workday)", "Active Directory"],
            GracePeriod = TimeSpan.FromDays(30),
            TriggeringSystemId = 1,
            TriggeringSystemName = "HR (Workday)",
            RemainingConnectedSourceSystemIds = [2],
            RemainingConnectedSourceSystemNames = ["Active Directory"]
        };
    }

    [Test]
    public void DeletionPolicySnapshotView_AllSourcesMode_RendersModeSourcesAndTriggeringSystem()
    {
        var snapshot = BuildAuthoritativeSourceSnapshot(AuthoritativeSourceTriggerMode.AllSourcesDisconnect);

        var cut = Render<DeletionPolicySnapshotView>(p => p
            .Add(c => c.Snapshot, snapshot)
            .Add(c => c.ObjectTypeName, "Person"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("All sources disconnect"));
            Assert.That(cut.Markup, Does.Contain("HR (Workday)"));
            Assert.That(cut.Markup, Does.Contain("Active Directory"));
            Assert.That(cut.Markup, Does.Contain("Person"));
            Assert.That(cut.Markup, Does.Contain("Recorded when this Activity ran."));
        }
    }

    [Test]
    public void DeletionPolicySnapshotView_SpecificSourcesMode_RendersTheSpecificModeLabel()
    {
        var snapshot = BuildAuthoritativeSourceSnapshot(AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect);

        var cut = Render<DeletionPolicySnapshotView>(p => p.Add(c => c.Snapshot, snapshot));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Specific source(s) disconnect"));
            Assert.That(cut.Markup, Does.Contain("any one of the selected sources"));
        }
    }

    [Test]
    public void DeletionPolicySnapshotView_WithGracePeriod_RendersTheGracePeriod()
    {
        var snapshot = BuildAuthoritativeSourceSnapshot(AuthoritativeSourceTriggerMode.AllSourcesDisconnect);

        var cut = Render<DeletionPolicySnapshotView>(p => p.Add(c => c.Snapshot, snapshot));

        Assert.That(cut.Markup, Does.Contain("30 days"));
    }

    [Test]
    public void DeletionPolicySnapshotView_WithRemainingSources_NamesTheStillConnectedSources()
    {
        var snapshot = BuildAuthoritativeSourceSnapshot(AuthoritativeSourceTriggerMode.AllSourcesDisconnect);

        var cut = Render<DeletionPolicySnapshotView>(p => p.Add(c => c.Snapshot, snapshot));

        Assert.That(cut.Markup, Does.Contain("still connected at decision time"));
    }

    [Test]
    public void DeletionPolicySnapshotView_WithNoRemainingSources_StatesNoSourcesRemainedConnected()
    {
        var snapshot = BuildAuthoritativeSourceSnapshot(AuthoritativeSourceTriggerMode.AllSourcesDisconnect);
        snapshot.RemainingConnectedSourceSystemIds = [];
        snapshot.RemainingConnectedSourceSystemNames = [];

        var cut = Render<DeletionPolicySnapshotView>(p => p.Add(c => c.Snapshot, snapshot));

        Assert.That(cut.Markup, Does.Contain("No selected sources remained connected"));
    }

    [Test]
    public void DeletionPolicySnapshotView_WhenLastConnectorDisconnectedRule_RendersTheLastConnectorExplanation()
    {
        var snapshot = new MvoDeletionPolicySnapshot
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            GracePeriod = TimeSpan.FromDays(7),
            TriggeringSystemId = 3,
            TriggeringSystemName = "Contractor Database"
        };

        var cut = Render<DeletionPolicySnapshotView>(p => p.Add(c => c.Snapshot, snapshot));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("last connector disconnects"));
            Assert.That(cut.Markup, Does.Contain("7 days"));
            Assert.That(cut.Markup, Does.Contain("Recorded when this Activity ran."));
        }
    }

    [Test]
    public void DeletionPolicySnapshotView_ManualRule_RendersTheManualExplanation()
    {
        var snapshot = new MvoDeletionPolicySnapshot
        {
            DeletionRule = MetaverseObjectDeletionRule.Manual
        };

        var cut = Render<DeletionPolicySnapshotView>(p => p.Add(c => c.Snapshot, snapshot));

        Assert.That(cut.Markup, Does.Contain("Manual deletion only."));
    }
}
