// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using NUnit.Framework;

namespace JIM.Utilities.Tests;

[TestFixture]
public class JimVersionTests
{
    [Test]
    public void Clean_WithSourceLinkCommitSuffix_StripsIt()
    {
        Assert.That(JimVersion.Clean("0.15.0+6444a6934e1b2c3d"), Is.EqualTo("0.15.0"));
    }

    [Test]
    public void Clean_PreReleaseWithoutSuffix_Unchanged()
    {
        Assert.That(JimVersion.Clean("0.15.0-beta.1"), Is.EqualTo("0.15.0-beta.1"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Clean_Missing_Unknown(string? informationalVersion)
    {
        Assert.That(JimVersion.Clean(informationalVersion), Is.EqualTo("unknown"));
    }

    [Test]
    public void Current_Always_HasNoCommitSuffix()
    {
        Assert.That(JimVersion.Current, Does.Not.Contain("+"));
    }
}
