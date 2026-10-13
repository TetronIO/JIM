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
/// An LDAP directory compares distinguished names ignoring case and reports them in whatever case it stored, so the
/// LDAP Connector declares every DN-valued attribute case-insensitive (#2195): <c>distinguishedName</c> itself, and
/// every attribute of DN syntax (a Reference). Other text, such as a display name, keeps the case an administrator
/// gives it, and a change of case there is a real change to export.
/// </summary>
[TestFixture]
public class LdapConnectorSchemaCaseInsensitiveTests
{
    private const string SchemaNamingContext = "CN=Schema,CN=Configuration,DC=corp,DC=local";
    private const string SubschemaDn = "cn=Subschema";
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    [Test]
    public async Task GetSchemaAsync_ActiveDirectory_DeclaresDistinguishedNamesCaseInsensitiveAsync()
    {
        var schema = await DiscoverActiveDirectoryAsync();
        var user = schema.ObjectTypes.Single(type => type.Name == "user");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Attribute(user, "distinguishedName").CaseInsensitive, Is.True);
            Assert.That(Attribute(user, "manager").CaseInsensitive, Is.True, "manager holds a DN.");
            Assert.That(Attribute(user, "displayName").CaseInsensitive, Is.False);
        }
    }

    [Test]
    public async Task GetSchemaAsync_Rfc4512_DeclaresDistinguishedNamesCaseInsensitiveAsync()
    {
        var schema = await DiscoverRfc4512Async();
        var person = schema.ObjectTypes.Single(type => type.Name == "inetOrgPerson");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Attribute(person, "distinguishedName").CaseInsensitive, Is.True,
                "The DN is synthesised for an RFC 4512 directory, which does not publish it, and is a DN all the same.");
            Assert.That(Attribute(person, "manager").CaseInsensitive, Is.True, "manager holds a DN.");
            Assert.That(Attribute(person, "displayName").CaseInsensitive, Is.False);
        }
    }

    private static ConnectorSchemaAttribute Attribute(ConnectorSchemaObjectType objectType, string name)
    {
        var attribute = objectType.Attributes.SingleOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Assert.That(attribute, Is.Not.Null, $"{name} was not discovered on {objectType.Name}.");
        return attribute!;
    }

    private static async Task<ConnectorSchema> DiscoverActiveDirectoryAsync()
    {
        var userClass = LdapTestResponses.EntryWithValues($"CN=user,{SchemaNamingContext}",
            ("name", ["user"]), ("lDAPDisplayName", ["user"]), ("objectClassCategory", ["1"]),
            ("subClassOf", ["top"]), ("systemMayContain", ["distinguishedName", "manager", "displayName"]));
        var topClass = LdapTestResponses.EntryWithValues($"CN=top,{SchemaNamingContext}",
            ("name", ["top"]), ("lDAPDisplayName", ["top"]), ("objectClassCategory", ["2"]), ("subClassOf", ["top"]));

        var attributes = new[]
        {
            AttributeSchema("distinguishedName", omSyntax: "127"),
            AttributeSchema("manager", omSyntax: "127"),
            AttributeSchema("displayName", omSyntax: "64")
        };

        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) =>
            {
                var search = (SearchRequest)request;
                var filter = (string)search.Filter;
                if (search.Scope == SearchScope.Base && search.Attributes.Contains("schemaNamingContext"))
                    return LdapTestResponses.SearchResponseWith("", ("schemaNamingContext", SchemaNamingContext));

                if (filter.Contains("objectClassCategory"))
                    return LdapTestResponses.SearchResponseWithEntries(userClass);

                if (filter.Contains("objectClass=classSchema"))
                    return LdapTestResponses.SearchResponseWithEntries(userClass, topClass);

                if (filter.Contains("objectClass=attributeSchema"))
                    return LdapTestResponses.SearchResponseWithEntries(attributes);

                return LdapTestResponses.EmptySearchResponse();
            });

        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };
        return await new LdapConnectorSchema(executor.Object, Logger, rootDse).GetSchemaAsync();
    }

    private static SearchResultEntry AttributeSchema(string ldapDisplayName, string omSyntax) =>
        LdapTestResponses.Entry($"CN={ldapDisplayName},{SchemaNamingContext}",
            ("lDAPDisplayName", ldapDisplayName), ("isSingleValued", "TRUE"), ("oMSyntax", omSyntax), ("systemOnly", "FALSE"));

    private static async Task<ConnectorSchema> DiscoverRfc4512Async()
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
                        ("objectClasses",
                        [
                            "( 2.5.6.0 NAME 'top' ABSTRACT MUST objectClass )",
                            "( 2.16.840.1.113730.3.2.2 NAME 'inetOrgPerson' SUP top STRUCTURAL MAY ( manager $ displayName ) )"
                        ]),
                        ("attributeTypes",
                        [
                            "( 2.5.4.0 NAME 'objectClass' SYNTAX 1.3.6.1.4.1.1466.115.121.1.38 )",
                            "( 0.9.2342.19200300.100.1.10 NAME 'manager' SYNTAX 1.3.6.1.4.1.1466.115.121.1.12 )",
                            "( 2.16.840.1.113730.3.1.241 NAME 'displayName' SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 SINGLE-VALUE )"
                        ])));

                return LdapTestResponses.EmptySearchResponse();
            });

        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };
        return await new LdapConnectorSchema(executor.Object, Logger, rootDse).GetSchemaAsync();
    }
}
