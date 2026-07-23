// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

[TestFixture]
public class BuiltInMetaverseSchemaTests
{
    [Test]
    public void Attributes_ContainsEveryConstantsBuiltInAttribute_ExactlyOnce()
    {
        var constantNames = typeof(Constants.BuiltInAttributes)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(p => (string)p.GetValue(null)!)
            .ToList();
        var catalogueNames = BuiltInMetaverseSchema.Attributes.Select(a => a.Name).ToList();

        Assert.That(catalogueNames, Is.Unique);
        Assert.That(catalogueNames, Is.EquivalentTo(constantNames));
    }

    [Test]
    public void Attributes_AllDefinitions_HaveValidShapes()
    {
        Assert.That(BuiltInMetaverseSchema.Attributes, Is.Not.Empty);
        foreach (var definition in BuiltInMetaverseSchema.Attributes)
        {
            Assert.That(definition.Name, Is.Not.Null.And.Not.Empty);
            Assert.That(definition.Type, Is.Not.EqualTo(AttributeDataType.NotSet), $"{definition.Name} has no data type");
            Assert.That(definition.ObjectTypeNames, Is.Not.Empty, $"{definition.Name} is not bound to any Metaverse Object Type");
            Assert.That(definition.ObjectTypeNames, Is.Unique, $"{definition.Name} has duplicate Metaverse Object Type bindings");
            foreach (var objectTypeName in definition.ObjectTypeNames)
            {
                Assert.That(objectTypeName, Is.EqualTo(Constants.BuiltInObjectTypes.User).Or.EqualTo(Constants.BuiltInObjectTypes.Group),
                    $"{definition.Name} is bound to unknown Metaverse Object Type '{objectTypeName}'");
            }
        }
    }

    [Test]
    public void Attributes_StandardMappings_AreValidAndUnique()
    {
        foreach (var definition in BuiltInMetaverseSchema.Attributes)
        {
            foreach (var mapping in definition.StandardMappings)
            {
                Assert.That(mapping.Standard, Is.Not.EqualTo(AttributeStandard.NotSet), $"{definition.Name} has a Standard Mapping without a standard");
                Assert.That(mapping.CounterpartName, Is.Not.Null.And.Not.Empty, $"{definition.Name} has a Standard Mapping without a counterpart name");
            }

            var keys = definition.StandardMappings.Select(m => (m.Standard, m.CounterpartName)).ToList();
            Assert.That(keys, Is.Unique, $"{definition.Name} has duplicate Standard Mappings");
        }
    }

    [Test]
    public void Attributes_SomeDefinitions_CarryStandardMappings()
    {
        // the catalogue must actually deliver the advisory metadata, not just permit it
        Assert.That(BuiltInMetaverseSchema.Attributes.Count(a => a.StandardMappings.Count > 0), Is.GreaterThan(20));
        Assert.That(BuiltInMetaverseSchema.Attributes.Count(a => a.StandardMappings.Any(m => m.Standard == AttributeStandard.Scim)), Is.GreaterThan(10));
        Assert.That(BuiltInMetaverseSchema.Attributes.Count(a => a.StandardMappings.Any(m => m.Standard == AttributeStandard.Ldap)), Is.GreaterThan(20));
    }

    [TestCase("Emails", AttributeDataType.Text, AttributePlurality.MultiValued)]
    [TestCase("Account Enabled", AttributeDataType.Boolean, AttributePlurality.SingleValued)]
    [TestCase("Nickname", AttributeDataType.Text, AttributePlurality.SingleValued)]
    [TestCase("Preferred Language", AttributeDataType.Text, AttributePlurality.SingleValued)]
    [TestCase("Locale", AttributeDataType.Text, AttributePlurality.SingleValued)]
    [TestCase("Time Zone", AttributeDataType.Text, AttributePlurality.SingleValued)]
    [TestCase("Middle Name", AttributeDataType.Text, AttributePlurality.SingleValued)]
    [TestCase("Honorific Prefix", AttributeDataType.Text, AttributePlurality.SingleValued)]
    [TestCase("Honorific Suffix", AttributeDataType.Text, AttributePlurality.SingleValued)]
    public void Attributes_ScimParityGapAttribute_IsDefinedWithExpectedShape(string name, AttributeDataType expectedType, AttributePlurality expectedPlurality)
    {
        var definition = BuiltInMetaverseSchema.Attributes.SingleOrDefault(a => a.Name == name);
        Assert.That(definition, Is.Not.Null, $"SCIM-parity gap attribute '{name}' is missing from the catalogue");
        Assert.That(definition!.Type, Is.EqualTo(expectedType));
        Assert.That(definition.Plurality, Is.EqualTo(expectedPlurality));
        Assert.That(definition.ObjectTypeNames, Does.Contain(Constants.BuiltInObjectTypes.User));
        Assert.That(definition.StandardMappings.Any(m => m.Standard == AttributeStandard.Scim), Is.True,
            $"SCIM-parity gap attribute '{name}' has no SCIM Standard Mapping");
    }

    [Test]
    public void Attributes_ScimActive_MapsToAccountEnabled()
    {
        var definition = BuiltInMetaverseSchema.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.AccountEnabled);
        Assert.That(definition.StandardMappings.Any(m => m.Standard == AttributeStandard.Scim && m.CounterpartName == "active"), Is.True);
    }

    [Test]
    public void Attributes_ScimEmails_MapsToEmails()
    {
        var definition = BuiltInMetaverseSchema.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.Emails);
        Assert.That(definition.Plurality, Is.EqualTo(AttributePlurality.MultiValued));
        Assert.That(definition.StandardMappings.Any(m => m.Standard == AttributeStandard.Scim && m.CounterpartName == "emails"), Is.True);
    }
}