// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using JIM.Models.Core;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Covers the advisory Standard Mapping hints shown in the Attribute Flow editor (#1122): which counterpart
/// names are surfaced for a Metaverse Attribute, and which attributes a Connected System attribute name
/// corresponds to. Hints never filter or disable anything, so the tests assert what is offered, never what
/// is withheld.
/// </summary>
[TestFixture]
public class StandardMappingHintsTests
{
    private static MetaverseAttributeStandardMapping Mapping(int attributeId, AttributeStandard standard, string counterpartName, string? notes = null) =>
        new() { MetaverseAttributeId = attributeId, Standard = standard, CounterpartName = counterpartName, Notes = notes };

    // Stand-ins for the seeded built-in attributes: 1 = First Name, 2 = Display Name, 3 = Email, 4 = Emails.
    private static List<MetaverseAttributeStandardMapping> SampleMappings() =>
    [
        Mapping(1, AttributeStandard.Scim, "name.givenName"),
        Mapping(1, AttributeStandard.Ldap, "givenName"),
        Mapping(2, AttributeStandard.Scim, "displayName"),
        Mapping(2, AttributeStandard.Ldap, "displayName"),
        Mapping(3, AttributeStandard.Scim, "emails", "SCIM emails is multi-valued."),
        Mapping(3, AttributeStandard.Ldap, "mail"),
        Mapping(4, AttributeStandard.Scim, "emails"),
        Mapping(4, AttributeStandard.Ldap, "mail", "LDAP mail is multi-valued.")
    ];

    [Test]
    public void Build_WithConnectedSystemStandard_ShowsOnlyThatStandardsCounterparts()
    {
        var hints = StandardMappingHints.Build(SampleMappings(), AttributeStandard.Ldap);

        var firstName = hints.ForAttribute(1);
        Assert.That(firstName, Has.Count.EqualTo(1));
        Assert.That(firstName[0].CounterpartName, Is.EqualTo("givenName"));
        Assert.That(firstName[0].StandardLabel, Is.EqualTo("LDAP/AD"));
    }

    [Test]
    public void Build_WithoutConnectedSystemStandard_ShowsEveryStandardLabelled()
    {
        var hints = StandardMappingHints.Build(SampleMappings(), AttributeStandard.NotSet);

        var email = hints.ForAttribute(3);
        Assert.That(email, Has.Count.EqualTo(2));
        Assert.That(email.Select(h => h.CounterpartName), Is.EquivalentTo(new[] { "emails", "mail" }));
        Assert.That(email.Select(h => h.StandardLabel), Is.EquivalentTo(new[] { "SCIM 2.0", "LDAP/AD" }));
    }

    [Test]
    public void Build_CounterpartSharedByTwoStandards_CollapsesToOneHint()
    {
        var hints = StandardMappingHints.Build(SampleMappings(), AttributeStandard.NotSet);

        // Display Name is "displayName" in both vocabularies; repeating the name twice would be noise.
        var displayName = hints.ForAttribute(2);
        Assert.That(displayName, Has.Count.EqualTo(1));
        Assert.That(displayName[0].CounterpartName, Is.EqualTo("displayName"));
        Assert.That(displayName[0].StandardLabel, Is.EqualTo("SCIM 2.0 · LDAP/AD"));
    }

    [Test]
    public void Build_CollapsedHintWithNotesFromBothStandards_AttributesEachNote()
    {
        var mappings = new List<MetaverseAttributeStandardMapping>
        {
            Mapping(4, AttributeStandard.Scim, "emails", "Values only."),
            Mapping(4, AttributeStandard.Ldap, "emails", "First value is primary.")
        };

        var hints = StandardMappingHints.Build(mappings, AttributeStandard.NotSet);

        Assert.That(hints.ForAttribute(4)[0].Notes, Is.EqualTo("SCIM 2.0: Values only. LDAP/AD: First value is primary."));
    }

    [Test]
    public void Build_SingleStandardHintWithNotes_KeepsTheNoteVerbatim()
    {
        var hints = StandardMappingHints.Build(SampleMappings(), AttributeStandard.Scim);

        Assert.That(hints.ForAttribute(3)[0].Notes, Is.EqualTo("SCIM emails is multi-valued."));
    }

    [Test]
    public void ForAttribute_AttributeWithNoMappings_ReturnsEmptyRatherThanFailing()
    {
        var hints = StandardMappingHints.Build(SampleMappings(), AttributeStandard.Ldap);

        Assert.That(hints.ForAttribute(99), Is.Empty);
    }

    [Test]
    public void ForAttribute_AttributeWithNoMappingForTheApplicableStandard_ReturnsEmpty()
    {
        // Nickname (id 5) is SCIM-only; against an LDAP system it simply has nothing to say.
        var mappings = new List<MetaverseAttributeStandardMapping> { Mapping(5, AttributeStandard.Scim, "nickName") };

        var hints = StandardMappingHints.Build(mappings, AttributeStandard.Ldap);

        Assert.That(hints.ForAttribute(5), Is.Empty);
        Assert.That(hints.HasHints, Is.False);
    }

    [Test]
    public void MatchesForAttributeName_KnownCounterpart_ReturnsTheCorrespondingAttribute()
    {
        var hints = StandardMappingHints.Build(SampleMappings(), AttributeStandard.Ldap);

        var matches = hints.MatchesForAttributeName("givenName");

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].MetaverseAttributeId, Is.EqualTo(1));
        Assert.That(matches[0].CounterpartName, Is.EqualTo("givenName"));
        Assert.That(matches[0].StandardLabel, Is.EqualTo("LDAP/AD"));
    }

    [Test]
    public void MatchesForAttributeName_DifferentCasing_StillMatches()
    {
        var hints = StandardMappingHints.Build(SampleMappings(), AttributeStandard.Ldap);

        Assert.That(hints.MatchesForAttributeName("GIVENNAME"), Has.Count.EqualTo(1));
    }

    [Test]
    public void MatchesForAttributeName_CounterpartSharedByTwoAttributes_ReturnsBoth()
    {
        var hints = StandardMappingHints.Build(SampleMappings(), AttributeStandard.Scim);

        // SCIM "emails" suits both the single-valued Email and the multi-valued Emails; the administrator picks.
        var matches = hints.MatchesForAttributeName("emails");

        Assert.That(matches.Select(m => m.MetaverseAttributeId), Is.EquivalentTo(new[] { 3, 4 }));
    }

    [Test]
    public void MatchesForAttributeName_UnknownName_ReturnsEmpty()
    {
        var hints = StandardMappingHints.Build(SampleMappings(), AttributeStandard.Ldap);

        Assert.That(hints.MatchesForAttributeName("costCentre"), Is.Empty);
    }

    [Test]
    public void MatchesForAttributeName_NullOrWhitespace_ReturnsEmpty()
    {
        var hints = StandardMappingHints.Build(SampleMappings(), AttributeStandard.Ldap);

        Assert.That(hints.MatchesForAttributeName(null), Is.Empty);
        Assert.That(hints.MatchesForAttributeName("   "), Is.Empty);
    }

    [Test]
    public void Build_NoMappings_ProducesEmptyHints()
    {
        var hints = StandardMappingHints.Build([], AttributeStandard.Ldap);

        Assert.That(hints.HasHints, Is.False);
        Assert.That(hints.ForAttribute(1), Is.Empty);
        Assert.That(hints.MatchesForAttributeName("givenName"), Is.Empty);
    }

    [Test]
    public void Empty_BehavesLikeHintsBuiltFromNothing()
    {
        Assert.That(StandardMappingHints.Empty.HasHints, Is.False);
        Assert.That(StandardMappingHints.Empty.ForAttribute(1), Is.Empty);
        Assert.That(StandardMappingHints.Empty.MatchesForAttributeName("givenName"), Is.Empty);
    }

    [Test]
    public void Build_MappingsAreOrderedByStandardThenName()
    {
        var mappings = new List<MetaverseAttributeStandardMapping>
        {
            Mapping(1, AttributeStandard.Ldap, "sn"),
            Mapping(1, AttributeStandard.Scim, "name.familyName")
        };

        var hints = StandardMappingHints.Build(mappings, AttributeStandard.NotSet);

        // SCIM before LDAP, matching the order the attribute editor lists them in.
        Assert.That(hints.ForAttribute(1).Select(h => h.CounterpartName), Is.EqualTo(new[] { "name.familyName", "sn" }));
    }

    [Test]
    public void StandardLabel_UsesThePortalsWording()
    {
        Assert.That(StandardMappingHints.StandardLabel(AttributeStandard.Scim), Is.EqualTo("SCIM 2.0"));
        Assert.That(StandardMappingHints.StandardLabel(AttributeStandard.Ldap), Is.EqualTo("LDAP/AD"));
        Assert.That(StandardMappingHints.StandardLabel(AttributeStandard.Jim), Is.EqualTo("JIM"));
    }
}
