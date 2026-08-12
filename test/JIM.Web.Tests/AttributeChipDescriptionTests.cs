// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Web;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Tests for the attribute chip's tooltip text (#1199).
/// <para>
/// The Attribute Flow tab had two hand-rolled versions of this chip that disagreed: one wrapped the whole chip in a
/// tooltip naming the side, the data type and the plurality, the other tooltipped only the avatar with a generic
/// label. The richer one wins, and it is built here so the two cannot drift apart again.
/// </para>
/// </summary>
[TestFixture]
public class AttributeChipDescriptionTests
{
    [Test]
    public void DescribeAttribute_ConnectedSystemSingleValued_NamesTheSideTypeAndPlurality()
    {
        var text = Helpers.DescribeAttribute(AttributeChipKind.ConnectedSystem, AttributeDataType.Text, AttributePlurality.SingleValued);

        Assert.That(text, Is.EqualTo("Connected System: Text, Single-Valued"));
    }

    [Test]
    public void DescribeAttribute_MetaverseMultiValued_NamesTheSideTypeAndPlurality()
    {
        var text = Helpers.DescribeAttribute(AttributeChipKind.Metaverse, AttributeDataType.Reference, AttributePlurality.MultiValued);

        Assert.That(text, Is.EqualTo("Metaverse: Reference, Multi-Valued"));
    }

    [Test]
    public void DescribeAttribute_SplitsCompoundTypeNamesIntoWords()
    {
        // The enum names are compound (LongNumber), and a tooltip is prose, not an identifier.
        var text = Helpers.DescribeAttribute(AttributeChipKind.Metaverse, AttributeDataType.LongNumber, AttributePlurality.SingleValued);

        Assert.That(text, Does.Contain("Long Number"));
    }
}
