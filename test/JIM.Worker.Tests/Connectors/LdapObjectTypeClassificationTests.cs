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

    /// <summary>
    /// Asserts a reported classification is the class-kind tag carrying the expected value.
    /// </summary>
    private static void AssertClassification(ConnectorSchemaObjectTypeTag? tag, string expectedValue)
    {
        Assert.That(tag, Is.Not.Null, "The connector must report a classification for a class kind it recognises.");
        Assert.Multiple(() =>
        {
            Assert.That(tag!.Key, Is.EqualTo(ObjectTypeTags.Keys.ClassKind));
            Assert.That(tag!.Value, Is.EqualTo(expectedValue));
        });
    }
}
