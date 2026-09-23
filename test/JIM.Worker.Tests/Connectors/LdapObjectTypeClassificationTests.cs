// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The LDAP connector reports each object type's class kind so JIM's schema screen can tell a structural class from
/// an auxiliary one. The two discovery paths learn it differently (RFC 4512 directories from the subschema's class
/// kind, Active Directory from objectClassCategory), and both must land on the same connector-agnostic vocabulary.
/// It also marks the classes an RFC 4512 directory keeps for itself internal, judged from the OID and the OBSOLETE
/// flag, never from the name; the live fixtures beside this one check those OIDs against real directories.
/// </summary>
[TestFixture]
public class LdapObjectTypeClassificationTests
{
    [Test]
    public void FromRfc4512Kind_ForAStructuralClass_ReturnsTheStructuralClassification()
    {
        AssertClassification(LdapObjectTypeClassification.FromRfc4512Kind(Rfc4512ObjectClassKind.Structural), ObjectTypeTags.Values.ClassKindStructural);
    }

    [Test]
    public void FromRfc4512Kind_ForAnAuxiliaryClass_ReturnsTheAuxiliaryClassification()
    {
        AssertClassification(LdapObjectTypeClassification.FromRfc4512Kind(Rfc4512ObjectClassKind.Auxiliary), ObjectTypeTags.Values.ClassKindAuxiliary);
    }

    [Test]
    public void FromRfc4512Kind_ForAnAbstractClass_ReturnsTheAbstractClassification()
    {
        AssertClassification(LdapObjectTypeClassification.FromRfc4512Kind(Rfc4512ObjectClassKind.Abstract), ObjectTypeTags.Values.ClassKindAbstract);
    }

    // objectClassCategory on an AD classSchema entry: 1 = structural, 2 = abstract, 3 = auxiliary.
    // 0 is a legacy "88 class", which predates the categories and has no equivalent in the RFC vocabulary.
    [TestCase("1", ObjectTypeTags.Values.ClassKindStructural)]
    [TestCase("2", ObjectTypeTags.Values.ClassKindAbstract)]
    [TestCase("3", ObjectTypeTags.Values.ClassKindAuxiliary)]
    public void FromActiveDirectoryObjectClassCategory_ForEachKnownCategory_ReturnsTheMatchingClassification(string category, string expectedValue)
    {
        AssertClassification(LdapObjectTypeClassification.FromActiveDirectoryObjectClassCategory(category), expectedValue);
    }

    [TestCase("0", TestName = "FromActiveDirectoryObjectClassCategory_ForALegacy88Class_ReportsNoClassification")]
    [TestCase(null, TestName = "FromActiveDirectoryObjectClassCategory_WhenTheAttributeIsAbsent_ReportsNoClassification")]
    [TestCase("", TestName = "FromActiveDirectoryObjectClassCategory_WhenTheAttributeIsEmpty_ReportsNoClassification")]
    [TestCase("not-a-number", TestName = "FromActiveDirectoryObjectClassCategory_WhenTheAttributeIsUnparseable_ReportsNoClassification")]
    public void FromActiveDirectoryObjectClassCategory_WhenTheCategoryHasNoEquivalent_ReportsNoClassification(string? category)
    {
        // Reporting nothing leaves the object type unclassified, which every consumer treats as "show it, do not
        // group it". Guessing a classification would be worse than admitting we do not know.
        Assert.That(LdapObjectTypeClassification.FromActiveDirectoryObjectClassCategory(category), Is.Null);
    }

    // Everything a stock OpenLDAP publishes under its own IANA arc (1.3.6.1.4.1.4203) is server machinery.
    [Test]
    public void FromRfc4512Definition_ForAClassUnderTheOpenLdapArc_ReportsInternal()
    {
        AssertInternal(LdapObjectTypeClassification.FromRfc4512Definition("1.3.6.1.4.1.4203.1.12.2.4.0.1", isObsolete: false));
    }

    [Test]
    public void FromRfc4512Definition_ForAnEnterpriseWhoseNumberMerelyStartsWith4203_ReportsNothing()
    {
        // Enterprise 42031 is not a child of enterprise 4203; the arc match must stop at a separator.
        Assert.That(LdapObjectTypeClassification.FromRfc4512Definition("1.3.6.1.4.1.42031.1", isObsolete: false), Is.Null);
    }

    [Test]
    public void FromRfc4512Definition_ForAnObsoleteClass_ReportsInternal()
    {
        // OBSOLETE is the directory itself declaring the class superseded, whichever arc it comes from.
        AssertInternal(LdapObjectTypeClassification.FromRfc4512Definition("2.5.6.6", isObsolete: true));
    }

    // 389 Directory Server publishes its own machinery under the Netscape arc, which it shares with inetOrgPerson
    // and its recommended user classes, so those classes are matched exactly rather than by arc. The console classes
    // from 30ns-common.ldif and 50ns-*.ldif carry the descriptive placeholder OID "<name>-oid".
    [TestCase("2.16.840.1.113730.3.2.39", TestName = "FromRfc4512Definition_ForA389DirectoryServerConfigurationClass_ReportsInternal(nsslapdConfig)")]
    [TestCase("2.16.840.1.113730.3.2.108", TestName = "FromRfc4512Definition_ForA389DirectoryServerConfigurationClass_ReportsInternal(nsDS5Replica)")]
    [TestCase("2.16.840.1.113730.3.2.101", TestName = "FromRfc4512Definition_ForA389DirectoryServerConfigurationClass_ReportsInternal(cosPointerDefinition)")]
    [TestCase("2.16.840.1.113730.3.2.104", TestName = "FromRfc4512Definition_ForA389DirectoryServerConfigurationClass_ReportsInternal(nsContainer)")]
    [TestCase("nsAdminConfig-oid", TestName = "FromRfc4512Definition_ForA389DirectoryServerConfigurationClass_ReportsInternal(nsAdminConfig)")]
    public void FromRfc4512Definition_ForA389DirectoryServerConfigurationClass_ReportsInternal(string oid)
    {
        AssertInternal(LdapObjectTypeClassification.FromRfc4512Definition(oid, isObsolete: false));
    }

    // The classes a 389 administrator is told to use for people and groups live under the same arc as the server's
    // own. Hiding any of these would be a serious regression.
    [TestCase("2.16.840.1.113730.3.2.2", TestName = "FromRfc4512Definition_ForAClassAnAdministratorManagesUnderTheNetscapeArc_ReportsNothing(inetOrgPerson)")]
    [TestCase("2.16.840.1.113730.3.2.333", TestName = "FromRfc4512Definition_ForAClassAnAdministratorManagesUnderTheNetscapeArc_ReportsNothing(nsPerson)")]
    [TestCase("2.16.840.1.113730.3.2.331", TestName = "FromRfc4512Definition_ForAClassAnAdministratorManagesUnderTheNetscapeArc_ReportsNothing(nsAccount)")]
    [TestCase("2.16.840.1.113730.3.2.334", TestName = "FromRfc4512Definition_ForAClassAnAdministratorManagesUnderTheNetscapeArc_ReportsNothing(nsOrgPerson)")]
    [TestCase("2.16.840.1.113730.3.2.329", TestName = "FromRfc4512Definition_ForAClassAnAdministratorManagesUnderTheNetscapeArc_ReportsNothing(nsMemberOf)")]
    [TestCase("2.16.840.1.113730.3.2.33", TestName = "FromRfc4512Definition_ForAClassAnAdministratorManagesUnderTheNetscapeArc_ReportsNothing(groupOfURLs)")]
    public void FromRfc4512Definition_ForAClassAnAdministratorManagesUnderTheNetscapeArc_ReportsNothing(string oid)
    {
        Assert.That(LdapObjectTypeClassification.FromRfc4512Definition(oid, isObsolete: false), Is.Null);
    }

    [Test]
    public void FromRfc4512Definition_ForAnUnlistedClassUnderTheNetscapeArc_ReportsNothing()
    {
        // The 389 list is exact, never an arc: a class it does not name shows as noise until it is judged, and is
        // never hidden by accident.
        Assert.That(LdapObjectTypeClassification.FromRfc4512Definition("2.16.840.1.113730.3.2.9999", isObsolete: false), Is.Null);
    }

    [Test]
    public void FromRfc4512Definition_ForAStandardsArcClass_ReportsNothing()
    {
        // person, from the X.500 arc.
        Assert.That(LdapObjectTypeClassification.FromRfc4512Definition("2.5.6.6", isObsolete: false), Is.Null);
    }

    [TestCase(null, TestName = "FromRfc4512Definition_WhenTheOidIsAbsent_ReportsNothing")]
    [TestCase("", TestName = "FromRfc4512Definition_WhenTheOidIsEmpty_ReportsNothing")]
    public void FromRfc4512Definition_WhenTheOidIsMissing_ReportsNothing(string? oid)
    {
        Assert.That(LdapObjectTypeClassification.FromRfc4512Definition(oid, isObsolete: false), Is.Null);
    }

    /// <summary>
    /// Asserts a reported classification is the visibility tag marking the object type internal.
    /// </summary>
    private static void AssertInternal(ConnectorSchemaObjectTypeTag? tag)
    {
        Assert.That(tag, Is.Not.Null, "The connector must classify a class the directory keeps for itself as internal.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tag!.Key, Is.EqualTo(ObjectTypeTags.Keys.Visibility));
            Assert.That(tag!.Value, Is.EqualTo(ObjectTypeTags.Values.VisibilityInternal));
        }
    }

    /// <summary>
    /// Asserts a reported classification is the class-kind tag carrying the expected value.
    /// </summary>
    private static void AssertClassification(ConnectorSchemaObjectTypeTag? tag, string expectedValue)
    {
        Assert.That(tag, Is.Not.Null, "The connector must report a classification for a class kind it recognises.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tag!.Key, Is.EqualTo(ObjectTypeTags.Keys.ClassKind));
            Assert.That(tag!.Value, Is.EqualTo(expectedValue));
        }
    }
}
