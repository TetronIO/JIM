// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A search filter built by string concatenation is the LDAP shape of an injection: a value carrying filter syntax
/// stops being something to match against and becomes part of the question. The values JIM puts into filters are
/// usually names it discovered from the directory itself, but a Connected System is a system boundary, and a
/// directory can serve whatever it likes.
/// </summary>
/// <remarks>
/// The escapes are RFC 4515 § 3: each character that means something to the filter grammar becomes a backslash
/// followed by its hex code.
/// </remarks>
[TestFixture]
public class LdapFilterEscapingTests
{
    [TestCase("*", "\\2a", TestName = "EscapeLdapFilterValue_ForAWildcard_EscapesIt")]
    [TestCase("(", "\\28", TestName = "EscapeLdapFilterValue_ForAnOpeningParenthesis_EscapesIt")]
    [TestCase(")", "\\29", TestName = "EscapeLdapFilterValue_ForAClosingParenthesis_EscapesIt")]
    [TestCase("\\", "\\5c", TestName = "EscapeLdapFilterValue_ForABackslash_EscapesIt")]
    [TestCase("\0", "\\00", TestName = "EscapeLdapFilterValue_ForANullCharacter_EscapesIt")]
    public void EscapeLdapFilterValue_ForACharacterTheFilterGrammarReserves_EscapesIt(string value, string expected)
    {
        Assert.That(LdapConnectorUtilities.EscapeLdapFilterValue(value), Is.EqualTo(expected));
    }

    [Test]
    public void EscapeLdapFilterValue_ForAnOrdinaryClassName_LeavesItAlone()
    {
        // Every legitimate RFC 4512 descriptor is letters, digits and hyphens, so escaping must be invisible in the
        // case that actually happens.
        Assert.That(LdapConnectorUtilities.EscapeLdapFilterValue("inetOrgPerson"), Is.EqualTo("inetOrgPerson"));
    }

    [Test]
    public void EscapeLdapFilterValue_EscapesTheBackslashItIntroducesOnlyOnce()
    {
        // The backslash must be replaced before anything else, or the escapes added afterwards get escaped again
        // and the value stops matching what it names.
        Assert.That(LdapConnectorUtilities.EscapeLdapFilterValue("a*b"), Is.EqualTo("a\\2ab"));
    }

    [Test]
    public void EscapeLdapFilterValue_ForAValueTryingToCloseTheFilterAndAddAnother_NeutralisesIt()
    {
        // The shape an injection would take: close the filter this value sits in, then bolt on one that matches
        // everything. Escaped, it is just an odd class name that matches nothing.
        var escaped = LdapConnectorUtilities.EscapeLdapFilterValue("person)(objectClass=*");

        Assert.That($"(objectClass={escaped})", Is.EqualTo("(objectClass=person\\29\\28objectClass=\\2a)"));
    }
}
