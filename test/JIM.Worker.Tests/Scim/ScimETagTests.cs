// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Scim.Resources;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// A resource's entity tag and the <c>If-Match</c> precondition (RFC 7644 section 3.14). SCIM versions are weak entity
/// tags, and the shipped client sends <c>meta.version</c> back exactly as it read it, so the comparison is weak.
/// </summary>
public class ScimETagTests
{
    [Test]
    public void For_Version_IsAWeakEntityTag()
    {
        Assert.That(ScimETag.For(5), Is.EqualTo("W/\"5\""));
    }

    [TestCase("W/\"5\"")]
    [TestCase("\"5\"")]
    [TestCase("*")]
    [TestCase("W/\"3\", W/\"5\"")]
    [TestCase("  W/\"5\"  ")]
    [TestCase("w/\"5\"")]
    public void IfMatch_TagForTheCurrentVersion_IsSatisfied(string header)
    {
        Assert.That(ScimETag.IfMatchSatisfied(header, 5), Is.True);
    }

    [TestCase("W/\"4\"")]
    [TestCase("W/\"50\"")]
    [TestCase("W/\"3\", \"4\"")]
    [TestCase("W/")]
    [TestCase("W/\"5")]
    [TestCase("5")]
    [TestCase("")]
    [TestCase(",")]
    public void IfMatch_NoTagForTheCurrentVersion_IsNotSatisfied(string header)
    {
        Assert.That(ScimETag.IfMatchSatisfied(header, 5), Is.False);
    }

    [Test]
    public void IfMatch_TagWrittenByFor_IsSatisfiedForThatVersionOnly()
    {
        var tag = ScimETag.For(42);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ScimETag.IfMatchSatisfied(tag, 42), Is.True);
            Assert.That(ScimETag.IfMatchSatisfied(tag, 43), Is.False);
        }
    }
}
