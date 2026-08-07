// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Core;
using JIM.Web;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// How attribute writability is worded and coloured for an administrator. The portal must describe the
/// state rather than name the enum value, and every state must be accounted for: an unhandled value
/// falling through to "Writable" would tell an administrator the opposite of the truth.
/// </summary>
[TestFixture]
public class AttributeWritabilityDisplayTests
{
    [TestCase(AttributeWritability.Writable, "Writable")]
    [TestCase(AttributeWritability.ReadOnly, "Read-Only")]
    [TestCase(AttributeWritability.WritableOnCreate, "Set on creation only")]
    public void GetAttributeWritabilityLabel_DescribesTheStateInAdministratorTerms(
        AttributeWritability writability, string expected)
    {
        Assert.That(Helpers.GetAttributeWritabilityLabel(writability), Is.EqualTo(expected));
    }

    [TestCase(AttributeWritability.Writable, Color.Success)]
    [TestCase(AttributeWritability.ReadOnly, Color.Warning)]
    [TestCase(AttributeWritability.WritableOnCreate, Color.Info)]
    public void GetAttributeWritabilityChipColour_DistinguishesEveryState(
        AttributeWritability writability, Color expected)
    {
        Assert.That(Helpers.GetAttributeWritabilityChipColour(writability), Is.EqualTo(expected));
    }

    [Test]
    public void GetAttributeWritabilityDescription_ExplainsEveryDefinedState()
    {
        using (Assert.EnterMultipleScope())
        {
            foreach (var writability in Enum.GetValues<AttributeWritability>())
                Assert.That(Helpers.GetAttributeWritabilityDescription(writability), Is.Not.Empty,
                    $"{writability} needs an explanation an administrator can act on");
        }
    }

    [Test]
    public void GetAttributeWritabilityDescription_ForWritableOnCreate_SaysTheValueIsNeverSentAgain()
    {
        // The whole point of the state: an administrator authoring the Attribute Flow must not expect
        // later changes to reach the Connected System.
        Assert.That(Helpers.GetAttributeWritabilityDescription(AttributeWritability.WritableOnCreate),
            Does.Contain("only when the object is created"));
    }

    [Test]
    public void GetAttributeWritabilityLabel_GivesEveryDefinedStateItsOwnWording()
    {
        // A state that falls through to another state's label would misdescribe it to an administrator.
        var labels = Array.ConvertAll(Enum.GetValues<AttributeWritability>(), Helpers.GetAttributeWritabilityLabel);

        Assert.That(labels, Is.Unique);
    }
}
