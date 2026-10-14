// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using Moq;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Schema discovery states which attributes' values are case-exact (#2051), through both discovery paths: Active
/// Directory from each attribute's syntax, an RFC 4512 directory from each attribute type's EQUALITY matching rule.
/// </summary>
[TestFixture]
public class LdapConnectorSchemaCaseExactTests
{
    private const string SchemaNamingContext = "CN=Schema,CN=Configuration,DC=corp,DC=local";
    private const string SubschemaDn = "cn=Subschema";
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    [Test]
    public async Task GetSchemaAsync_ActiveDirectory_DeclaresCaseExactFromTheSyntaxAsync()
    {
        var schema = await DiscoverActiveDirectoryAsync(
            AttributeSchema("sAMAccountName", omSyntax: 64),
            AttributeSchema("caseSensitiveCode", omSyntax: 27),
            AttributeSchema("ia5Code", omSyntax: 22),
            AttributeSchema("printableCode", omSyntax: 19),
            AttributeSchema("employeeType", omSyntax: 20),
            AttributeSchema("cn", omSyntax: 64));

        var user = schema.ObjectTypes.Single(t => t.Name == "user");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(CaseExact(user, "sAMAccountName"), Is.False, "Unicode strings ignore case in Active Directory.");
            Assert.That(CaseExact(user, "caseSensitiveCode"), Is.True);
            Assert.That(CaseExact(user, "ia5Code"), Is.True);
            Assert.That(CaseExact(user, "printableCode"), Is.True);
            Assert.That(CaseExact(user, "employeeType"), Is.False, "Teletex strings ignore case.");
        }
    }

    [Test]
    public async Task GetSchemaAsync_Rfc4512_DeclaresCaseExactFromTheEqualityRuleAsync()
    {
        var schema = await DiscoverRfc4512Async(
            "( 2.5.4.0 NAME 'objectClass' EQUALITY objectIdentifierMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.38 )",
            "( 2.5.4.41 NAME 'name' EQUALITY caseIgnoreMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 2.5.4.3 NAME 'cn' SUP name )",
            "( 0.9.2342.19200300.100.1.1 NAME 'uid' EQUALITY caseExactMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 1.1.1 NAME 'exactChild' SUP uid )",
            "( 0.9.2342.19200300.100.1.3 NAME 'mail' EQUALITY caseIgnoreIA5Match SYNTAX 1.3.6.1.4.1.1466.115.121.1.26 )",
            "( 1.3.6.1.1.1.1.0 NAME 'uidNumber' EQUALITY integerMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.27 )",
            "( 2.5.4.31 NAME 'member' EQUALITY distinguishedNameMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.12 )",
            "( 1.1.2 NAME 'plain' SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 1.3.6.1.1.16.4 NAME 'entryUUID' EQUALITY UUIDMatch SYNTAX 1.3.6.1.1.16.1 SINGLE-VALUE NO-USER-MODIFICATION USAGE directoryOperation )");

        var account = schema.ObjectTypes.Single(t => t.Name == "account");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(CaseExact(account, "uid"), Is.True);
            Assert.That(CaseExact(account, "exactChild"), Is.True, "A subtype inherits its supertype's EQUALITY.");
            Assert.That(CaseExact(account, "cn"), Is.False, "cn inherits caseIgnoreMatch from name.");
            Assert.That(CaseExact(account, "mail"), Is.False);
            Assert.That(CaseExact(account, "plain"), Is.False, "No matching rule at all ignores case.");
            Assert.That(CaseExact(account, "uidNumber"), Is.False, "Only Text attributes carry the declaration.");
            Assert.That(CaseExact(account, "member"), Is.False, "Distinguished names ignore case.");
            Assert.That(CaseExact(account, "entryUUID"), Is.True,
                "The synthesised External ID takes its declaration from the directory's own definition (uuidMatch).");
        }
    }

    private static bool CaseExact(ConnectorSchemaObjectType objectType, string attributeName) =>
        objectType.Attributes.Single(a => a.Name.Equals(attributeName, StringComparison.OrdinalIgnoreCase)).CaseExact;

    private static SearchResultEntry AttributeSchema(string name, int omSyntax) =>
        LdapTestResponses.Entry($"CN={name},{SchemaNamingContext}",
            ("lDAPDisplayName", name), ("isSingleValued", "TRUE"), ("oMSyntax", omSyntax.ToString()), ("systemOnly", "FALSE"));

    private static async Task<ConnectorSchema> DiscoverActiveDirectoryAsync(params SearchResultEntry[] attributes)
    {
        var userClass = LdapTestResponses.EntryWithValues($"CN=user,{SchemaNamingContext}",
            ("name", ["user"]), ("lDAPDisplayName", ["user"]), ("objectClassCategory", ["1"]), ("subClassOf", ["user"]),
            ("systemMayContain", attributes.Select(a => (string)a.Attributes["lDAPDisplayName"][0]).ToArray()));

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

        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };
        return await new LdapConnectorSchema(executor.Object, Logger, rootDse).GetSchemaAsync();
    }

    private static async Task<ConnectorSchema> DiscoverRfc4512Async(params string[] attributeTypes)
    {
        string[] objectClasses =
        [
            "( 2.5.6.0 NAME 'top' ABSTRACT MUST objectClass )",
            "( 0.9.2342.19200300.100.4.5 NAME 'account' SUP top STRUCTURAL MUST uid MAY ( cn $ exactChild $ mail $ uidNumber $ member $ plain ) )"
        ];

        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) =>
            {
                var search = (SearchRequest)request;
                if (search.Scope == SearchScope.Base && search.Attributes.Contains("subschemaSubentry"))
                    return LdapTestResponses.SearchResponseWith("", ("subschemaSubentry", SubschemaDn));

                if (search.DistinguishedName == SubschemaDn)
                    return LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.EntryWithValues(SubschemaDn,
                        ("objectClasses", objectClasses),
                        ("attributeTypes", attributeTypes)));

                return LdapTestResponses.EmptySearchResponse();
            });

        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };
        return await new LdapConnectorSchema(executor.Object, Logger, rootDse).GetSchemaAsync();
    }
}
