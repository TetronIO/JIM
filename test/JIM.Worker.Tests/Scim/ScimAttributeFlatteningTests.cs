// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Schema;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// The SCIM-to-JIM attribute mapping: type translation, and the flattening of complex and multi-valued
/// attributes into the flat, single-valued targets Attribute Flows can be pointed at.
/// </summary>
public class ScimAttributeFlatteningTests
{
    private static ScimSchemaAttribute Simple(string name, string type, bool multiValued = false, bool required = false, string? mutability = null)
    {
        return new ScimSchemaAttribute { Name = name, Type = type, MultiValued = multiValued, Required = required, Mutability = mutability };
    }

    #region type mapping
    [TestCase(ScimAttributeTypes.String, AttributeDataType.Text)]
    [TestCase(ScimAttributeTypes.Boolean, AttributeDataType.Boolean)]
    [TestCase(ScimAttributeTypes.Integer, AttributeDataType.LongNumber)]
    [TestCase(ScimAttributeTypes.Decimal, AttributeDataType.Decimal)]
    [TestCase(ScimAttributeTypes.DateTime, AttributeDataType.DateTime)]
    [TestCase(ScimAttributeTypes.Binary, AttributeDataType.Binary)]
    [TestCase(ScimAttributeTypes.Reference, AttributeDataType.Reference)]
    public void Flatten_SimpleAttribute_MapsScimTypeToTheJimEquivalent(string scimType, AttributeDataType expected)
    {
        var flattened = ScimAttributeMapper.Flatten(Simple("anAttribute", scimType), ScimUrns.User);

        Assert.That(flattened, Has.Count.EqualTo(1));
        Assert.That(flattened[0].Type, Is.EqualTo(expected));
    }

    [Test]
    public void Flatten_IntegerAttribute_MapsToLongNumberSoSixtyFourBitValuesSurvive()
    {
        // RFC 7643 integers are not bounded to 32 bits; mapping to Number would silently overflow.
        var flattened = ScimAttributeMapper.Flatten(Simple("employeeNumber", ScimAttributeTypes.Integer), ScimUrns.User);

        Assert.That(flattened[0].Type, Is.EqualTo(AttributeDataType.LongNumber));
    }

    [Test]
    public void Flatten_TypeOmitted_DefaultsToTextRatherThanUnset()
    {
        // RFC 7643 section 7: "string" is the default when a schema omits the type.
        var flattened = ScimAttributeMapper.Flatten(new ScimSchemaAttribute { Name = "displayName" }, ScimUrns.User);

        Assert.That(flattened[0].Type, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void Flatten_TypeInUnexpectedCase_IsStillRecognised()
    {
        // Providers are inconsistent about "dateTime" versus "datetime".
        var flattened = ScimAttributeMapper.Flatten(Simple("startDate", "DATETIME"), ScimUrns.User);

        Assert.That(flattened[0].Type, Is.EqualTo(AttributeDataType.DateTime));
    }

    [Test]
    public void Flatten_UnrecognisedType_FallsBackToTextRatherThanDroppingTheAttribute()
    {
        // A vendor type JIM does not model is still importable as text; dropping it would lose data
        // silently, which is worse than an imprecise type.
        var flattened = ScimAttributeMapper.Flatten(Simple("vendorThing", "quaternion"), ScimUrns.User);

        Assert.That(flattened, Has.Count.EqualTo(1));
        Assert.That(flattened[0].Type, Is.EqualTo(AttributeDataType.Text));
    }
    #endregion

    #region plurality, writability and requiredness
    [Test]
    public void Flatten_MultiValuedSimpleAttribute_StaysMultiValued()
    {
        var flattened = ScimAttributeMapper.Flatten(Simple("tags", ScimAttributeTypes.String, multiValued: true), ScimUrns.User);

        Assert.That(flattened, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flattened[0].Name, Is.EqualTo("tags"));
            Assert.That(flattened[0].AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
        }
    }

    [TestCase(ScimMutability.ReadOnly, AttributeWritability.ReadOnly)]
    [TestCase(ScimMutability.ReadWrite, AttributeWritability.Writable)]
    [TestCase(ScimMutability.Immutable, AttributeWritability.Writable)]
    [TestCase(ScimMutability.WriteOnly, AttributeWritability.Writable)]
    public void Flatten_Mutability_DecidesWritability(string mutability, AttributeWritability expected)
    {
        var flattened = ScimAttributeMapper.Flatten(Simple("anAttribute", ScimAttributeTypes.String, mutability: mutability), ScimUrns.User);

        Assert.That(flattened[0].Writability, Is.EqualTo(expected));
    }

    [Test]
    public void Flatten_MutabilityOmitted_DefaultsToWritable()
    {
        // RFC 7643 section 7 defaults mutability to readWrite. Defaulting to ReadOnly instead would
        // hide every attribute of a terse schema from export Attribute Flows.
        var flattened = ScimAttributeMapper.Flatten(Simple("displayName", ScimAttributeTypes.String), ScimUrns.User);

        Assert.That(flattened[0].Writability, Is.EqualTo(AttributeWritability.Writable));
    }

    [Test]
    public void Flatten_RequiredSimpleAttribute_IsMarkedRequired()
    {
        var flattened = ScimAttributeMapper.Flatten(Simple("userName", ScimAttributeTypes.String, required: true), ScimUrns.User);

        Assert.That(flattened[0].Required, Is.True);
    }

    [Test]
    public void Flatten_RecordsTheOwningSchemaUrnAsTheClassName()
    {
        var flattened = ScimAttributeMapper.Flatten(Simple("userName", ScimAttributeTypes.String), ScimUrns.User);

        Assert.That(flattened[0].ClassName, Is.EqualTo(ScimUrns.User));
    }
    #endregion

    #region complex attributes
    [Test]
    public void Flatten_SingleValuedComplex_ProducesOneDottedAttributePerSubAttribute()
    {
        var name = new ScimSchemaAttribute
        {
            Name = "name",
            Type = ScimAttributeTypes.Complex,
            SubAttributes =
            [
                Simple("givenName", ScimAttributeTypes.String),
                Simple("familyName", ScimAttributeTypes.String)
            ]
        };

        var flattened = ScimAttributeMapper.Flatten(name, ScimUrns.User);

        Assert.That(flattened.Select(a => a.Name), Is.EqualTo(new[] { "name.givenName", "name.familyName" }));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flattened.Select(a => a.ScimPath), Is.EqualTo(new[] { "name.givenName", "name.familyName" }));
            Assert.That(flattened, Has.All.Property(nameof(ScimFlattenedAttribute.AttributePlurality)).EqualTo(AttributePlurality.SingleValued));
        }
    }

    [Test]
    public void Flatten_ComplexSubAttributeRequired_IsOnlyRequiredWhenTheParentIsToo()
    {
        // A required sub-attribute of an optional complex attribute is conditionally required: it binds
        // only once the parent is being sent. Marking it required outright would fail validation on
        // every object that legitimately omits the whole complex attribute.
        var name = new ScimSchemaAttribute
        {
            Name = "name",
            Type = ScimAttributeTypes.Complex,
            Required = false,
            SubAttributes = [Simple("familyName", ScimAttributeTypes.String, required: true)]
        };

        var flattened = ScimAttributeMapper.Flatten(name, ScimUrns.User);

        Assert.That(flattened[0].Required, Is.False);
    }

    [Test]
    public void Flatten_ComplexWithNoSubAttributes_IsSkippedRatherThanEmittingAnUntypedAttribute()
    {
        // A complex attribute with no sub-attributes carries no readable or writable leaf; emitting a
        // bare "manager" of type Text would invite a flow that can never work.
        var broken = new ScimSchemaAttribute { Name = "mystery", Type = ScimAttributeTypes.Complex };

        var flattened = ScimAttributeMapper.Flatten(broken, ScimUrns.User);

        Assert.That(flattened, Is.Empty);
    }
    #endregion

    #region multi-valued complex with canonical types
    private static ScimSchemaAttribute Emails()
    {
        return new ScimSchemaAttribute
        {
            Name = "emails",
            Type = ScimAttributeTypes.Complex,
            MultiValued = true,
            SubAttributes =
            [
                Simple("value", ScimAttributeTypes.String),
                new ScimSchemaAttribute { Name = "type", Type = ScimAttributeTypes.String, CanonicalValues = ["work", "home", "other"] },
                Simple("primary", ScimAttributeTypes.Boolean)
            ]
        };
    }

    [Test]
    public void Flatten_MultiValuedComplexWithCanonicalTypes_ProducesOneSlotPerCanonicalTypePlusPrimary()
    {
        var flattened = ScimAttributeMapper.Flatten(Emails(), ScimUrns.User);

        Assert.That(flattened.Select(a => a.Name),
            Is.EqualTo(new[] { "emails.work", "emails.home", "emails.other", "emails.primary" }));
    }

    [Test]
    public void Flatten_CanonicalSlots_AreSingleValuedAndTypedFromTheValueSubAttribute()
    {
        var flattened = ScimAttributeMapper.Flatten(Emails(), ScimUrns.User);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flattened, Has.All.Property(nameof(ScimFlattenedAttribute.AttributePlurality)).EqualTo(AttributePlurality.SingleValued));
            Assert.That(flattened, Has.All.Property(nameof(ScimFlattenedAttribute.Type)).EqualTo(AttributeDataType.Text));
        }
    }

    [Test]
    public void Flatten_CanonicalSlots_CarryAFilterPathThatSelectsTheMatchingEntry()
    {
        var flattened = ScimAttributeMapper.Flatten(Emails(), ScimUrns.User);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flattened.Single(a => a.Name == "emails.work").ScimPath, Is.EqualTo("emails[type eq \"work\"].value"));
            Assert.That(flattened.Single(a => a.Name == "emails.primary").ScimPath, Is.EqualTo("emails[primary eq true].value"));
        }
    }

    [Test]
    public void Flatten_CanonicalSlots_AreNeverRequired()
    {
        var emails = Emails();
        emails.Required = true;

        var flattened = ScimAttributeMapper.Flatten(emails, ScimUrns.User);

        Assert.That(flattened, Has.None.Property(nameof(ScimFlattenedAttribute.Required)).EqualTo(true));
    }

    [Test]
    public void Flatten_CanonicalComplexWithNoPrimarySubAttribute_OmitsThePrimarySlot()
    {
        var attribute = new ScimSchemaAttribute
        {
            Name = "phoneNumbers",
            Type = ScimAttributeTypes.Complex,
            MultiValued = true,
            SubAttributes =
            [
                Simple("value", ScimAttributeTypes.String),
                new ScimSchemaAttribute { Name = "type", Type = ScimAttributeTypes.String, CanonicalValues = ["work", "mobile"] }
            ]
        };

        var flattened = ScimAttributeMapper.Flatten(attribute, ScimUrns.User);

        Assert.That(flattened.Select(a => a.Name), Is.EqualTo(new[] { "phoneNumbers.work", "phoneNumbers.mobile" }));
    }

    [Test]
    public void Flatten_CanonicalComplexWithNoValueSubAttribute_FlattensEachSubAttributeUnderEachCanonicalType()
    {
        // Addresses have no "value": the address is spread across several sub-attributes, so each
        // canonical type needs a slot per sub-attribute rather than a single one.
        var addresses = new ScimSchemaAttribute
        {
            Name = "addresses",
            Type = ScimAttributeTypes.Complex,
            MultiValued = true,
            SubAttributes =
            [
                Simple("streetAddress", ScimAttributeTypes.String),
                Simple("postalCode", ScimAttributeTypes.String),
                new ScimSchemaAttribute { Name = "type", Type = ScimAttributeTypes.String, CanonicalValues = ["work", "home"] }
            ]
        };

        var flattened = ScimAttributeMapper.Flatten(addresses, ScimUrns.User);

        Assert.That(flattened.Select(a => a.Name), Is.EqualTo(new[]
        {
            "addresses.work.streetAddress", "addresses.work.postalCode",
            "addresses.home.streetAddress", "addresses.home.postalCode"
        }));
        Assert.That(flattened[0].ScimPath, Is.EqualTo("addresses[type eq \"work\"].streetAddress"));
    }

    [Test]
    public void Flatten_MultiValuedComplexWithNoCanonicalTypes_FlattensPerSubAttributeAndStaysMultiValued()
    {
        // Without canonical types there is nothing to key the entries on, so the entries' sub-attributes
        // become multi-valued attributes; the alternative is dropping the attribute entirely.
        var certificates = new ScimSchemaAttribute
        {
            Name = "x509Certificates",
            Type = ScimAttributeTypes.Complex,
            MultiValued = true,
            SubAttributes = [Simple("value", ScimAttributeTypes.Binary), Simple("display", ScimAttributeTypes.String)]
        };

        var flattened = ScimAttributeMapper.Flatten(certificates, ScimUrns.User);

        Assert.That(flattened.Select(a => a.Name), Is.EqualTo(new[] { "x509Certificates.value", "x509Certificates.display" }));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flattened, Has.All.Property(nameof(ScimFlattenedAttribute.AttributePlurality)).EqualTo(AttributePlurality.MultiValued));
            Assert.That(flattened[0].Type, Is.EqualTo(AttributeDataType.Binary));
        }
    }
    #endregion

    #region references
    [Test]
    public void Flatten_MultiValuedComplexCarryingARef_BecomesOneMultiValuedReference()
    {
        // Group members are a reference collection, not a labelled set: flattening them per canonical
        // type would produce single-valued "members.User" slots that could hold one member each.
        var members = new ScimSchemaAttribute
        {
            Name = "members",
            Type = ScimAttributeTypes.Complex,
            MultiValued = true,
            SubAttributes =
            [
                Simple("value", ScimAttributeTypes.String),
                new ScimSchemaAttribute { Name = "$ref", Type = ScimAttributeTypes.Reference, ReferenceTypes = ["User", "Group"] },
                new ScimSchemaAttribute { Name = "type", Type = ScimAttributeTypes.String, CanonicalValues = ["User", "Group"] }
            ]
        };

        var flattened = ScimAttributeMapper.Flatten(members, ScimUrns.Group);

        Assert.That(flattened, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flattened[0].Name, Is.EqualTo("members"));
            Assert.That(flattened[0].Type, Is.EqualTo(AttributeDataType.Reference));
            Assert.That(flattened[0].AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
            Assert.That(flattened[0].ScimPath, Is.EqualTo("members"));
        }
    }

    [Test]
    public void Flatten_SingleValuedComplexCarryingARef_BecomesOneSingleValuedReference()
    {
        var manager = new ScimSchemaAttribute
        {
            Name = "manager",
            Type = ScimAttributeTypes.Complex,
            SubAttributes =
            [
                Simple("value", ScimAttributeTypes.String),
                new ScimSchemaAttribute { Name = "$ref", Type = ScimAttributeTypes.Reference, ReferenceTypes = ["User"] },
                Simple("displayName", ScimAttributeTypes.String, mutability: ScimMutability.ReadOnly)
            ]
        };

        var flattened = ScimAttributeMapper.Flatten(manager, ScimUrns.EnterpriseUser);

        Assert.That(flattened, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flattened[0].Name, Is.EqualTo("manager"));
            Assert.That(flattened[0].Type, Is.EqualTo(AttributeDataType.Reference));
            Assert.That(flattened[0].AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
        }
    }

    [Test]
    public void Flatten_ReadOnlyReferenceCollection_KeepsItsReadOnlyWritability()
    {
        // User.groups is maintained by the provider; exporting to it must never be offered.
        var groups = new ScimSchemaAttribute
        {
            Name = "groups",
            Type = ScimAttributeTypes.Complex,
            MultiValued = true,
            Mutability = ScimMutability.ReadOnly,
            SubAttributes =
            [
                Simple("value", ScimAttributeTypes.String),
                new ScimSchemaAttribute { Name = "$ref", Type = ScimAttributeTypes.Reference, ReferenceTypes = ["Group"] }
            ]
        };

        var flattened = ScimAttributeMapper.Flatten(groups, ScimUrns.User);

        Assert.That(flattened[0].Writability, Is.EqualTo(AttributeWritability.ReadOnly));
    }
    #endregion

    #region extension prefixing
    [Test]
    public void Flatten_ExtensionAttribute_IsPrefixedSoItCannotCollideWithTheCoreSchema()
    {
        var flattened = ScimAttributeMapper.Flatten(
            Simple("department", ScimAttributeTypes.String), ScimUrns.EnterpriseUser, namePrefix: "enterpriseUser");

        Assert.That(flattened[0].Name, Is.EqualTo("enterpriseUser.department"));
    }

    [Test]
    public void Flatten_ExtensionAttribute_KeepsTheUrnQualifiedPathTheProviderExpects()
    {
        // The display name is prefixed for the administrator; the wire path must stay the URN-qualified
        // form, or the provider will not recognise the attribute.
        var flattened = ScimAttributeMapper.Flatten(
            Simple("department", ScimAttributeTypes.String), ScimUrns.EnterpriseUser, namePrefix: "enterpriseUser");

        Assert.That(flattened[0].ScimPath, Is.EqualTo($"{ScimUrns.EnterpriseUser}:department"));
    }

    [Test]
    public void Flatten_ExtensionComplexAttribute_PrefixesEveryFlattenedLeaf()
    {
        var manager = new ScimSchemaAttribute
        {
            Name = "manager",
            Type = ScimAttributeTypes.Complex,
            SubAttributes = [Simple("value", ScimAttributeTypes.String), new ScimSchemaAttribute { Name = "$ref", Type = ScimAttributeTypes.Reference }]
        };

        var flattened = ScimAttributeMapper.Flatten(manager, ScimUrns.EnterpriseUser, namePrefix: "enterpriseUser");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flattened[0].Name, Is.EqualTo("enterpriseUser.manager"));
            Assert.That(flattened[0].ScimPath, Is.EqualTo($"{ScimUrns.EnterpriseUser}:manager"));
        }
    }

    [Test]
    public void DerivePrefix_NamedExtensionSchema_UsesTheSchemaNameInCamelCase()
    {
        var schema = new ScimSchema { Id = ScimUrns.EnterpriseUser, Name = "EnterpriseUser" };

        Assert.That(ScimAttributeMapper.DeriveNamePrefix(schema), Is.EqualTo("enterpriseUser"));
    }

    [Test]
    public void DerivePrefix_UnnamedExtensionSchema_FallsBackToTheFinalUrnSegment()
    {
        var schema = new ScimSchema { Id = "urn:example:params:scim:schemas:extension:2.0:CustomThing" };

        Assert.That(ScimAttributeMapper.DeriveNamePrefix(schema), Is.EqualTo("customThing"));
    }
    #endregion

    #region whole-schema mapping
    [Test]
    public void FlattenSchema_SkipsAttributesWithNoName()
    {
        var schema = new ScimSchema
        {
            Id = ScimUrns.User,
            Attributes = [Simple("userName", ScimAttributeTypes.String), new ScimSchemaAttribute { Type = ScimAttributeTypes.String }]
        };

        var flattened = ScimAttributeMapper.FlattenSchema(schema);

        Assert.That(flattened.Select(a => a.Name), Is.EqualTo(new[] { "userName" }));
    }

    [Test]
    public void FlattenSchema_DuplicateAttributeNames_AreEmittedOnceOnly()
    {
        // A provider repeating an attribute in its schema must not produce two Connected System
        // Attributes with the same name; the persistence layer treats the name as the key.
        var schema = new ScimSchema
        {
            Id = ScimUrns.User,
            Attributes = [Simple("userName", ScimAttributeTypes.String), Simple("userName", ScimAttributeTypes.String)]
        };

        var flattened = ScimAttributeMapper.FlattenSchema(schema);

        Assert.That(flattened, Has.Count.EqualTo(1));
    }

    [Test]
    public void ToConnectorSchemaAttribute_CarriesNameTypePluralityAndWritabilityAcross()
    {
        var flattened = ScimAttributeMapper.Flatten(
            Simple("userName", ScimAttributeTypes.String, required: true), ScimUrns.User)[0];

        var connectorAttribute = flattened.ToConnectorSchemaAttribute();

        Assert.That(connectorAttribute, Is.InstanceOf<ConnectorSchemaAttribute>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(connectorAttribute.Name, Is.EqualTo("userName"));
            Assert.That(connectorAttribute.Type, Is.EqualTo(AttributeDataType.Text));
            Assert.That(connectorAttribute.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
            Assert.That(connectorAttribute.Required, Is.True);
            Assert.That(connectorAttribute.Writability, Is.EqualTo(AttributeWritability.Writable));
            Assert.That(connectorAttribute.ClassName, Is.EqualTo(ScimUrns.User));
        }
    }
    #endregion
}
