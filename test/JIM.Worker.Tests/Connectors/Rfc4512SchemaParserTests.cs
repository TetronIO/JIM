using JIM.Connectors.LDAP;
using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class Rfc4512SchemaParserTests
{
    #region ParseObjectClassDescription

    [Test]
    public void ParseObjectClass_StructuralWithMustAndMay_ParsesCorrectly()
    {
        var definition = "( 2.5.6.6 NAME 'person' DESC 'RFC 4519: a human being' SUP top STRUCTURAL MUST ( sn $ cn ) MAY ( userPassword $ telephoneNumber $ seeAlso $ description ) )";
        var result = Rfc4512SchemaParser.ParseObjectClassDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("person"));
        Assert.That(result.Description, Is.EqualTo("RFC 4519: a human being"));
        Assert.That(result.Kind, Is.EqualTo(Rfc4512ObjectClassKind.Structural));
        Assert.That(result.SuperiorName, Is.EqualTo("top"));
        Assert.That(result.MustAttributes, Is.EquivalentTo(new[] { "sn", "cn" }));
        Assert.That(result.MayAttributes, Is.EquivalentTo(new[] { "userPassword", "telephoneNumber", "seeAlso", "description" }));
    }

    [Test]
    public void ParseObjectClass_AuxiliaryClass_ParsesKindCorrectly()
    {
        var definition = "( 2.16.840.1.113730.3.2.33 NAME 'groupOfURLs' SUP top AUXILIARY MUST cn MAY ( memberURL $ businessCategory $ description $ o $ ou $ owner $ seeAlso ) )";
        var result = Rfc4512SchemaParser.ParseObjectClassDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("groupOfURLs"));
        Assert.That(result.Kind, Is.EqualTo(Rfc4512ObjectClassKind.Auxiliary));
        Assert.That(result.MustAttributes, Is.EquivalentTo(new[] { "cn" }));
    }

    [Test]
    public void ParseObjectClass_AbstractClass_ParsesKindCorrectly()
    {
        var definition = "( 2.5.6.0 NAME 'top' ABSTRACT MUST objectClass )";
        var result = Rfc4512SchemaParser.ParseObjectClassDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("top"));
        Assert.That(result.Kind, Is.EqualTo(Rfc4512ObjectClassKind.Abstract));
        Assert.That(result.SuperiorName, Is.Null);
        Assert.That(result.MustAttributes, Is.EquivalentTo(new[] { "objectClass" }));
        Assert.That(result.MayAttributes, Is.Empty);
    }

    [Test]
    public void ParseObjectClass_NoDescription_DescriptionIsNull()
    {
        var definition = "( 2.5.6.0 NAME 'top' ABSTRACT MUST objectClass )";
        var result = Rfc4512SchemaParser.ParseObjectClassDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Description, Is.Null);
    }

    [Test]
    public void ParseObjectClass_InetOrgPerson_ParsesCorrectly()
    {
        var definition = "( 2.16.840.1.113730.3.2.2 NAME 'inetOrgPerson' DESC 'RFC 2798: Internet Organizational Person' SUP organizationalPerson STRUCTURAL MAY ( audio $ businessCategory $ carLicense $ departmentNumber $ displayName $ employeeNumber $ employeeType $ givenName $ homePhone $ homePostalAddress $ initials $ jpegPhoto $ labeledURI $ mail $ manager $ mobile $ o $ pager $ photo $ roomNumber $ secretary $ uid $ userCertificate $ x500uniqueIdentifier $ preferredLanguage $ userSMIMECertificate $ userPKCS12 ) )";
        var result = Rfc4512SchemaParser.ParseObjectClassDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("inetOrgPerson"));
        Assert.That(result.SuperiorName, Is.EqualTo("organizationalPerson"));
        Assert.That(result.Kind, Is.EqualTo(Rfc4512ObjectClassKind.Structural));
        Assert.That(result.MustAttributes, Is.Empty);
        Assert.That(result.MayAttributes, Contains.Item("mail"));
        Assert.That(result.MayAttributes, Contains.Item("uid"));
        Assert.That(result.MayAttributes, Contains.Item("manager"));
    }

    [Test]
    public void ParseObjectClass_GroupOfNames_ParsesCorrectly()
    {
        var definition = "( 2.5.6.9 NAME 'groupOfNames' DESC 'RFC 4519: a group of names (DNs)' SUP top STRUCTURAL MUST ( member $ cn ) MAY ( businessCategory $ description $ o $ ou $ owner $ seeAlso ) )";
        var result = Rfc4512SchemaParser.ParseObjectClassDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("groupOfNames"));
        Assert.That(result.MustAttributes, Is.EquivalentTo(new[] { "member", "cn" }));
        Assert.That(result.MayAttributes, Contains.Item("description"));
    }

    [Test]
    public void ParseObjectClass_MultipleNames_UsesFirstName()
    {
        // Some directories give multiple names: NAME ( 'sn' 'surname' )
        var definition = "( 2.5.6.6 NAME ( 'person' 'PERSON' ) SUP top STRUCTURAL MUST ( sn $ cn ) )";
        var result = Rfc4512SchemaParser.ParseObjectClassDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("person"));
    }

    [Test]
    public void ParseObjectClass_NoMustOrMay_ReturnsEmptyLists()
    {
        var definition = "( 1.3.6.1.4.1.4203.666.11.1 NAME 'testClass' SUP top STRUCTURAL )";
        var result = Rfc4512SchemaParser.ParseObjectClassDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.MustAttributes, Is.Empty);
        Assert.That(result.MayAttributes, Is.Empty);
    }

    #endregion

    #region ParseAttributeTypeDescription

    [Test]
    public void ParseAttributeType_SingleValuedWithSyntax_ParsesCorrectly()
    {
        var definition = "( 2.5.4.4 NAME 'sn' DESC 'RFC 4519: last name(s) for which the entity is known by' SUP name EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15{64} SINGLE-VALUE )";
        var result = Rfc4512SchemaParser.ParseAttributeTypeDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("sn"));
        Assert.That(result.Description, Is.EqualTo("RFC 4519: last name(s) for which the entity is known by"));
        Assert.That(result.SyntaxOid, Is.EqualTo("1.3.6.1.4.1.1466.115.121.1.15"));
        Assert.That(result.IsSingleValued, Is.True);
    }

    [Test]
    public void ParseAttributeType_MultiValued_DefaultsToMultiValued()
    {
        var definition = "( 2.5.4.31 NAME 'member' SUP distinguishedName EQUALITY distinguishedNameMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.12 )";
        var result = Rfc4512SchemaParser.ParseAttributeTypeDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("member"));
        Assert.That(result.IsSingleValued, Is.False);
        Assert.That(result.SyntaxOid, Is.EqualTo("1.3.6.1.4.1.1466.115.121.1.12"));
    }

    [Test]
    public void ParseAttributeType_SyntaxWithLengthConstraint_StripsLength()
    {
        var definition = "( 2.5.4.3 NAME 'cn' SUP name SYNTAX 1.3.6.1.4.1.1466.115.121.1.15{64} )";
        var result = Rfc4512SchemaParser.ParseAttributeTypeDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SyntaxOid, Is.EqualTo("1.3.6.1.4.1.1466.115.121.1.15"));
    }

    [Test]
    public void ParseAttributeType_NoExplicitSyntax_InheritedFromSuperior()
    {
        // When SYNTAX is omitted, the attribute inherits from SUP. SyntaxOid will be null.
        var definition = "( 2.5.4.4 NAME 'sn' SUP name )";
        var result = Rfc4512SchemaParser.ParseAttributeTypeDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SyntaxOid, Is.Null);
        Assert.That(result.SuperiorName, Is.EqualTo("name"));
    }

    [Test]
    public void ParseAttributeType_OperationalUsage_ParsesCorrectly()
    {
        var definition = "( 1.3.6.1.1.16.4 NAME 'entryUUID' DESC 'UUID of the entry' EQUALITY UUIDMatch ORDERING UUIDOrderingMatch SYNTAX 1.3.6.1.1.16.1 SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )";
        var result = Rfc4512SchemaParser.ParseAttributeTypeDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("entryUUID"));
        Assert.That(result.Usage, Is.EqualTo(Rfc4512AttributeUsage.DirectoryOperation));
        Assert.That(result.IsNoUserModification, Is.True);
        Assert.That(result.IsSingleValued, Is.True);
    }

    [Test]
    public void ParseAttributeType_DsaOperationUsage_ParsesCorrectly()
    {
        var definition = "( 2.5.18.1 NAME 'createTimestamp' EQUALITY generalizedTimeMatch ORDERING generalizedTimeOrderingMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.24 SINGLE-VALUE NO-USER-MODIFICATION USAGE dSAOperation )";
        var result = Rfc4512SchemaParser.ParseAttributeTypeDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Usage, Is.EqualTo(Rfc4512AttributeUsage.DsaOperation));
    }

    [Test]
    public void ParseAttributeType_NoUsageField_DefaultsToUserApplications()
    {
        var definition = "( 2.5.4.3 NAME 'cn' SUP name SYNTAX 1.3.6.1.4.1.1466.115.121.1.15{64} )";
        var result = Rfc4512SchemaParser.ParseAttributeTypeDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Usage, Is.EqualTo(Rfc4512AttributeUsage.UserApplications));
    }

    [Test]
    public void ParseAttributeType_MultipleNames_UsesFirstName()
    {
        var definition = "( 2.5.4.4 NAME ( 'sn' 'surname' ) SUP name SYNTAX 1.3.6.1.4.1.1466.115.121.1.15{64} )";
        var result = Rfc4512SchemaParser.ParseAttributeTypeDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("sn"));
    }

    [Test]
    public void ParseAttributeType_NoDescription_DescriptionIsNull()
    {
        var definition = "( 2.5.4.3 NAME 'cn' SUP name SYNTAX 1.3.6.1.4.1.1466.115.121.1.15{64} )";
        var result = Rfc4512SchemaParser.ParseAttributeTypeDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Description, Is.Null);
    }

    [Test]
    public void ParseAttributeType_DistributedOperationUsage_ParsesCorrectly()
    {
        var definition = "( 2.5.18.10 NAME 'subschemaSubentry' EQUALITY distinguishedNameMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.12 SINGLE-VALUE NO-USER-MODIFICATION USAGE distributedOperation )";
        var result = Rfc4512SchemaParser.ParseAttributeTypeDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Usage, Is.EqualTo(Rfc4512AttributeUsage.DistributedOperation));
    }

    #endregion

    #region GetRfcAttributeDataType (SYNTAX OID mapping)

    [Test]
    public void GetRfcAttributeDataType_DirectoryString_ReturnsText()
    {
        var result = Rfc4512SchemaParser.GetRfcAttributeDataType("1.3.6.1.4.1.1466.115.121.1.15");
        Assert.That(result, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void GetRfcAttributeDataType_IA5String_ReturnsText()
    {
        var result = Rfc4512SchemaParser.GetRfcAttributeDataType("1.3.6.1.4.1.1466.115.121.1.26");
        Assert.That(result, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void GetRfcAttributeDataType_Integer_ReturnsNumber()
    {
        var result = Rfc4512SchemaParser.GetRfcAttributeDataType("1.3.6.1.4.1.1466.115.121.1.27");
        Assert.That(result, Is.EqualTo(AttributeDataType.Number));
    }

    [Test]
    public void GetRfcAttributeDataType_Boolean_ReturnsBoolean()
    {
        var result = Rfc4512SchemaParser.GetRfcAttributeDataType("1.3.6.1.4.1.1466.115.121.1.7");
        Assert.That(result, Is.EqualTo(AttributeDataType.Boolean));
    }

    [Test]
    public void GetRfcAttributeDataType_GeneralisedTime_ReturnsDateTime()
    {
        var result = Rfc4512SchemaParser.GetRfcAttributeDataType("1.3.6.1.4.1.1466.115.121.1.24");
        Assert.That(result, Is.EqualTo(AttributeDataType.DateTime));
    }

    [Test]
    public void GetRfcAttributeDataType_OctetString_ReturnsBinary()
    {
        var result = Rfc4512SchemaParser.GetRfcAttributeDataType("1.3.6.1.4.1.1466.115.121.1.40");
        Assert.That(result, Is.EqualTo(AttributeDataType.Binary));
    }

    [Test]
    public void GetRfcAttributeDataType_DistinguishedName_ReturnsReference()
    {
        var result = Rfc4512SchemaParser.GetRfcAttributeDataType("1.3.6.1.4.1.1466.115.121.1.12");
        Assert.That(result, Is.EqualTo(AttributeDataType.Reference));
    }

    [Test]
    public void GetRfcAttributeDataType_Oid_ReturnsText()
    {
        var result = Rfc4512SchemaParser.GetRfcAttributeDataType("1.3.6.1.4.1.1466.115.121.1.38");
        Assert.That(result, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void GetRfcAttributeDataType_TelephoneNumber_ReturnsText()
    {
        var result = Rfc4512SchemaParser.GetRfcAttributeDataType("1.3.6.1.4.1.1466.115.121.1.50");
        Assert.That(result, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void GetRfcAttributeDataType_UnknownOid_ReturnsText()
    {
        var result = Rfc4512SchemaParser.GetRfcAttributeDataType("1.2.3.4.5.6.7.8.9");
        Assert.That(result, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void GetRfcAttributeDataType_Null_ReturnsText()
    {
        var result = Rfc4512SchemaParser.GetRfcAttributeDataType(null);
        Assert.That(result, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void GetRfcAttributeDataType_Uuid_ReturnsText()
    {
        // UUID syntax (RFC 4530) — entryUUID uses this
        var result = Rfc4512SchemaParser.GetRfcAttributeDataType("1.3.6.1.1.16.1");
        Assert.That(result, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void GetRfcAttributeDataType_PrintableString_ReturnsText()
    {
        var result = Rfc4512SchemaParser.GetRfcAttributeDataType("1.3.6.1.4.1.1466.115.121.1.44");
        Assert.That(result, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void GetRfcAttributeDataType_NumericString_ReturnsText()
    {
        var result = Rfc4512SchemaParser.GetRfcAttributeDataType("1.3.6.1.4.1.1466.115.121.1.36");
        Assert.That(result, Is.EqualTo(AttributeDataType.Text));
    }

    #endregion

    #region DetermineRfcAttributeWritability

    [Test]
    public void DetermineRfcWritability_UserApplications_NotNoUserMod_ReturnsWritable()
    {
        var result = Rfc4512SchemaParser.DetermineRfcAttributeWritability(
            Rfc4512AttributeUsage.UserApplications, isNoUserModification: false);
        Assert.That(result, Is.EqualTo(AttributeWritability.Writable));
    }

    [Test]
    public void DetermineRfcWritability_DirectoryOperation_ReturnsReadOnly()
    {
        var result = Rfc4512SchemaParser.DetermineRfcAttributeWritability(
            Rfc4512AttributeUsage.DirectoryOperation, isNoUserModification: false);
        Assert.That(result, Is.EqualTo(AttributeWritability.ReadOnly));
    }

    [Test]
    public void DetermineRfcWritability_DsaOperation_ReturnsReadOnly()
    {
        var result = Rfc4512SchemaParser.DetermineRfcAttributeWritability(
            Rfc4512AttributeUsage.DsaOperation, isNoUserModification: false);
        Assert.That(result, Is.EqualTo(AttributeWritability.ReadOnly));
    }

    [Test]
    public void DetermineRfcWritability_DistributedOperation_ReturnsReadOnly()
    {
        var result = Rfc4512SchemaParser.DetermineRfcAttributeWritability(
            Rfc4512AttributeUsage.DistributedOperation, isNoUserModification: false);
        Assert.That(result, Is.EqualTo(AttributeWritability.ReadOnly));
    }

    [Test]
    public void DetermineRfcWritability_NoUserModification_ReturnsReadOnly()
    {
        var result = Rfc4512SchemaParser.DetermineRfcAttributeWritability(
            Rfc4512AttributeUsage.UserApplications, isNoUserModification: true);
        Assert.That(result, Is.EqualTo(AttributeWritability.ReadOnly));
    }

    #endregion
}
