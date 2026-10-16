// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// The schema screen holds operational attributes back until asked for, and the REST API reports the same
/// classification, so the question "is this attribute operational?" is answered in one place.
/// </summary>
[TestFixture]
public class ConnectedSystemObjectTypeAttributeTagExtensionsTests
{
    [Test]
    public void IsOperational_WhenTaggedOperational_IsTrue()
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Name = "pwdFailureTime", Tags = [AttributeTags.Operational] };

        Assert.That(attribute.IsOperational(), Is.True);
    }

    [Test]
    public void IsOperational_WhenUntagged_IsFalse()
    {
        // An unclassified attribute is always shown; every Connector that tags nothing relies on this.
        var attribute = new ConnectedSystemObjectTypeAttribute { Name = "mail" };

        Assert.That(attribute.IsOperational(), Is.False);
    }

    [Test]
    public void IsOperational_WhenTaggedSomethingElse_IsFalse()
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Name = "mail", Tags = ["something-a-connector-invented"] };

        Assert.That(attribute.IsOperational(), Is.False);
    }
}
