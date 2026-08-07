// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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

    [Test]
    public void ParseObjectClass_CapturesTheOid()
    {
        // The OID identifies which enterprise defined the class, which is how the connector tells the directory's
        // own machinery apart from the classes an administrator manages.
        var definition = "( 1.3.6.1.4.1.4203.1.12.2.4.0.1 NAME 'olcGlobal' SUP olcConfig STRUCTURAL MAY olcConfigFile )";
        var result = Rfc4512SchemaParser.ParseObjectClassDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Oid, Is.EqualTo("1.3.6.1.4.1.4203.1.12.2.4.0.1"));
    }

    [Test]
    public void ParseObjectClass_ObsoleteClass_IsReportedObsolete()
    {
        var definition = "( 0.9.2342.19200300.100.4.4 NAME 'pilotPerson' OBSOLETE SUP person STRUCTURAL MAY userid )";
        var result = Rfc4512SchemaParser.ParseObjectClassDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsObsolete, Is.True);
    }

    [Test]
    public void ParseObjectClass_WithoutAnOid_ReportsNoOidRatherThanTheFirstKeyword()
    {
        // The OID is read positionally, so a definition that opens straight onto a clause must not yield "NAME" as
        // an object identifier: LdapObjectTypeClassification decides visibility by matching the OID against the
        // directory's own arcs, and a junk OID there is a classification made on nonsense.
        var definition = "( NAME 'malformed' SUP top STRUCTURAL MUST cn )";
        var result = Rfc4512SchemaParser.ParseObjectClassDescription(definition);

        Assert.That(result, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result!.Oid, Is.Null);
            Assert.That(result.Name, Is.EqualTo("malformed"), "the rest of the definition is still usable");
        }
    }

    [Test]
    public void ParseObjectClass_ClassWithoutTheObsoleteKeyword_IsNotReportedObsolete()
    {
        var definition = "( 2.5.6.6 NAME 'person' SUP top STRUCTURAL MUST ( sn $ cn ) )";
        var result = Rfc4512SchemaParser.ParseObjectClassDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsObsolete, Is.False);
    }

    #endregion

    #region ParseDitContentRuleDescription

    // A DIT Content Rule (RFC 4512 § 4.1.6) is how a directory says which auxiliary classes may be attached to
    // entries of one structural class, and it is the only machine-readable statement of that anywhere in LDAP: the
    // auxiliary class's own definition says nothing about where it may be used. The rule names its structural class
    // by OID and never by name, which is why the connector needs an OID-keyed class index alongside the name-keyed
    // one.

    [Test]
    public void ParseDitContentRule_WithAuxMustMayAndNot_ParsesEveryList()
    {
        var definition = "( 2.5.6.6 NAME 'personContentRule' DESC 'what a person entry may carry' " +
                         "AUX ( posixAccount $ shadowAccount ) MUST uid MAY ( loginShell $ gecos ) NOT ( telephoneNumber ) )";
        var result = Rfc4512SchemaParser.ParseDitContentRuleDescription(definition);

        Assert.That(result, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result!.Oid, Is.EqualTo("2.5.6.6"), "the OID identifies the structural class the rule governs");
            Assert.That(result.Name, Is.EqualTo("personContentRule"));
            Assert.That(result.Description, Is.EqualTo("what a person entry may carry"));
            Assert.That(result.AuxiliaryClasses, Is.EquivalentTo(new[] { "posixAccount", "shadowAccount" }));
            Assert.That(result.MustAttributes, Is.EquivalentTo(new[] { "uid" }));
            Assert.That(result.MayAttributes, Is.EquivalentTo(new[] { "loginShell", "gecos" }));
            Assert.That(result.ProhibitedAttributes, Is.EquivalentTo(new[] { "telephoneNumber" }),
                "NOT is a prohibition, not another MAY; merging the two would offer administrators an attribute the directory will refuse");
        }
    }

    [Test]
    public void ParseDitContentRule_WithASingleAuxClassAndNoParentheses_ParsesTheOneClass()
    {
        var definition = "( 2.16.840.1.113730.3.2.2 NAME 'inetOrgPersonContentRule' AUX pkiUser )";
        var result = Rfc4512SchemaParser.ParseDitContentRuleDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.AuxiliaryClasses, Is.EquivalentTo(new[] { "pkiUser" }));
    }

    [Test]
    public void ParseDitContentRule_WithAuxClassesNamedByOid_PreservesThemVerbatim()
    {
        // A directory may name classes in these lists by OID rather than by descriptor. Resolving them is the
        // caller's job (that is what the OID index is for); the parser must not silently drop what it cannot name.
        var definition = "( 2.5.6.6 AUX ( 1.3.6.1.1.1.2.0 $ 1.3.6.1.1.1.2.1 ) )";
        var result = Rfc4512SchemaParser.ParseDitContentRuleDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.AuxiliaryClasses, Is.EquivalentTo(new[] { "1.3.6.1.1.1.2.0", "1.3.6.1.1.1.2.1" }));
    }

    [Test]
    public void ParseDitContentRule_WithoutAName_IsStillParsed()
    {
        // Unlike an objectClass description, NAME is optional here and the OID alone identifies the rule, so
        // discarding an unnamed rule would lose auxiliary classes a directory genuinely permits.
        var definition = "( 2.5.6.6 AUX posixAccount )";
        var result = Rfc4512SchemaParser.ParseDitContentRuleDescription(definition);

        Assert.That(result, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result!.Oid, Is.EqualTo("2.5.6.6"));
            Assert.That(result.Name, Is.Null);
        }
    }

    [Test]
    public void ParseDitContentRule_WithoutAnOid_ReturnsNull()
    {
        // With no OID there is no structural class to attach the rule to, so it cannot be acted on.
        var definition = "( NAME 'orphanRule' AUX posixAccount )";
        var result = Rfc4512SchemaParser.ParseDitContentRuleDescription(definition);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void ParseDitContentRule_WithMultipleNames_TakesTheFirst()
    {
        var definition = "( 2.5.6.6 NAME ( 'personContentRule' 'personRule' ) AUX posixAccount )";
        var result = Rfc4512SchemaParser.ParseDitContentRuleDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("personContentRule"));
    }

    [Test]
    public void ParseDitContentRule_MarkedObsolete_IsReportedObsolete()
    {
        var definition = "( 2.5.6.6 NAME 'personContentRule' OBSOLETE AUX posixAccount )";
        var result = Rfc4512SchemaParser.ParseDitContentRuleDescription(definition);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsObsolete, Is.True);
    }

    [Test]
    public void ParseDitContentRule_WithNoAuxClauseAtAll_PermitsNoAuxiliaryClasses()
    {
        // A rule may exist purely to constrain attributes. It permits no auxiliary classes, which is a statement,
        // not an absence of one.
        var definition = "( 2.5.6.6 NAME 'personContentRule' MUST uid )";
        var result = Rfc4512SchemaParser.ParseDitContentRuleDescription(definition);

        Assert.That(result, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result!.AuxiliaryClasses, Is.Empty);
            Assert.That(result.MustAttributes, Is.EquivalentTo(new[] { "uid" }));
        }
    }

    [TestCase("")]
    [TestCase("   ")]
    public void ParseDitContentRule_WithNothingToParse_ReturnsNull(string definition)
    {
        Assert.That(Rfc4512SchemaParser.ParseDitContentRuleDescription(definition), Is.Null);
    }

    #endregion

    #region IndexObjectClasses

    private static readonly string[] SampleClassDefinitions =
    [
        "( 2.5.6.6 NAME 'person' SUP top STRUCTURAL MUST ( sn $ cn ) )",
        "( 1.3.6.1.1.1.2.0 NAME 'posixAccount' SUP top AUXILIARY MUST ( cn $ uid $ uidNumber ) )",
        "( 2.5.6.0 NAME 'top' ABSTRACT MUST objectClass )"
    ];

    [Test]
    public void IndexObjectClasses_KeysTheSameClassByBothNameAndOid()
    {
        var index = Rfc4512SchemaParser.IndexObjectClasses(SampleClassDefinitions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(index.ByName.Keys, Is.EquivalentTo(new[] { "person", "posixAccount", "top" }));
            Assert.That(index.ByOid.Keys, Is.EquivalentTo(new[] { "2.5.6.6", "1.3.6.1.1.1.2.0", "2.5.6.0" }));
            Assert.That(index.ByOid["1.3.6.1.1.1.2.0"], Is.SameAs(index.ByName["posixAccount"]),
                "both indexes must hand back the same parsed class, so a caller that resolves by OID and one that resolves by name never disagree");
        }
    }

    [Test]
    public void IndexObjectClasses_LooksUpNamesWithoutRegardToCase()
    {
        // LDAP descriptors are case-insensitive, and a DIT Content Rule may spell a class differently from the
        // class's own definition.
        var index = Rfc4512SchemaParser.IndexObjectClasses(SampleClassDefinitions);

        Assert.That(index.ByName.ContainsKey("POSIXACCOUNT"), Is.True);
    }

    [Test]
    public void IndexObjectClasses_WhenTwoDefinitionsShareAnOid_KeepsTheFirst()
    {
        var index = Rfc4512SchemaParser.IndexObjectClasses(
        [
            "( 2.5.6.6 NAME 'person' SUP top STRUCTURAL MUST sn )",
            "( 2.5.6.6 NAME 'personDuplicate' SUP top STRUCTURAL MUST cn )"
        ]);

        Assert.That(index.ByOid["2.5.6.6"].Name, Is.EqualTo("person"));
    }

    [Test]
    public void IndexObjectClasses_WithAnUnparseableDefinition_SkipsItRatherThanFailing()
    {
        // One malformed definition must not cost an administrator the rest of the directory's schema.
        var index = Rfc4512SchemaParser.IndexObjectClasses(
        [
            "not a schema definition at all",
            "( 2.5.6.6 NAME 'person' SUP top STRUCTURAL MUST sn )"
        ]);

        Assert.That(index.ByName.Keys, Is.EquivalentTo(new[] { "person" }));
    }

    #endregion

    #region IndexDitContentRules

    [Test]
    public void IndexDitContentRules_KeysEachRuleByTheClassItGoverns()
    {
        var rules = Rfc4512SchemaParser.IndexDitContentRules(
        [
            "( 2.5.6.6 NAME 'personContentRule' AUX posixAccount )",
            "( 2.16.840.1.113730.3.2.2 NAME 'inetOrgPersonContentRule' AUX shadowAccount )"
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rules.Keys, Is.EquivalentTo(new[] { "2.5.6.6", "2.16.840.1.113730.3.2.2" }));
            Assert.That(rules["2.5.6.6"].AuxiliaryClasses, Is.EquivalentTo(new[] { "posixAccount" }));
        }
    }

    [Test]
    public void IndexDitContentRules_WithNoRulesPublished_ReturnsAnEmptyIndex()
    {
        // The ordinary case: a stock OpenLDAP publishes no dITContentRules attribute at all. That means nothing is
        // suggested, not that nothing is permitted, so it must not read as a discovery failure.
        Assert.That(Rfc4512SchemaParser.IndexDitContentRules([]), Is.Empty);
    }

    [Test]
    public void IndexDitContentRules_WhenTwoRulesGovernTheSameClass_KeepsTheFirst()
    {
        var rules = Rfc4512SchemaParser.IndexDitContentRules(
        [
            "( 2.5.6.6 NAME 'first' AUX posixAccount )",
            "( 2.5.6.6 NAME 'second' AUX shadowAccount )"
        ]);

        Assert.That(rules["2.5.6.6"].Name, Is.EqualTo("first"));
    }

    [Test]
    public void IndexDitContentRules_WithAnUnusableRule_SkipsItRatherThanFailing()
    {
        // One malformed or class-less rule must not cost an administrator the suggestions from every other rule.
        var rules = Rfc4512SchemaParser.IndexDitContentRules(
        [
            "not a rule at all",
            "( NAME 'noClassToAttachTo' AUX posixAccount )",
            "( 2.5.6.6 NAME 'personContentRule' AUX posixAccount )"
        ]);

        Assert.That(rules.Keys, Is.EquivalentTo(new[] { "2.5.6.6" }));
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
