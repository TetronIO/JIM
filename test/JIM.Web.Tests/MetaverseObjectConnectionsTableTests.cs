// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="MetaverseObjectConnectionsTable"/>: the Metaverse Object's Connections tab table
/// (#1519), one row per joined Connected System Object, rendering every
/// <see cref="ConnectedSystemObjectConnectionState"/>, the loading and empty states, and the per-row
/// Preview Sync callback.
/// </summary>
[TestFixture]
public class MetaverseObjectConnectionsTableTests : JimComponentTestContext
{
    private static MetaverseObjectConnection BuildConnection(
        ConnectedSystemObjectConnectionState state,
        bool isSource = true,
        bool isTarget = true) => new()
    {
        ConnectedSystemObjectId = Guid.NewGuid(),
        DisplayName = "S8-287551",
        ConnectedSystemId = 1,
        ConnectedSystemName = "Yellowstone APAC",
        ObjectTypeName = "person",
        JoinType = ConnectedSystemObjectJoinType.Joined,
        IsSource = isSource,
        IsTarget = isTarget,
        State = state,
        LastSynchronised = DateTime.UtcNow.AddMinutes(-5)
    };

    private static readonly ConnectedSystemObjectConnectionState[] AllStates =
        Enum.GetValues<ConnectedSystemObjectConnectionState>();

    [Test]
    public void Render_Loading_ShowsProgressBarNotTable()
    {
        var cut = Render<MetaverseObjectConnectionsTable>(p => p
            .Add(c => c.IsLoading, true)
            .Add(c => c.Connections, []));

        Assert.That(cut.HasComponent<MudBlazor.MudProgressLinear>(), Is.True);
        Assert.That(cut.FindAll("[data-testid='jim-mvo-connections-table']"), Is.Empty);
    }

    [Test]
    public void Render_LoadedWithNoConnections_ShowsEmptyState()
    {
        var cut = Render<MetaverseObjectConnectionsTable>(p => p
            .Add(c => c.IsLoaded, true)
            .Add(c => c.Connections, []));

        Assert.That(cut.Markup, Does.Contain("This Metaverse Object has no Connected System Objects joined to it."));
        Assert.That(cut.FindAll("[data-testid='jim-mvo-connections-table']"), Is.Empty);
    }

    /// <summary>
    /// The provisioning states say what is outstanding, not that the object was provisioned: the Join column
    /// beside them already reads Provisioned, and "Pending export" is the term the rest of JIM uses for a
    /// queued export.
    /// </summary>
    [TestCase(ConnectedSystemObjectConnectionState.ProvisioningExportPending, "Pending export")]
    [TestCase(ConnectedSystemObjectConnectionState.ProvisioningAwaitingConfirmation, "Awaiting confirmation")]
    [TestCase(ConnectedSystemObjectConnectionState.UpdatePending, "Update pending")]
    [TestCase(ConnectedSystemObjectConnectionState.DeletePending, "Delete pending")]
    public void GetConnectionStateLabel_NamesWhatIsOutstanding(ConnectedSystemObjectConnectionState state, string expected)
    {
        Assert.That(ConnectionStateChip.GetConnectionStateLabel(state), Is.EqualTo(expected));
    }

    [TestCaseSource(nameof(AllStates))]
    public void Render_EveryConnectionState_RendersItsLabel(ConnectedSystemObjectConnectionState state)
    {
        var connection = BuildConnection(state);

        var cut = Render<MetaverseObjectConnectionsTable>(p => p
            .Add(c => c.IsLoaded, true)
            .Add(c => c.Connections, [connection]));

        var expectedLabel = ConnectionStateChip.GetConnectionStateLabel(state);
        var chip = cut.Find(".jim-connection-state-chip");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chip.TextContent.Trim(), Is.EqualTo(expectedLabel));
            Assert.That(chip.GetAttribute("data-state"), Is.EqualTo(state.ToString()));
        }
    }

    [Test]
    public void Render_UpdatePendingWithAttributeCount_ShowsSecondaryText()
    {
        var connection = BuildConnection(ConnectedSystemObjectConnectionState.UpdatePending);
        connection.PendingAttributeChangeCount = 2;

        var cut = Render<MetaverseObjectConnectionsTable>(p => p
            .Add(c => c.IsLoaded, true)
            .Add(c => c.Connections, [connection]));

        Assert.That(cut.Markup, Does.Contain("2 attributes"));
    }

    [Test]
    public void Render_ConnectionRow_RendersConnectedSystemAndObjectLinks()
    {
        var connection = BuildConnection(ConnectedSystemObjectConnectionState.InSync);

        var cut = Render<MetaverseObjectConnectionsTable>(p => p
            .Add(c => c.IsLoaded, true)
            .Add(c => c.Connections, [connection]));

        var links = cut.FindAll("a").Select(a => a.GetAttribute("href")).ToList();
        Assert.That(links, Does.Contain("/admin/connected-systems/1"));
        Assert.That(links, Does.Contain($"/admin/connected-systems/1/connector-space/{connection.ConnectedSystemObjectId}"));
    }

    [Test]
    public void Click_PreviewSyncButton_RaisesOnPreviewWithTheRow()
    {
        var connection = BuildConnection(ConnectedSystemObjectConnectionState.InSync);
        MetaverseObjectConnection? previewed = null;

        var cut = Render<MetaverseObjectConnectionsTable>(p => p
            .Add(c => c.IsLoaded, true)
            .Add(c => c.Connections, [connection])
            .Add(c => c.OnPreview, (MetaverseObjectConnection c) => previewed = c));

        cut.Find("[data-testid='jim-mvo-connection-preview']").Click();

        Assert.That(previewed, Is.SameAs(connection));
    }

    [Test]
    public void Render_RoleFlags_RendersSourceAndTargetChips()
    {
        var sourceOnly = BuildConnection(ConnectedSystemObjectConnectionState.InSync, isSource: true, isTarget: false);

        var cut = Render<MetaverseObjectConnectionsTable>(p => p
            .Add(c => c.IsLoaded, true)
            .Add(c => c.Connections, [sourceOnly]));

        Assert.That(cut.Markup, Does.Contain("Source"));
        Assert.That(cut.Markup, Does.Not.Contain(">Target<"));
    }

    [Test]
    public void ConnectionsTable_EveryDataColumn_IsSortable()
    {
        var cut = Render<MetaverseObjectConnectionsTable>(p => p
            .Add(c => c.IsLoaded, true)
            .Add(c => c.Connections, [BuildConnection(ConnectedSystemObjectConnectionState.InSync)]));

        var sortLabels = cut.FindComponents<MudBlazor.MudTableSortLabel<MetaverseObjectConnection>>();
        Assert.That(sortLabels, Has.Count.EqualTo(6), "Connected System, Object, Role, Join, State and Last synchronised sort; the action column does not");
    }
}
