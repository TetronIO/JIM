// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Web.Causality;
using JIM.Web.Shared.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Causality Explorer's chrome (#2100): the trail back through the foci that led to the one in view, and the focus
/// header naming what is in view with its way out to the page it stands for.
/// </summary>
[TestFixture]
public class CausalityExplorerChromeTests : JimComponentTestContext
{
    private static readonly CausalityFocus First = new(CausalityFocusKind.Item, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));
    private static readonly CausalityFocus Second = new(CausalityFocusKind.MetaverseObject, Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));
    private static readonly CausalityFocus InView = new(CausalityFocusKind.ConnectedSystemObject, Guid.Parse("cccccccc-0000-0000-0000-000000000003"));

    // ─── The trail ───

    [Test]
    public void TrailBar_EachEarlierFocus_LinksBackToItAsItStood()
    {
        var trail = CausalityTrail.Parse($"{First.Token},{Second.Token}", InView);

        var cut = Render<CausalityTrailBar>(ps => ps
            .Add(c => c.Trail, trail)
            .Add(c => c.Current, InView)
            .Add(c => c.Labels, Labels()));

        var links = cut.FindAll("a.jim-causality-crumb");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(links.Select(a => a.GetAttribute("href")), Is.EqualTo(new[] { trail.CrumbHref(0), trail.CrumbHref(1) }));
            Assert.That(links.Select(a => a.QuerySelector(".jim-causality-crumb-label")!.TextContent.Trim()),
                Is.EqualTo(new[] { "group: Project-Pulse · Full Synchronisation", "User: Tina Adams" }));
        }
    }

    [Test]
    public void TrailBar_FocusInView_IsMarkedAsTheCurrentPageAndNotLinked()
    {
        var cut = Render<CausalityTrailBar>(ps => ps
            .Add(c => c.Trail, CausalityTrail.Parse(First.Token, InView))
            .Add(c => c.Current, InView)
            .Add(c => c.Labels, Labels()));

        var current = cut.Find("[aria-current='page']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(current.TagName, Is.Not.EqualTo("A"));
            Assert.That(current.TextContent, Does.Contain("person: tina.adams99"));
            Assert.That(current.QuerySelector(".jim-causality-crumb-n")!.TextContent.Trim(), Is.EqualTo("2"));
        }
    }

    [Test]
    public void TrailBar_FocusNoLongerFound_SaysSoRatherThanDroppingTheStep()
    {
        var gone = new CausalityFocus(CausalityFocusKind.Item, Guid.NewGuid());

        var cut = Render<CausalityTrailBar>(ps => ps
            .Add(c => c.Trail, CausalityTrail.Parse(gone.Token, InView))
            .Add(c => c.Current, InView)
            .Add(c => c.Labels, Labels()));

        Assert.That(cut.Find("a.jim-causality-crumb").TextContent, Does.Contain(CausalityFocusLabel.NotRetained));
    }

    // ─── The focus header ───

    [Test]
    public void FocusHeader_Item_NamesTheRunAndObjectAndOpensTheItemPage()
    {
        var cut = RenderHeader(First);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find(".jim-causality-focus-what").TextContent, Does.Contain("Full Synchronisation").And.Contain("Project-Pulse"));
            Assert.That(WayOut(cut), Is.EqualTo(("Open activity item", $"/activity/item/{First.Id}")));
        }
    }

    [Test]
    public void FocusHeader_LiveConnectedSystemObject_OpensItsPage()
    {
        var cut = RenderHeader(InView);

        Assert.That(WayOut(cut), Is.EqualTo(("Open Connected System Object", $"/admin/connected-systems/4/connector-space/{InView.Id}")));
    }

    [Test]
    public void FocusHeader_LiveMetaverseObject_OpensItsPage()
    {
        var cut = RenderHeader(Second);

        Assert.That(WayOut(cut), Is.EqualTo(("Open Metaverse Object", $"/t/users/v/{Second.Id}")));
    }

    /// <summary>
    /// A deleted object's page is gone; its way out is its deletion record, which the portal addresses by the object's
    /// own id.
    /// </summary>
    [TestCase(CausalityFocusKind.ConnectedSystemObject, "/admin/deleted-objects?cso={0}")]
    [TestCase(CausalityFocusKind.MetaverseObject, "/admin/deleted-objects?t=deleted-mvos&mvo={0}")]
    public void FocusHeader_DeletedObject_SaysSoAndOpensItsDeletionRecord(CausalityFocusKind kind, string expectedHref)
    {
        var focus = new CausalityFocus(kind, Guid.NewGuid());
        var labels = new CausalFocusLabels
        {
            Objects =
            {
                [focus.Subject!] = new CausalObjectHeader
                {
                    Subject = focus.Subject!, IsDeleted = true, DeletedAt = DateTime.UtcNow.AddDays(-3), DisplayName = "Tina Adams",
                    ObjectTypeName = "person", ConnectedSystemId = 4, ConnectedSystemName = "Glitterband EMEA"
                }
            }
        };

        var cut = RenderHeader(focus, labels);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find(".jim-causality-focus-what").TextContent, Does.Contain("deleted"));
            Assert.That(WayOut(cut), Is.EqualTo(("View deletion record", string.Format(expectedHref, focus.Id))));
        }
    }

    [Test]
    public void FocusHeader_FocusNoLongerFound_SaysSoAndOffersNoWayOut()
    {
        var cut = RenderHeader(new CausalityFocus(CausalityFocusKind.Item, Guid.NewGuid()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find(".jim-causality-focus-what").TextContent, Does.Contain("no longer retained"));
            Assert.That(cut.FindAll("a.jim-causality-way-out"), Is.Empty);
        }
    }

    [Test]
    public void FocusHeader_CopyLink_RaisesTheHostsCallback()
    {
        var copied = 0;
        var cut = Render<CausalityFocusHeader>(ps => ps
            .Add(c => c.Focus, First)
            .Add(c => c.Labels, Labels())
            .Add(c => c.OnCopyLink, () => copied++));

        cut.Find("button.jim-causality-copy-link").Click();

        Assert.That(copied, Is.EqualTo(1));
    }

    private IRenderedComponent<CausalityFocusHeader> RenderHeader(CausalityFocus focus, CausalFocusLabels? labels = null) =>
        Render<CausalityFocusHeader>(ps => ps.Add(c => c.Focus, focus).Add(c => c.Labels, labels ?? Labels()));

    private static (string Text, string? Href) WayOut(IRenderedComponent<CausalityFocusHeader> cut)
    {
        var link = cut.Find("a.jim-causality-way-out");
        return (link.TextContent.Trim(), link.GetAttribute("href"));
    }

    private static CausalFocusLabels Labels() => new()
    {
        Items =
        {
            [First.Id] = new CausalTimelineEntry
            {
                RunProfileExecutionItemId = First.Id, ObjectType = "group", DisplayName = "Project-Pulse",
                ActivityTargetName = "Full Synchronisation", ActivityTargetContext = "Yellowstone APAC",
                Executed = DateTime.UtcNow.AddMinutes(-20), ObjectChangeType = JIM.Models.Enums.ObjectChangeType.AttributeFlow
            }
        },
        Objects =
        {
            [Second.Subject!] = new CausalObjectHeader
            {
                Subject = Second.Subject!, ObjectTypeName = "User", ObjectTypePluralName = "Users", DisplayName = "Tina Adams"
            },
            [InView.Subject!] = new CausalObjectHeader
            {
                Subject = InView.Subject!, ObjectTypeName = "person", DisplayName = "tina.adams99", ConnectedSystemId = 4,
                ConnectedSystemName = "Glitterband EMEA"
            }
        }
    };
}
