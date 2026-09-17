// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Web;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Join Type chip's colour comes from one place, so a join type is the same colour on the Connector
/// Space list as on a Metaverse Object's Connections tab.
/// </summary>
[TestFixture]
public class HelpersJoinTypeColourTests
{
    [TestCase(ConnectedSystemObjectJoinType.Projected, Color.Primary)]
    [TestCase(ConnectedSystemObjectJoinType.Provisioned, Color.Secondary)]
    [TestCase(ConnectedSystemObjectJoinType.Joined, Color.Info)]
    [TestCase(ConnectedSystemObjectJoinType.NotJoined, Color.Default)]
    public void GetJoinTypeColor_EachJoinType_HasItsOwnColour(ConnectedSystemObjectJoinType joinType, Color expected)
    {
        Assert.That(Helpers.GetJoinTypeColor(joinType), Is.EqualTo(expected));
    }

    [Test]
    public void GetJoinTypeColor_EveryJoinedType_IsVisuallyDistinct()
    {
        var colours = Enum.GetValues<ConnectedSystemObjectJoinType>().Select(Helpers.GetJoinTypeColor).ToList();

        Assert.That(colours, Is.Unique, "two join types sharing a colour defeats the point of chipping the column");
    }
}
