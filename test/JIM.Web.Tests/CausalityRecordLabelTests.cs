// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The record is mentioned in the summary sentence, the Flow view, the Timeline view and the Graph's source
/// node, and all four read it from <see cref="CausalityPageContext"/> rather than formatting their own copy,
/// which is how they used to drift apart.
/// <para>
/// There are two formats, not one. <see cref="CausalityPageContext.RecordLabel"/> qualifies the name with the
/// external id and is used by the Timeline, which has room to be precise.
/// <see cref="CausalityPageContext.RecordName"/> is the name alone, for the summary sentence and the Flow and
/// Graph views, where the record is named in running prose or inside a fixed-width chip and a trailing
/// entryUUID is noise at best and pushes the name out of the chip at worst.
/// </para>
/// </summary>
[TestFixture]
public class CausalityRecordLabelTests
{
    private static CausalityPageContext Context(string? displayName, string? externalId) => new(
        ConnectedSystemId: 1,
        ConnectedSystemName: "Yellowstone APAC",
        RunProfileName: "Full Synchronisation",
        CsoId: CausalityTestData.CsoId,
        CsoConnectedSystemId: 1,
        CsoConnectedSystemName: "Yellowstone APAC",
        CsoDisplayName: displayName,
        CsoExternalId: externalId,
        CsoObjectTypeName: "jimGroup",
        MvoTypeName: "Group",
        MvoTypePluralName: "Groups");

    [Test]
    public void RecordLabel_NameAndExternalIdDiffer_QualifiesTheNameWithTheExternalId()
    {
        Assert.That(Context("Erin Byrne", "S8-100").RecordLabel, Is.EqualTo("Erin Byrne (S8-100)"));
    }

    [Test]
    public void RecordLabel_NameEqualsExternalId_RendersTheValueOnce()
    {
        // A record carrying none of the naming attributes resolves its name to the external id, so the
        // two slots hold the same value; showing it twice reads as two separate facts about the object.
        const string entryUuid = "1f16ccb0-1f01-1041-8be1-eb9f4cb3f25e";

        Assert.That(Context(entryUuid, entryUuid).RecordLabel, Is.EqualTo(entryUuid));
    }

    [Test]
    public void RecordLabel_CommonNameResolvedRecord_ShowsTheNameAndTheExternalId()
    {
        // The case the naming policy fixes: an LDAP group has cn but no displayName, so its name now
        // resolves to the cn instead of falling through to the entryUUID.
        Assert.That(
            Context("Project-GlobalGateway", "1f16ccb0-1f01-1041-8be1-eb9f4cb3f25e").RecordLabel,
            Is.EqualTo("Project-GlobalGateway (1f16ccb0-1f01-1041-8be1-eb9f4cb3f25e)"));
    }

    [Test]
    public void RecordLabel_NameOnly_ReturnsTheName()
    {
        Assert.That(Context("Erin Byrne", null).RecordLabel, Is.EqualTo("Erin Byrne"));
    }

    [Test]
    public void RecordLabel_ExternalIdOnly_ReturnsTheExternalId()
    {
        Assert.That(Context(null, "S8-100").RecordLabel, Is.EqualTo("S8-100"));
    }

    [Test]
    public void RecordLabel_WhitespaceOnlyValues_TreatedAsAbsent()
    {
        Assert.That(Context("   ", "  ").RecordLabel, Is.Null);
    }

    [Test]
    public void RecordLabel_NeitherPresent_ReturnsNull()
    {
        Assert.That(Context(null, null).RecordLabel, Is.Null);
    }

    [Test]
    public void Build_RecordNameEqualsExternalId_SentenceMentionsTheValueOnce()
    {
        const string entryUuid = "1f16ccb0-1f01-1041-8be1-eb9f4cb3f25e";
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), Context(entryUuid, entryUuid));
        var summary = CausalitySummaryBuilder.Build(model);

        var sentence = string.Concat(summary.Segments.Select(segment => segment switch
        {
            SummarySegment.Text text => text.Value,
            SummarySegment.Entity entity => entity.Label,
            _ => string.Empty
        }));

        Assert.That(sentence, Does.Contain($"processed the record for {entryUuid}:"));
        Assert.That(sentence, Does.Not.Contain($"{entryUuid} ({entryUuid})"));
    }

    [Test]
    public void RecordName_NameAndExternalIdDiffer_ReturnsTheNameAlone()
    {
        Assert.That(Context("Erin Byrne", "S8-100").RecordName, Is.EqualTo("Erin Byrne"));
    }

    [Test]
    public void RecordName_NameOnly_ReturnsTheName()
    {
        Assert.That(Context("Erin Byrne", null).RecordName, Is.EqualTo("Erin Byrne"));
    }

    [Test]
    public void RecordName_ExternalIdOnly_FallsBackToItRatherThanShowingNothing()
    {
        Assert.That(Context(null, "S8-100").RecordName, Is.EqualTo("S8-100"));
    }

    [Test]
    public void RecordName_WhitespaceOnlyValues_TreatedAsAbsent()
    {
        Assert.That(Context("   ", "  ").RecordName, Is.Null);
    }

    [Test]
    public void RecordName_NeitherPresent_ReturnsNull()
    {
        Assert.That(Context(null, null).RecordName, Is.Null);
    }
}
