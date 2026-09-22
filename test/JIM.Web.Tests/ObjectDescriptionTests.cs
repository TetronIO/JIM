// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Tests for <see cref="ObjectDescription"/> (#1669): the single place that turns a Connected System
/// Object's or Metaverse Object's type, name and system into the wording the portal uses wherever it
/// mentions the object in running text, so a reader never needs to know what a "Connected System
/// Object" is to follow the sentence. Covers every fallback tier for both the full running-text
/// mention (name included) and the "place" sub-line used beneath a name already shown elsewhere.
/// </summary>
[TestFixture]
public class ObjectDescriptionTests
{
    // ─── ForConnectedSystemObject: full mention, including the object's name ───

    [Test]
    public void ForConnectedSystemObject_TypeAndSystemKnown_NamesTypeNameAndSystem()
    {
        var text = ObjectDescription.ForConnectedSystemObject("user", "Baseline User", "Panoply AD");

        Assert.That(text, Is.EqualTo("user Baseline User in Panoply AD"));
    }

    [Test]
    public void ForConnectedSystemObject_TypeLowerCaseAsStored_IsNotReCased()
    {
        // The schema may store a type name lower-case; it must be printed exactly as given.
        var text = ObjectDescription.ForConnectedSystemObject("person", "EMP000001", "HR CSV Source");

        Assert.That(text, Is.EqualTo("person EMP000001 in HR CSV Source"));
    }

    [Test]
    public void ForConnectedSystemObject_TypeUnknown_NamesNameAndSystem()
    {
        var text = ObjectDescription.ForConnectedSystemObject(null, "Baseline User", "Panoply AD");

        Assert.That(text, Is.EqualTo("Baseline User in Panoply AD"));
    }

    [Test]
    public void ForConnectedSystemObject_TypeKnownSystemUnknown_NamesTypeAndName()
    {
        var text = ObjectDescription.ForConnectedSystemObject("user", "Baseline User", null);

        Assert.That(text, Is.EqualTo("user Baseline User"));
    }

    [Test]
    public void ForConnectedSystemObject_TypeAndSystemUnknown_FallsBackToTheFullProductNoun()
    {
        var text = ObjectDescription.ForConnectedSystemObject(null, "Baseline User", null);

        Assert.That(text, Is.EqualTo("Connected System Object Baseline User"));
    }

    [Test]
    public void ForConnectedSystemObject_EmptyTypeAndSystem_AreTreatedAsUnknown()
    {
        var text = ObjectDescription.ForConnectedSystemObject(string.Empty, "Baseline User", "   ");

        Assert.That(text, Is.EqualTo("Connected System Object Baseline User"));
    }

    // ─── ForConnectedSystemObjectLabel: the "type: name" form for a label, not running text ───

    [Test]
    public void ForConnectedSystemObjectLabel_TypeKnown_NamesTypeThenName()
    {
        var text = ObjectDescription.ForConnectedSystemObjectLabel("user", "Baseline User");

        Assert.That(text, Is.EqualTo("user: Baseline User"));
    }

    [Test]
    public void ForConnectedSystemObjectLabel_TypeUnknown_NamesTheNameAlone()
    {
        var text = ObjectDescription.ForConnectedSystemObjectLabel(null, "Baseline User");

        Assert.That(text, Is.EqualTo("Baseline User"));
    }

    [Test]
    public void ForConnectedSystemObjectLabel_EmptyType_IsTreatedAsUnknown()
    {
        var text = ObjectDescription.ForConnectedSystemObjectLabel("   ", "Baseline User");

        Assert.That(text, Is.EqualTo("Baseline User"));
    }

    // ─── ForConnectedSystemObjectPlace: sub-line, name already shown elsewhere ───

    [Test]
    public void ForConnectedSystemObjectPlace_TypeAndSystemKnown_NamesTypeAndSystem()
    {
        var text = ObjectDescription.ForConnectedSystemObjectPlace("user", "Panoply AD");

        Assert.That(text, Is.EqualTo("user in Panoply AD"));
    }

    [Test]
    public void ForConnectedSystemObjectPlace_TypeUnknown_NamesTheSystemAlone()
    {
        var text = ObjectDescription.ForConnectedSystemObjectPlace(null, "Panoply AD");

        Assert.That(text, Is.EqualTo("in Panoply AD"));
    }

    [Test]
    public void ForConnectedSystemObjectPlace_SystemUnknown_NamesTheTypeAlone()
    {
        var text = ObjectDescription.ForConnectedSystemObjectPlace("user", null);

        Assert.That(text, Is.EqualTo("user"));
    }

    [Test]
    public void ForConnectedSystemObjectPlace_TypeAndSystemUnknown_FallsBackToTheFullProductNoun()
    {
        var text = ObjectDescription.ForConnectedSystemObjectPlace(null, null);

        Assert.That(text, Is.EqualTo("Connected System Object"));
    }

    // ─── ForConnectedSystemObjectPlace (system only): sub-line beneath a chip that already shows the type ───

    [Test]
    public void ForConnectedSystemObjectPlace_SystemNameOnly_NamesTheSystemAlone()
    {
        var text = ObjectDescription.ForConnectedSystemObjectPlace("Panoply AD");

        Assert.That(text, Is.EqualTo("in Panoply AD"));
    }

    [Test]
    public void ForConnectedSystemObjectPlace_SystemNameOnlyUnknown_FallsBackToTheFullProductNoun()
    {
        var text = ObjectDescription.ForConnectedSystemObjectPlace((string?)null);

        Assert.That(text, Is.EqualTo("Connected System Object"));
    }

    // ─── ForMetaverseObject: full mention, including the object's name ───

    [Test]
    public void ForMetaverseObject_TypeKnown_NamesTypeAndName()
    {
        var text = ObjectDescription.ForMetaverseObject("User", "Baseline User");

        Assert.That(text, Is.EqualTo("User Baseline User"));
    }

    [Test]
    public void ForMetaverseObject_TypeUnknown_FallsBackToTheFullProductNoun()
    {
        var text = ObjectDescription.ForMetaverseObject(null, "Baseline User");

        Assert.That(text, Is.EqualTo("Metaverse Object Baseline User"));
    }

    [Test]
    public void ForMetaverseObject_EmptyType_IsTreatedAsUnknown()
    {
        var text = ObjectDescription.ForMetaverseObject("   ", "Baseline User");

        Assert.That(text, Is.EqualTo("Metaverse Object Baseline User"));
    }

    // ─── ForMetaverseObjectPlace: sub-line, name already shown elsewhere ───

    [Test]
    public void ForMetaverseObjectPlace_TypeKnown_NamesTheTypeInTheMetaverse()
    {
        var text = ObjectDescription.ForMetaverseObjectPlace("User");

        Assert.That(text, Is.EqualTo("User in the Metaverse"));
    }

    [Test]
    public void ForMetaverseObjectPlace_TypeUnknown_FallsBackToTheFullProductNoun()
    {
        var text = ObjectDescription.ForMetaverseObjectPlace(null);

        Assert.That(text, Is.EqualTo("Metaverse Object"));
    }

    // ─── ForMetaverseObjectPlace (no type): sub-line beneath a chip that already shows the type ───

    [Test]
    public void ForMetaverseObjectPlace_NoArguments_AlwaysNamesTheMetaverse()
    {
        var text = ObjectDescription.ForMetaverseObjectPlace();

        Assert.That(text, Is.EqualTo("in the Metaverse"));
    }

    // ─── ChipName: the one naming rule - display name wins, then external id, never both ───

    [Test]
    public void ChipName_NameAndExternalIdPresent_ReturnsTheNameAlone()
    {
        Assert.That(ObjectDescription.ChipName("Erin Byrne", "S8-100"), Is.EqualTo("Erin Byrne"));
    }

    [Test]
    public void ChipName_NameOnly_ReturnsTheName()
    {
        Assert.That(ObjectDescription.ChipName("Erin Byrne", null), Is.EqualTo("Erin Byrne"));
    }

    [Test]
    public void ChipName_ExternalIdOnly_FallsBackToIt()
    {
        Assert.That(ObjectDescription.ChipName(null, "S8-100"), Is.EqualTo("S8-100"));
    }

    [Test]
    public void ChipName_WhitespaceOnlyValues_TreatedAsAbsent()
    {
        Assert.That(ObjectDescription.ChipName("   ", "  "), Is.Null);
    }

    [Test]
    public void ChipName_NeitherPresent_ReturnsNull()
    {
        Assert.That(ObjectDescription.ChipName(null, null), Is.Null);
    }

    // ─── ForConnectedSystemObjectChipTooltip ───

    [Test]
    public void ForConnectedSystemObjectChipTooltip_EveryValueKnown_CombinesTypeNameExternalIdAndSystem()
    {
        var text = ObjectDescription.ForConnectedSystemObjectChipTooltip(
            "person", "Sienna Quinn", "EMP000051", "HR CSV Source");

        Assert.That(text, Is.EqualTo("person: Sienna Quinn · EMP000051 · in HR CSV Source"));
    }

    [Test]
    public void ForConnectedSystemObjectChipTooltip_ExternalIdEqualsName_NeverDuplicatesIt()
    {
        // The chip already shows the value once (ChipName falls through to the external id here); the
        // tooltip must not repeat it as though it were a second, distinct fact.
        const string entryUuid = "1f16ccb0-1f01-1041-8be1-eb9f4cb3f25e";

        var text = ObjectDescription.ForConnectedSystemObjectChipTooltip("jimGroup", null, entryUuid, "Yellowstone APAC");

        Assert.That(text, Is.EqualTo($"jimGroup: {entryUuid} · in Yellowstone APAC"));
    }

    [Test]
    public void ForConnectedSystemObjectChipTooltip_NoExternalId_OmitsThatSegment()
    {
        var text = ObjectDescription.ForConnectedSystemObjectChipTooltip("person", "Sienna Quinn", null, "HR CSV Source");

        Assert.That(text, Is.EqualTo("person: Sienna Quinn · in HR CSV Source"));
    }

    [Test]
    public void ForConnectedSystemObjectChipTooltip_NoConnectedSystem_OmitsThatSegment()
    {
        var text = ObjectDescription.ForConnectedSystemObjectChipTooltip("person", "Sienna Quinn", "EMP000051", null);

        Assert.That(text, Is.EqualTo("person: Sienna Quinn · EMP000051"));
    }

    [Test]
    public void ForConnectedSystemObjectChipTooltip_NoNameOrExternalId_NamesTheTypeAlone()
    {
        var text = ObjectDescription.ForConnectedSystemObjectChipTooltip("person", null, null, "HR CSV Source");

        Assert.That(text, Is.EqualTo("person · in HR CSV Source"));
    }

    [Test]
    public void ForConnectedSystemObjectChipTooltip_NothingKnown_FallsBackToTheFullProductNoun()
    {
        var text = ObjectDescription.ForConnectedSystemObjectChipTooltip(null, null, null, null);

        Assert.That(text, Is.EqualTo("Connected System Object"));
    }

    // ─── ForMetaverseObjectChipTooltip ───

    [Test]
    public void ForMetaverseObjectChipTooltip_TypeAndNameKnown_CombinesThem()
    {
        var text = ObjectDescription.ForMetaverseObjectChipTooltip("User", "Sienna Quinn");

        Assert.That(text, Is.EqualTo("User: Sienna Quinn"));
    }

    [Test]
    public void ForMetaverseObjectChipTooltip_TypeUnknown_NamesTheNameAlone()
    {
        var text = ObjectDescription.ForMetaverseObjectChipTooltip(null, "Sienna Quinn");

        Assert.That(text, Is.EqualTo("Sienna Quinn"));
    }

    [Test]
    public void ForMetaverseObjectChipTooltip_NameUnknown_NamesTheTypeAlone()
    {
        var text = ObjectDescription.ForMetaverseObjectChipTooltip("User", null);

        Assert.That(text, Is.EqualTo("User"));
    }

    [Test]
    public void ForMetaverseObjectChipTooltip_NothingKnown_FallsBackToTheFullProductNoun()
    {
        var text = ObjectDescription.ForMetaverseObjectChipTooltip(null, null);

        Assert.That(text, Is.EqualTo("Metaverse Object"));
    }
}
