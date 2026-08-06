// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// An RFC 4512 directory publishes its own machinery in the same subschema as the classes an administrator manages:
/// OpenLDAP's cn=config backend and its accesslog overlay account for 27 of the 67 structural classes a stock
/// instance returns. The connector marks those internal so the schema screen can put them out of the way, and the
/// judgement is made from the class's OID rather than its name, because an OID arc is assigned by the directory's
/// vendor and a name prefix is not.
/// </summary>
/// <remarks>
/// The OIDs used here were taken from the repository's own OpenLDAP fixture
/// (<c>test/integration/docker/openldap</c>) by reading its subschemaSubentry, not from memory.
/// </remarks>
[TestFixture]
public class LdapObjectTypeVisibilityTests
{
    // Every class OpenLDAP publishes under its own IANA private enterprise arc is part of the server rather than
    // part of the directory an administrator manages. These are the three groups a stock instance returns.
    [TestCase("1.3.6.1.4.1.4203.1.12.2.4.0.1", TestName = "FromRfc4512Definition_ForTheCnConfigGlobalClass_ReportsItInternal")]
    [TestCase("1.3.6.1.4.1.4203.1.12.2.4.3.4.1", TestName = "FromRfc4512Definition_ForACnConfigOverlayClass_ReportsItInternal")]
    [TestCase("1.3.6.1.4.1.4203.666.11.5.2.5", TestName = "FromRfc4512Definition_ForAnAccesslogAuditClass_ReportsItInternal")]
    [TestCase("1.3.6.1.4.1.4203.1.4.1", TestName = "FromRfc4512Definition_ForTheRootDseClass_ReportsItInternal")]
    public void FromRfc4512Definition_ForAClassUnderTheDirectorysOwnArc_ReportsItInternal(string oid)
    {
        AssertInternal(LdapObjectTypeClassification.FromRfc4512Definition(oid, isObsolete: false));
    }

    // The classes an administrator actually manages come from the X.500, COSINE and Internet standards arcs, and a
    // customer's own schema extensions come from the customer's own arc. None of those may be hidden.
    [TestCase("2.16.840.1.113730.3.2.2", TestName = "FromRfc4512Definition_ForInetOrgPerson_ReportsNoClassification")]
    [TestCase("2.5.6.9", TestName = "FromRfc4512Definition_ForGroupOfNames_ReportsNoClassification")]
    [TestCase("1.3.6.1.1.1.2.2", TestName = "FromRfc4512Definition_ForPosixGroup_ReportsNoClassification")]
    [TestCase("0.9.2342.19200300.100.4.5", TestName = "FromRfc4512Definition_ForACosineClass_ReportsNoClassification")]
    [TestCase("1.3.6.1.4.1.99999.1.2.2", TestName = "FromRfc4512Definition_ForACustomerSchemaExtension_ReportsNoClassification")]
    public void FromRfc4512Definition_ForAClassAnAdministratorMayManage_ReportsNoClassification(string oid)
    {
        Assert.That(LdapObjectTypeClassification.FromRfc4512Definition(oid, isObsolete: false), Is.Null);
    }

    [Test]
    public void FromRfc4512Definition_ForAnObsoleteClass_ReportsItInternal()
    {
        // OBSOLETE is the directory itself saying the class is superseded. No class in the OpenLDAP fixture carries
        // it, so this rule earns nothing there; it is honoured because it is the one statement of intent the schema
        // format actually provides, and other directories do use it.
        AssertInternal(LdapObjectTypeClassification.FromRfc4512Definition("2.5.6.6", isObsolete: true));
    }

    [TestCase(null, TestName = "FromRfc4512Definition_WhenTheOidIsAbsent_ReportsNoClassification")]
    [TestCase("", TestName = "FromRfc4512Definition_WhenTheOidIsEmpty_ReportsNoClassification")]
    public void FromRfc4512Definition_WhenTheOidCannotBeRead_ReportsNoClassification(string? oid)
    {
        // Reporting nothing leaves the object type unclassified, which every consumer treats as "show it". Hiding a
        // class JIM could not identify would be the one failure mode this feature must not have.
        Assert.That(LdapObjectTypeClassification.FromRfc4512Definition(oid, isObsolete: false), Is.Null);
    }

    [Test]
    public void FromRfc4512Definition_ForAnArcThatMerelyStartsWithTheSameDigits_ReportsNoClassification()
    {
        // 1.3.6.1.4.1.42031 is a different enterprise to 1.3.6.1.4.1.4203; matching on the arc must respect the
        // separator rather than comparing the strings.
        Assert.That(LdapObjectTypeClassification.FromRfc4512Definition("1.3.6.1.4.1.42031.1.1", isObsolete: false), Is.Null);
    }

    /// <summary>
    /// Asserts a reported classification is the visibility tag marking the object type internal.
    /// </summary>
    private static void AssertInternal(ConnectorSchemaObjectTypeTag? tag)
    {
        Assert.That(tag, Is.Not.Null, "The connector must report a classification for a class it recognises as the directory's own.");
        Assert.Multiple(() =>
        {
            Assert.That(tag!.Key, Is.EqualTo(ObjectTypeTags.Keys.Visibility));
            Assert.That(tag!.Value, Is.EqualTo(ObjectTypeTags.Values.VisibilityInternal));
        });
    }
}
