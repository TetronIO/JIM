// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the site's one object chip: the shared rendering of a reference to a Connected System
/// Object, a Metaverse Object, a Connected System, a Synchronisation Rule, a Pending Export, a Deletion
/// Record or a Run Profile. It replaces both the portal's earlier CS/MV-only ObjectChip and the causality
/// panel's own CausalityEntityChip, so the behaviour worth pinning spans what both used to get right (which
/// glyph and prefix class each kind gets; a chip with no identifier does not trail a colon with nothing
/// after it) plus the glyph's own accessible title and the ShowGlyph escape hatch a Lineage card needs.
/// </summary>
[TestFixture]
public class ObjectChipTests : JimComponentTestContext
{
    [TestCase(ObjectChipKind.ConnectedSystemObject, "CSO", "cso", "Connected System Object")]
    [TestCase(ObjectChipKind.MetaverseObject, "MVO", "mvo", "Metaverse Object")]
    [TestCase(ObjectChipKind.ConnectedSystem, "CS", "sys", "Connected System")]
    [TestCase(ObjectChipKind.SynchronisationRule, "SR", "rule", "Synchronisation Rule")]
    [TestCase(ObjectChipKind.PendingExport, "PE", "other", "Pending Export")]
    [TestCase(ObjectChipKind.DeletionRecord, "DR", "other", "Deletion Record")]
    [TestCase(ObjectChipKind.RunProfile, "RP", "other", "Run Profile")]
    public void ObjectChip_EveryKind_CarriesItsOwnGlyphTextTitleAndClass(
        ObjectChipKind kind, string glyphText, string glyphClass, string fullName)
    {
        var cut = Render<ObjectChip>(p => p.Add(c => c.Kind, kind).Add(c => c.Name, "Baseline User"));

        var glyph = cut.Find(".jim-object-chip-glyph");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(glyph.TextContent.Trim(), Is.EqualTo(glyphText));
            Assert.That(glyph.GetAttribute("title"), Is.EqualTo(fullName));
            Assert.That(glyph.GetAttribute("aria-hidden"), Is.EqualTo("true"));
            Assert.That(glyph.ClassList, Does.Contain(glyphClass));
        }
    }

    [Test]
    public void ObjectChip_TypeAndName_RendersTheCombinedPrefixWithAColon()
    {
        var cut = Render<ObjectChip>(p => p
            .Add(c => c.Kind, ObjectChipKind.ConnectedSystemObject)
            .Add(c => c.TypeName, "inetOrgPerson")
            .Add(c => c.Name, "test.deprov.joindelete"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find(".jim-object-chip-type").TextContent, Is.EqualTo("inetOrgPerson: "));
            Assert.That(cut.Find(".jim-object-chip-name").TextContent, Is.EqualTo("test.deprov.joindelete"));
        }
    }

    [Test]
    public void ObjectChip_WithNoName_OmitsTheColonRatherThanTrailingIt()
    {
        // A record that has not been exported yet has no identifier to show. The colon joins the type to the
        // identifier, so with nothing to join it is punctuation pointing at nothing.
        var cut = Render<ObjectChip>(p => p
            .Add(c => c.Kind, ObjectChipKind.ConnectedSystemObject)
            .Add(c => c.TypeName, "inetOrgPerson"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find(".jim-object-chip-type").TextContent, Is.EqualTo("inetOrgPerson"));
            Assert.That(cut.FindAll(".jim-object-chip-name"), Is.Empty);
        }
    }

    [Test]
    public void ObjectChip_WithNoTypeName_RendersTheNameAloneRatherThanLeadingWithAColon()
    {
        // The inverse of the case above: a Configuration Change Preview's drill-down carries the type in its own
        // column, so its chip is the side marker and the link. Joining a name to a type that is not there would
        // lead the chip with a colon and a space.
        var cut = Render<ObjectChip>(p => p
            .Add(c => c.Kind, ObjectChipKind.MetaverseObject)
            .Add(c => c.Name, "Amelia Sullivan"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".jim-object-chip-type"), Is.Empty);
            Assert.That(cut.Find(".jim-object-chip-name").TextContent, Is.EqualTo("Amelia Sullivan"));
        }
    }

    [Test]
    public void ObjectChip_ShowGlyphFalse_RendersThePlainClassAndNoGlyph()
    {
        var cut = Render<ObjectChip>(p => p
            .Add(c => c.Kind, ObjectChipKind.ConnectedSystemObject)
            .Add(c => c.Name, "Baseline User")
            .Add(c => c.ShowGlyph, false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find(".jim-object-chip").ClassList, Does.Contain("plain"));
            Assert.That(cut.FindAll(".jim-object-chip-glyph"), Is.Empty);
        }
    }

    [Test]
    public void ObjectChip_WithHref_WrapsTheChipInTheHoverTreatmentLink()
    {
        var cut = Render<ObjectChip>(p => p
            .Add(c => c.Kind, ObjectChipKind.ConnectedSystemObject)
            .Add(c => c.TypeName, "inetOrgPerson")
            .Add(c => c.Name, "abc")
            .Add(c => c.Href, "/admin/connected-systems/2/connector-space/1"));

        var link = cut.Find("a");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.GetAttribute("href"), Is.EqualTo("/admin/connected-systems/2/connector-space/1"));
            // jim-chip-link is the hook for the whole hover treatment, the glyph recolouring included, and it
            // belongs on the link rather than the chip because it is the hover target.
            Assert.That(link.ClassList, Does.Contain("jim-chip-link"));
        }
    }

    [TestCase(ObjectChipKind.ConnectedSystemObject, "Connected System Object")]
    [TestCase(ObjectChipKind.MetaverseObject, "Metaverse Object")]
    [TestCase(ObjectChipKind.ConnectedSystem, "Connected System")]
    [TestCase(ObjectChipKind.SynchronisationRule, "Synchronisation Rule")]
    [TestCase(ObjectChipKind.PendingExport, "Pending Export")]
    [TestCase(ObjectChipKind.DeletionRecord, "Deletion Record")]
    [TestCase(ObjectChipKind.RunProfile, "Run Profile")]
    public void ObjectChip_ByDefault_TooltipNamesTheKind(ObjectChipKind kind, string expected)
    {
        var cut = Render<ObjectChip>(p => p.Add(c => c.Kind, kind).Add(c => c.TypeName, "User"));

        Assert.That(cut.FindComponent<MudTooltip>().Instance.Text, Is.EqualTo(expected));
    }

    [Test]
    public void ObjectChip_WithATooltip_UsesItInsteadOfTheDefault()
    {
        var cut = Render<ObjectChip>(p => p
            .Add(c => c.Kind, ObjectChipKind.ConnectedSystemObject)
            .Add(c => c.TypeName, "inetOrgPerson")
            .Add(c => c.Name, "Sienna Quinn")
            .Add(c => c.Tooltip, "person: Sienna Quinn · EMP000051 · in HR CSV Source"));

        Assert.That(cut.FindComponent<MudTooltip>().Instance.Text,
            Is.EqualTo("person: Sienna Quinn · EMP000051 · in HR CSV Source"));
    }

    [Test]
    public void ObjectChip_WithHref_KeepsTheTooltipOutsideTheHoverTreatmentsSubtree()
    {
        // The tooltip renders an inline-flex box of its own. Nesting it between the link and the chip would
        // sit a new box across the hover treatment's descendant selectors, so the link must stay the chip's
        // direct ancestor; this pins the nesting rather than trusting the markup to stay that way.
        var cut = Render<ObjectChip>(p => p
            .Add(c => c.Kind, ObjectChipKind.ConnectedSystemObject)
            .Add(c => c.TypeName, "inetOrgPerson")
            .Add(c => c.Name, "abc")
            .Add(c => c.Href, "/somewhere"));

        var link = cut.Find("a.jim-chip-link");
        Assert.That(link.QuerySelector(".jim-object-chip"), Is.Not.Null,
            "the chip must remain inside the link that carries the hover treatment");
    }

    [Test]
    public void ObjectChip_WithNoHref_RendersNoLink()
    {
        var cut = Render<ObjectChip>(p => p
            .Add(c => c.Kind, ObjectChipKind.ConnectedSystemObject)
            .Add(c => c.TypeName, "inetOrgPerson"));

        Assert.That(cut.FindAll("a"), Is.Empty);
    }
}
