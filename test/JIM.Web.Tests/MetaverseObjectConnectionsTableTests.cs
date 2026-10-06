// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Web.Shared;
using NUnit.Framework;
using static JIM.Web.Tests.ConnectionExplanationTestData;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="MetaverseObjectConnectionsTable"/>: the Metaverse Object's Connections tab table
/// (#1519), one row per joined Connected System Object, rendering every
/// <see cref="ConnectedSystemObjectConnectionState"/>, the loading and empty states, the per-row
/// Preview Sync callback, and each row's expansion explaining why the connection exists (#348).
/// </summary>
[TestFixture]
public class MetaverseObjectConnectionsTableTests : JimComponentTestContext
{
    private static MetaverseObjectConnectionExplanation BuildConnection(
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

        var sortLabels = cut.FindComponents<MudBlazor.MudTableSortLabel<MetaverseObjectConnectionExplanation>>();
        Assert.That(sortLabels, Has.Count.EqualTo(6), "Connected System, Object, Role, Join, State and Last synchronised sort; the action column does not");
    }

    private IRenderedComponent<MetaverseObjectConnectionsTable> RenderExplained(MetaverseObjectConnectionExplanation connection,
        ISet<Guid>? expandedIds = null) =>
        Render<MetaverseObjectConnectionsTable>(p => p
            .Add(c => c.IsLoaded, true)
            .Add(c => c.Connections, [connection])
            .Add(c => c.EvaluatedAt, EvaluatedAt)
            .Add(c => c.ExpandedIds, expandedIds ?? new HashSet<Guid>()));

    [Test]
    public void Render_ConnectionRow_IsCollapsedByDefault()
    {
        var cut = RenderExplained(HrSystem());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find("[data-testid='jim-mvo-connection-expand']").GetAttribute("aria-expanded"), Is.EqualTo("false"));
            Assert.That(cut.FindAll("[data-testid='jim-mvo-connection-detail']"), Is.Empty);
        }
    }

    [Test]
    public void Click_Expand_ShowsHowTheConnectionJoinedAndItsScoping()
    {
        var connection = HrSystem();
        var expanded = new HashSet<Guid>();
        var cut = RenderExplained(connection, expanded);

        cut.Find("[data-testid='jim-mvo-connection-expand']").Click();

        var detail = cut.Find("[data-testid='jim-mvo-connection-detail']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(expanded, Does.Contain(connection.ConnectedSystemObjectId), "what the reader opened is kept on the state the page passed in");
            Assert.That(detail.QuerySelector("[data-testid='jim-join-description']")!.TextContent.Trim(),
                Is.EqualTo("Projected by the Synchronisation Rule \"HR Users Import\""));
            Assert.That(detail.QuerySelectorAll("[data-testid='jim-scoping-explanation']"), Has.Length.EqualTo(1));
        }
    }

    [Test]
    public void Render_ExpandedRow_SaysWhenItJoinedAndWhenScopingWasEvaluated()
    {
        var connection = HrSystem();
        var cut = RenderExplained(connection, new HashSet<Guid> { connection.ConnectedSystemObjectId });

        var detail = cut.Find("[data-testid='jim-mvo-connection-detail']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail.TextContent, Does.Contain(connection.Join.DateJoined!.Value.ToLocalTime().ToFriendlyDate()));
            Assert.That(detail.QuerySelector("[data-testid='jim-connection-evaluated-at']")!.TextContent,
                Does.Contain(EvaluatedAt.ToLocalTime().ToFriendlyDate()));
        }
    }

    /// <summary>
    /// The Activity is linked at the item that processed this object where history holds it, since that is where the
    /// join itself is recorded; otherwise at the Activity; otherwise not at all.
    /// </summary>
    [TestCase(true, true, "item")]
    [TestCase(false, true, "activity")]
    [TestCase(false, false, null)]
    public void Render_ExpandedRow_LinksToTheJoinsActivityWhereHistoryHoldsIt(bool hasItem, bool hasActivity, string? expected)
    {
        var connection = HrSystem();
        if (!hasItem)
            connection.Join.RunProfileExecutionItemId = null;
        if (!hasActivity)
            connection.Join.ActivityId = null;
        var cut = RenderExplained(connection, new HashSet<Guid> { connection.ConnectedSystemObjectId });

        var link = cut.FindAll("[data-testid='jim-join-activity-link']").SingleOrDefault()?.GetAttribute("href");
        var expectedHref = expected switch
        {
            "item" => $"/activity/item/{connection.Join.RunProfileExecutionItemId}",
            "activity" => $"/activity/{connection.Join.ActivityId}",
            _ => null
        };
        Assert.That(link, Is.EqualTo(expectedHref));
    }

    [Test]
    public void Render_ExpandedRowWithARecordedRule_LinksToTheRule()
    {
        var connection = HrSystem();
        var cut = RenderExplained(connection, new HashSet<Guid> { connection.ConnectedSystemObjectId });

        Assert.That(cut.Find("[data-testid='jim-join-rule-link']").GetAttribute("href"), Is.EqualTo("/admin/sync-rules/2"));
    }

    /// <summary>A rule since deleted is named by the record, but there is nothing left to link to.</summary>
    [Test]
    public void Render_ExpandedRowWhoseRuleWasDeleted_DoesNotLinkToIt()
    {
        var connection = HrSystem();
        connection.Join.SyncRuleId = null;
        var cut = RenderExplained(connection, new HashSet<Guid> { connection.ConnectedSystemObjectId });

        Assert.That(cut.FindAll("[data-testid='jim-join-rule-link']"), Is.Empty);
    }

    [Test]
    public void Render_ExpandedRowWithAnObjectTypeConflict_ExplainsTheConflict()
    {
        var connection = HrSystem();
        connection.Conflicts.Add(new ConnectionObjectTypeConflict
        {
            SyncRuleId = 9,
            SyncRuleName = "HR Groups Export",
            TargetObjectTypeName = "group",
            ExistingObjectTypeName = "person",
            Description = "The Synchronisation Rule \"HR Groups Export\" targets \"group\" objects in HR System, but this Metaverse Object is already represented there by a \"person\" object.",
            Scoping = InScopeWithoutCriteria(9, "HR Groups Export", SyncRuleDirection.Export)
        });
        var cut = RenderExplained(connection, new HashSet<Guid> { connection.ConnectedSystemObjectId });

        var conflict = cut.Find("[data-testid='jim-connection-conflict']");
        Assert.That(conflict.TextContent, Does.Contain(connection.Conflicts[0].Description));
    }

    [Test]
    public void Render_ExpandedRowWithNoRelevantRule_SaysThereIsNoScopingToEvaluate()
    {
        var connection = HrSystem();
        connection.Scoping.Clear();
        var cut = RenderExplained(connection, new HashSet<Guid> { connection.ConnectedSystemObjectId });

        Assert.That(cut.FindAll("[data-testid='jim-connection-no-scoping']"), Has.Count.EqualTo(1));
    }
}
