// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Staging;
using Moq;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// An RFC 4512 directory keeps some of the most useful facts about an entry in operational attributes (USAGE other
/// than userApplications): OpenLDAP's password policy failure times, creation and modification stamps, memberOf.
/// They belong to no object class, so discovery that only walks MUST and MAY never offered them (#2153, D8). These
/// tests hold discovery to adding each one to every structural Object Type as an unselected, read-only, tagged
/// attribute, and to leaving out what can never carry a value on an ordinary entry.
/// </summary>
[TestFixture]
public class LdapConnectorSchemaOperationalAttributeTests
{
    private const string SubschemaDn = "cn=Subschema";
    private const string SchemaNamingContext = "CN=Schema,CN=Configuration,DC=corp,DC=local";
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    // A trimmed but verbatim OpenLDAP 2.6 subschema subentry with the ppolicy overlay loaded.
    private static readonly string[] OpenLdapObjectClasses =
    [
        "( 2.5.6.0 NAME 'top' DESC 'top of the superclass chain' ABSTRACT MUST objectClass )",
        "( 2.5.20.1 NAME 'subschema' DESC 'RFC4512: controlling subschema (sub)entry' AUXILIARY MAY ( dITStructureRules $ nameForms $ dITContentRules $ objectClasses $ attributeTypes $ matchingRules $ matchingRuleUse ) )",
        "( 2.5.6.6 NAME 'person' DESC 'RFC2256: a person' SUP top STRUCTURAL MUST ( sn $ cn ) MAY ( userPassword $ description ) )",
        "( 2.16.840.1.113730.3.2.2 NAME 'inetOrgPerson' DESC 'RFC2798: Internet Organizational Person' SUP person STRUCTURAL MAY ( uid $ mail ) )",
        "( 2.5.6.9 NAME 'groupOfNames' DESC 'RFC2256: a group of names (DNs)' SUP top STRUCTURAL MUST ( member $ cn ) MAY description )",
        "( 1.3.6.1.1.1.2.0 NAME 'posixAccount' DESC 'Abstraction of an account with POSIX attributes' SUP top AUXILIARY MUST ( cn $ uid $ uidNumber ) )"
    ];

    private static readonly string[] OpenLdapAttributeTypes =
    [
        // User applications: discovered through the classes' MUST and MAY, exactly as before.
        "( 2.5.4.0 NAME 'objectClass' DESC 'RFC4512: object classes of the entity' EQUALITY objectIdentifierMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.38 )",
        "( 2.5.4.41 NAME 'name' DESC 'RFC4519: common supertype of name attributes' EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15{32768} )",
        "( 2.5.4.3 NAME ( 'cn' 'commonName' ) DESC 'RFC4519: common name(s) for which the entity is known by' SUP name )",
        "( 2.5.4.4 NAME ( 'sn' 'surname' ) DESC 'RFC2256: last (family) name(s) for which the entity is known by' SUP name )",
        "( 2.5.4.13 NAME 'description' DESC 'RFC4519: descriptive information' EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15{1024} )",
        "( 2.5.4.35 NAME 'userPassword' DESC 'RFC4519/2307: password of user' EQUALITY octetStringMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.40{128} )",
        "( 0.9.2342.19200300.100.1.1 NAME ( 'uid' 'userid' ) DESC 'RFC4519: user identifier' EQUALITY caseIgnoreMatch SUBSTR caseIgnoreSubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15{256} )",
        "( 0.9.2342.19200300.100.1.3 NAME ( 'mail' 'rfc822Mailbox' ) DESC 'RFC1274: RFC822 Mailbox' EQUALITY caseIgnoreIA5Match SUBSTR caseIgnoreIA5SubstringsMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.26{256} )",
        "( 2.5.4.31 NAME 'member' DESC 'RFC2256: member of a group' SUP distinguishedName )",
        "( 2.5.4.49 NAME 'distinguishedName' DESC 'RFC4519: common supertype of DN attributes' EQUALITY distinguishedNameMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.12 )",
        "( 1.3.6.1.1.1.1.0 NAME 'uidNumber' DESC 'RFC2307: An integer uniquely identifying a user in an administrative domain' EQUALITY integerMatch ORDERING integerOrderingMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.27 SINGLE-VALUE )",

        // Operational attributes an ordinary entry carries.
        "( 2.5.18.1 NAME 'createTimestamp' DESC 'RFC4512: time which object was created' EQUALITY generalizedTimeMatch ORDERING generalizedTimeOrderingMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.24 SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )",
        "( 2.5.18.2 NAME 'modifyTimestamp' DESC 'RFC4512: time which object was last modified' EQUALITY generalizedTimeMatch ORDERING generalizedTimeOrderingMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.24 SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )",
        "( 2.5.18.3 NAME 'creatorsName' DESC 'RFC4512: name of creator' EQUALITY distinguishedNameMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.12 SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )",
        "( 2.5.18.9 NAME 'hasSubordinates' DESC 'X.501: entry has children' EQUALITY booleanMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.7 SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )",
        "( 2.5.18.10 NAME 'subschemaSubentry' DESC 'RFC4512: name of controlling subschema entry' EQUALITY distinguishedNameMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.12 SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )",
        "( 1.3.6.1.1.16.4 NAME 'entryUUID' DESC 'UUID of the entry' EQUALITY UUIDMatch ORDERING UUIDOrderingMatch SYNTAX 1.3.6.1.1.16.1 SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )",
        "( 1.3.6.1.4.1.4203.666.1.7 NAME 'entryCSN' DESC 'change sequence number of the entry content' EQUALITY CSNMatch ORDERING CSNOrderingMatch SYNTAX 1.3.6.1.4.1.4203.666.11.2.1{64} SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )",
        "( 1.2.840.113556.1.2.102 NAME 'memberOf' DESC 'Group that the entry belongs to' EQUALITY distinguishedNameMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.12 USAGE dSAOperation X-ORIGIN 'iPlanet Delegated Administrator' )",
        "( 1.3.6.1.4.1.42.2.27.8.1.16 NAME ( 'pwdChangedTime' ) DESC 'The time the password was last changed' EQUALITY generalizedTimeMatch ORDERING generalizedTimeOrderingMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.24 SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )",
        "( 1.3.6.1.4.1.42.2.27.8.1.17 NAME ( 'pwdAccountLockedTime' ) DESC 'The time an user account was locked' EQUALITY generalizedTimeMatch ORDERING generalizedTimeOrderingMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.24 SINGLE-VALUE USAGE directoryOperation )",
        "( 1.3.6.1.4.1.42.2.27.8.1.19 NAME ( 'pwdFailureTime' ) DESC 'The timestamps of the last consecutive authentication failures' EQUALITY generalizedTimeMatch ORDERING generalizedTimeOrderingMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.24 NO-USER-MODIFICATION USAGE directoryOperation )",
        "( 1.3.6.1.4.1.42.2.27.8.1.20 NAME ( 'pwdHistory' ) DESC 'The history of users passwords' EQUALITY octetStringMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.40 NO-USER-MODIFICATION USAGE directoryOperation )",
        "( 1.3.6.1.4.1.42.2.27.8.1.22 NAME ( 'pwdGraceUseTime' ) DESC 'The timestamps of the grace authentication after the password has expired' EQUALITY generalizedTimeMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.24 NO-USER-MODIFICATION USAGE directoryOperation )",

        // Operational attributes that only ever appear on the subschema subentry or the root DSE.
        "( 2.5.21.5 NAME 'attributeTypes' DESC 'RFC4512: attribute types' EQUALITY objectIdentifierFirstComponentMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.3 USAGE directoryOperation )",
        "( 2.5.21.6 NAME 'objectClasses' DESC 'RFC4512: object classes' EQUALITY objectIdentifierFirstComponentMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.37 USAGE directoryOperation )",
        "( 2.5.21.4 NAME 'matchingRules' DESC 'RFC4512: matching rules' EQUALITY objectIdentifierFirstComponentMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.30 USAGE directoryOperation )",
        "( 1.3.6.1.4.1.1466.101.120.16 NAME 'ldapSyntaxes' DESC 'RFC4512: LDAP syntaxes' EQUALITY objectIdentifierFirstComponentMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.54 USAGE directoryOperation )",
        "( 1.3.6.1.4.1.1466.101.120.5 NAME 'namingContexts' DESC 'RFC4512: naming contexts' SYNTAX 1.3.6.1.4.1.1466.115.121.1.12 USAGE dSAOperation )",
        "( 1.3.6.1.4.1.1466.101.120.13 NAME 'supportedControl' DESC 'RFC4512: supported controls' SYNTAX 1.3.6.1.4.1.1466.115.121.1.38 USAGE dSAOperation )",
        "( 1.3.6.1.4.1.4203.1.12.2.1 NAME 'vendorName' DESC 'RFC3045: name of implementation vendor' EQUALITY caseExactMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 SINGLE-VALUE NO-USER-MODIFICATION USAGE dSAOperation )",
        "( 1.3.6.1.4.1.4203.1.3.1 NAME 'entry' DESC 'OpenLDAP ACL entry pseudo-attribute' SYNTAX 1.3.6.1.4.1.4203.1.1.1 SINGLE-VALUE NO-USER-MODIFICATION USAGE dSAOperation )",

        // An operational attribute the directory has declared superseded.
        "( 1.3.6.1.4.1.4203.666.1.99 NAME 'retiredCounter' DESC 'A superseded counter' OBSOLETE SYNTAX 1.3.6.1.4.1.1466.115.121.1.27 NO-USER-MODIFICATION USAGE dSAOperation )"
    ];

    [Test]
    public async Task GetSchemaAsync_OpenLdap_AddsPwdFailureTimeToEveryStructuralObjectTypeAsync()
    {
        var schema = await DiscoverRfc4512Async(LdapDirectoryType.OpenLDAP);

        foreach (var structural in new[] { "person", "inetOrgPerson", "groupOfNames" })
        {
            var attribute = FindAttribute(schema, structural, "pwdFailureTime");
            Assert.That(attribute, Is.Not.Null, $"pwdFailureTime must be offered on the structural Object Type '{structural}'.");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(attribute!.Type, Is.EqualTo(AttributeDataType.DateTime), "GeneralizedTime maps to DateTime.");
                Assert.That(attribute!.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued),
                    "pwdFailureTime is not SINGLE-VALUE: it holds one timestamp per consecutive failure.");
                Assert.That(attribute!.Writability, Is.EqualTo(AttributeWritability.ReadOnly),
                    "An operational attribute is maintained by the directory, so it must never be an export target.");
                Assert.That(attribute!.Tags, Is.EqualTo(new[] { AttributeTags.Operational }),
                    "The attribute must be tagged so the schema screen can group it.");
                Assert.That(attribute!.Required, Is.False);
                Assert.That(attribute!.ClassName, Is.Null, "An operational attribute belongs to no object class.");
                Assert.That(attribute!.Description, Is.EqualTo("The timestamps of the last consecutive authentication failures"));
            }
        }
    }

    [Test]
    public async Task GetSchemaAsync_OpenLdap_AddsEveryReadableOperationalAttributeAsync()
    {
        var schema = await DiscoverRfc4512Async(LdapDirectoryType.OpenLDAP);

        var operational = OperationalAttributeNames(schema, "inetOrgPerson");

        Assert.That(operational, Is.EquivalentTo(new[]
        {
            "createTimestamp", "modifyTimestamp", "creatorsName", "hasSubordinates", "subschemaSubentry", "entryCSN",
            "memberOf", "pwdChangedTime", "pwdAccountLockedTime", "pwdFailureTime", "pwdGraceUseTime"
        }), "Every operational attribute an ordinary entry can carry is offered, whatever its usage.");
    }

    [Test]
    public async Task GetSchemaAsync_OpenLdap_ReadsEachOperationalAttributesDefinitionAsync()
    {
        var schema = await DiscoverRfc4512Async(LdapDirectoryType.OpenLDAP);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(FindAttribute(schema, "inetOrgPerson", "createTimestamp")!.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
            Assert.That(FindAttribute(schema, "inetOrgPerson", "creatorsName")!.Type, Is.EqualTo(AttributeDataType.Reference));
            Assert.That(FindAttribute(schema, "inetOrgPerson", "hasSubordinates")!.Type, Is.EqualTo(AttributeDataType.Boolean));
            Assert.That(FindAttribute(schema, "inetOrgPerson", "memberOf")!.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
            Assert.That(FindAttribute(schema, "inetOrgPerson", "pwdAccountLockedTime")!.Writability, Is.EqualTo(AttributeWritability.ReadOnly),
                "An operational attribute without NO-USER-MODIFICATION is still the directory's, so it stays read-only.");
        }
    }

    [Test]
    public async Task GetSchemaAsync_OpenLdap_LeavesOutSubschemaRootDseAndObsoleteAttributesAsync()
    {
        var schema = await DiscoverRfc4512Async(LdapDirectoryType.OpenLDAP);

        var names = schema.ObjectTypes.Single(t => t.Name == "inetOrgPerson").Attributes.Select(a => a.Name).ToList();

        Assert.That(names, Has.None.AnyOf(
                "attributeTypes", "objectClasses", "matchingRules", "ldapSyntaxes",
                "namingContexts", "supportedControl", "vendorName", "entry", "retiredCounter"),
            "Attributes only the subschema subentry or the root DSE carry, pseudo-attributes and obsolete types can never hold a value on an imported entry.");
    }

    [Test]
    public async Task GetSchemaAsync_OpenLdap_DoesNotOfferPasswordHistoryAsync()
    {
        // pwdHistory holds hashes of previous passwords: credential material JIM never manages, so the Connector
        // does not offer it, rather than leaving schema import to block it on every structural type.
        var schema = await DiscoverRfc4512Async(LdapDirectoryType.OpenLDAP);

        Assert.That(schema.ObjectTypes.SelectMany(t => t.Attributes).Select(a => a.Name), Has.None.EqualTo("pwdHistory"));
    }

    [Test]
    public async Task GetSchemaAsync_OpenLdap_DoesNotAddOperationalAttributesToAuxiliaryClassesAsync()
    {
        var schema = await DiscoverRfc4512Async(LdapDirectoryType.OpenLDAP);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(OperationalAttributeNames(schema, "posixAccount"), Is.Empty,
                "An auxiliary class is merged into a structural type, which already carries the operational attributes.");
            Assert.That(OperationalAttributeNames(schema, "subschema"), Is.Empty);
        }
    }

    [Test]
    public async Task GetSchemaAsync_OpenLdap_LeavesTheExternalIdAndUserAttributesAsTheyWereAsync()
    {
        var schema = await DiscoverRfc4512Async(LdapDirectoryType.OpenLDAP);

        var entryUuid = FindAttribute(schema, "inetOrgPerson", "entryUUID");
        var mail = FindAttribute(schema, "inetOrgPerson", "mail");
        var distinguishedName = FindAttribute(schema, "inetOrgPerson", "distinguishedName");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entryUuid!.Tags, Is.Empty, "The External ID is synthesised as before, so a refresh does not restate it.");
            Assert.That(entryUuid!.ClassName, Is.EqualTo("inetOrgPerson"));
            Assert.That(entryUuid!.Description, Is.EqualTo("System-assigned unique identifier for this entry"));
            Assert.That(mail!.Tags, Is.Empty);
            Assert.That(distinguishedName!.Tags, Is.Empty);
            Assert.That(schema.ObjectTypes.Single(t => t.Name == "inetOrgPerson").Attributes.Count(a => a.Name == "pwdFailureTime"), Is.EqualTo(1));
        }
    }

    [TestCase(nameof(LdapDirectoryType.DirectoryServer389))]
    [TestCase(nameof(LdapDirectoryType.Generic))]
    public async Task GetSchemaAsync_OtherRfc4512Directory_AddsOperationalAttributesThroughTheSamePathAsync(string directoryType)
    {
        var schema = await DiscoverRfc4512Async(Enum.Parse<LdapDirectoryType>(directoryType));

        Assert.That(OperationalAttributeNames(schema, "inetOrgPerson"), Does.Contain("modifyTimestamp"));
    }

    [TestCase(nameof(LdapDirectoryType.ActiveDirectory))]
    [TestCase(nameof(LdapDirectoryType.SambaAD))]
    public async Task GetSchemaAsync_ActiveDirectoryFamily_TagsNoAttributeOperationalAsync(string directoryType)
    {
        // Active Directory and Samba AD are discovered from the schema partition, not the subschema subentry, so they
        // never reach the RFC 4512 path: their operational attributes (whenChanged, uSNChanged, memberOf) are on
        // top's MAY sets, so their own discovery already offers them and must not change.
        var attributes = new[]
        {
            AdAttributeSchema("whenChanged", omSyntax: 24, systemOnly: true),
            AdAttributeSchema("sAMAccountName", omSyntax: 64, systemOnly: false)
        };
        var userClass = LdapTestResponses.EntryWithValues($"CN=user,{SchemaNamingContext}",
            ("name", ["user"]), ("lDAPDisplayName", ["user"]), ("objectClassCategory", ["1"]), ("subClassOf", ["user"]),
            ("systemMayContain", ["whenChanged", "sAMAccountName"]));

        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) =>
            {
                var search = (SearchRequest)request;
                var filter = (string)search.Filter;
                if (search.Scope == SearchScope.Base && search.Attributes.Contains("schemaNamingContext"))
                    return LdapTestResponses.SearchResponseWith("", ("schemaNamingContext", SchemaNamingContext));
                if (filter.Contains("objectClassCategory") || filter.Contains("objectClass=classSchema"))
                    return LdapTestResponses.SearchResponseWithEntries(userClass);
                if (filter.Contains("objectClass=attributeSchema"))
                    return LdapTestResponses.SearchResponseWithEntries(attributes);
                return LdapTestResponses.EmptySearchResponse();
            });

        var rootDse = new LdapConnectorRootDse { DirectoryType = Enum.Parse<LdapDirectoryType>(directoryType) };
        var schema = await new LdapConnectorSchema(executor.Object, Logger, rootDse).GetSchemaAsync();

        Assert.That(schema.ObjectTypes.Single(t => t.Name == "user").Attributes.Select(a => a.Name), Does.Contain("whenChanged"),
            "The fixture must reach the schema partition path, or the assertion below proves nothing.");
        Assert.That(schema.ObjectTypes.SelectMany(t => t.Attributes).Where(a => a.Tags.Count > 0), Is.Empty);
    }

    private static ConnectorSchemaAttribute? FindAttribute(ConnectorSchema schema, string objectTypeName, string attributeName) =>
        schema.ObjectTypes.Single(t => t.Name == objectTypeName).Attributes
            .SingleOrDefault(a => a.Name.Equals(attributeName, StringComparison.OrdinalIgnoreCase));

    private static List<string> OperationalAttributeNames(ConnectorSchema schema, string objectTypeName) =>
        schema.ObjectTypes.Single(t => t.Name == objectTypeName).Attributes
            .Where(a => a.Tags.Contains(AttributeTags.Operational))
            .Select(a => a.Name)
            .ToList();

    private static SearchResultEntry AdAttributeSchema(string name, int omSyntax, bool systemOnly) =>
        LdapTestResponses.Entry($"CN={name},{SchemaNamingContext}",
            ("lDAPDisplayName", name), ("isSingleValued", "TRUE"), ("oMSyntax", omSyntax.ToString()),
            ("systemOnly", systemOnly ? "TRUE" : "FALSE"));

    private static async Task<ConnectorSchema> DiscoverRfc4512Async(LdapDirectoryType directoryType)
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) =>
            {
                var search = (SearchRequest)request;
                if (search.Scope == SearchScope.Base && search.Attributes.Contains("subschemaSubentry"))
                    return LdapTestResponses.SearchResponseWith("", ("subschemaSubentry", SubschemaDn));

                if (search.DistinguishedName == SubschemaDn)
                    return LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.EntryWithValues(SubschemaDn,
                        ("objectClasses", OpenLdapObjectClasses),
                        ("attributeTypes", OpenLdapAttributeTypes)));

                return LdapTestResponses.EmptySearchResponse();
            });

        var rootDse = new LdapConnectorRootDse { DirectoryType = directoryType };
        return await new LdapConnectorSchema(executor.Object, Logger, rootDse).GetSchemaAsync();
    }
}
