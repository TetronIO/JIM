// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the object chip: the shared rendering of a reference to a Connected System Object or a Metaverse
/// Object. The behaviour worth pinning is what the duplicated markup this replaced got wrong or disagreed on:
/// which avatar and prefix class each side gets (the hover treatment in site.css keys on both), and that a chip
/// with no identifier to show does not trail a colon with nothing after it.
/// </summary>
[TestFixture]
public class ObjectChipTests : JimComponentTestContext
{
    [Test]
    public void ObjectChip_ConnectedSystemKind_CarriesTheCsAvatarAndItsPrefixClass()
    {
        var cut = Render<ObjectChip>(p => p
            .Add(c => c.Kind, ObjectChipKind.ConnectedSystem)
            .Add(c => c.TypeName, "inetOrgPerson")
            .Add(c => c.Name, "test.deprov.joindelete"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("CS"));
            // The prefix class is what the hover rule recolours against the filled chip; the two sides carry
            // different classes and site.css names both.
            Assert.That(cut.Find("b").ClassList, Does.Contain("jim-cs-chip-prefix"));
            Assert.That(cut.Find("b").TextContent, Is.EqualTo("inetOrgPerson:"));
            Assert.That(cut.Markup, Does.Contain("test.deprov.joindelete"));
        }
    }

    [Test]
    public void ObjectChip_MetaverseKind_CarriesTheMvAvatarAndItsPrefixClass()
    {
        var cut = Render<ObjectChip>(p => p
            .Add(c => c.Kind, ObjectChipKind.Metaverse)
            .Add(c => c.TypeName, "User")
            .Add(c => c.Name, "Baseline User"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("MV"));
            Assert.That(cut.Find("b").ClassList, Does.Contain("jim-mv-chip-prefix"));
            Assert.That(cut.Find("b").TextContent, Is.EqualTo("User:"));
        }
    }

    [Test]
    public void ObjectChip_WithNoName_OmitsTheColonRatherThanTrailingIt()
    {
        // A record that has not been exported yet has no identifier to show. The colon joins the type to the
        // identifier, so with nothing to join it is punctuation pointing at nothing.
        var cut = Render<ObjectChip>(p => p
            .Add(c => c.Kind, ObjectChipKind.ConnectedSystem)
            .Add(c => c.TypeName, "inetOrgPerson"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find("b").TextContent, Is.EqualTo("inetOrgPerson"));
            Assert.That(cut.Markup, Does.Not.Contain("inetOrgPerson:"));
        }
    }

    [Test]
    public void ObjectChip_WithHref_WrapsTheChipInTheHoverTreatmentLink()
    {
        var cut = Render<ObjectChip>(p => p
            .Add(c => c.Kind, ObjectChipKind.ConnectedSystem)
            .Add(c => c.TypeName, "inetOrgPerson")
            .Add(c => c.Name, "abc")
            .Add(c => c.Href, "/admin/connected-systems/2/connector-space/1"));

        var link = cut.Find("a");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.GetAttribute("href"), Is.EqualTo("/admin/connected-systems/2/connector-space/1"));
            // jim-chip-link is the hook for the whole hover treatment, the avatar recolouring included, and it
            // belongs on the link rather than the chip because it is the hover target.
            Assert.That(link.ClassList, Does.Contain("jim-chip-link"));
        }
    }

    [Test]
    public void ObjectChip_WithNoHref_RendersNoLink()
    {
        var cut = Render<ObjectChip>(p => p
            .Add(c => c.Kind, ObjectChipKind.ConnectedSystem)
            .Add(c => c.TypeName, "inetOrgPerson"));

        Assert.That(cut.FindAll("a"), Is.Empty);
    }
}
