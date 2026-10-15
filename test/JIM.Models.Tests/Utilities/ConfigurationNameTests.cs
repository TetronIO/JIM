// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Utility;
using NUnit.Framework;

namespace JIM.Models.Tests.Utilities;

/// <summary>
/// Resolving a configuration name someone typed to the name JIM stores (#2051). Configuration names ignore case, but a
/// Connected System whose own names are case-exact can hold two that differ only in case, so an exact name wins and a
/// case-insensitive match is taken only when it is the only one.
/// </summary>
[TestFixture]
public class ConfigurationNameTests
{
    [Test]
    public void Resolve_ExactName_ReturnsIt()
    {
        Assert.That(ConfigurationName.Resolve(["mail", "department"], "mail"), Is.EqualTo("mail"));
    }

    [Test]
    public void Resolve_NameInDifferentCase_ReturnsTheStoredName()
    {
        Assert.That(ConfigurationName.Resolve(["mail", "department"], "MAIL"), Is.EqualTo("mail"));
    }

    [Test]
    public void Resolve_TwoStoredNamesDifferingOnlyInCase_ExactWins()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ConfigurationName.Resolve(["mail", "Mail"], "Mail"), Is.EqualTo("Mail"));
            Assert.That(ConfigurationName.Resolve(["mail", "Mail"], "mail"), Is.EqualTo("mail"));
        }
    }

    [Test]
    public void Resolve_AmbiguousCaseInsensitiveMatch_ReturnsNull()
    {
        Assert.That(ConfigurationName.Resolve(["mail", "Mail"], "MAIL"), Is.Null);
    }

    [Test]
    public void Resolve_NoMatch_ReturnsNull()
    {
        Assert.That(ConfigurationName.Resolve(["mail"], "telephoneNumber"), Is.Null);
    }

    [Test]
    public void Resolve_SameStoredNameListedTwice_IsNotAmbiguous()
    {
        Assert.That(ConfigurationName.Resolve(["mail", "mail"], "MAIL"), Is.EqualTo("mail"));
    }
}
