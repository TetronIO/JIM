// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using Moq;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Schema discovery records every class an Object Type inherits from (#2043), so an entry carrying two selected
/// classes resolves to the more specific one whatever order the directory lists them in. Both discovery paths know
/// the chain already, because they walk it to collect inherited attributes: Active Directory through each
/// classSchema entry's subClassOf, an RFC 4512 directory through each class's SUP.
/// </summary>
[TestFixture]
public class LdapConnectorSchemaSuperiorClassTests
{
    private const string SchemaNamingContext = "CN=Schema,CN=Configuration,DC=corp,DC=local";
    private const string SubschemaDn = "cn=Subschema";
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    [Test]
    public async Task GetSchemaAsync_ActiveDirectory_RecordsEveryClassUpTheChainAsync()
    {
        var schema = await DiscoverActiveDirectoryAsync(
            ClassSchema("user", "organizationalPerson", category: "1"),
            ClassSchema("organizationalPerson", "person", category: "1"),
            ClassSchema("person", "top", category: "1"),
            ClassSchema("top", "top", category: "2"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SuperiorClasses(schema, "user"), Is.EquivalentTo(new[] { "organizationalPerson", "person", "top" }));
            Assert.That(SuperiorClasses(schema, "person"), Is.EquivalentTo(new[] { "top" }));
        }
    }

    [Test]
    public async Task GetSchemaAsync_ActiveDirectory_TopInheritsFromNothingAsync()
    {
        // top names itself as its own subClassOf, which is how the walk knows it has reached the end.
        var schema = await DiscoverActiveDirectoryAsync(ClassSchema("top", "top", category: "1"));

        Assert.That(SuperiorClasses(schema, "top"), Is.Empty);
    }

    [Test]
    public async Task GetSchemaAsync_ActiveDirectoryWithACycleInTheChain_StopsRatherThanLoopingAsync()
    {
        // No domain controller should publish this; the walk used to loop for ever on it.
        var schema = await DiscoverActiveDirectoryAsync(
            ClassSchema("alpha", "beta", category: "1"),
            ClassSchema("beta", "alpha", category: "1"));

        Assert.That(SuperiorClasses(schema, "alpha"), Is.EquivalentTo(new[] { "beta" }));
    }

    [Test]
    public async Task GetSchemaAsync_Rfc4512_RecordsEveryClassUpTheChainAsync()
    {
        var schema = await DiscoverRfc4512Async(
            "( 2.5.6.0 NAME 'top' ABSTRACT MUST objectClass )",
            "( 2.5.6.6 NAME 'person' SUP top STRUCTURAL MUST ( sn $ cn ) )",
            "( 2.5.6.7 NAME 'organizationalPerson' SUP person STRUCTURAL MAY title )",
            "( 2.16.840.1.113730.3.2.2 NAME 'inetOrgPerson' SUP organizationalPerson STRUCTURAL MAY mail )",
            "( 1.3.6.1.1.1.2.0 NAME 'posixAccount' SUP top AUXILIARY MAY uid )");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SuperiorClasses(schema, "inetOrgPerson"), Is.EquivalentTo(new[] { "organizationalPerson", "person", "top" }));
            Assert.That(SuperiorClasses(schema, "person"), Is.EquivalentTo(new[] { "top" }));
            Assert.That(SuperiorClasses(schema, "posixAccount"), Is.EquivalentTo(new[] { "top" }));
        }
    }

    [Test]
    public async Task GetSchemaAsync_Rfc4512WithACycleInTheChain_StopsRatherThanLoopingAsync()
    {
        // No directory should publish this, but a hand-edited schema can, and discovery must still finish.
        var schema = await DiscoverRfc4512Async(
            "( 1.1 NAME 'alpha' SUP beta STRUCTURAL MAY cn )",
            "( 1.2 NAME 'beta' SUP alpha STRUCTURAL MAY cn )");

        Assert.That(SuperiorClasses(schema, "alpha"), Is.EquivalentTo(new[] { "beta" }));
    }

    [Test]
    public async Task GetSchemaAsync_Rfc4512WithASuperiorTheSchemaDoesNotDefine_RecordsItAndStopsAsync()
    {
        var schema = await DiscoverRfc4512Async("( 1.1 NAME 'alpha' SUP undefinedClass STRUCTURAL MAY cn )");

        Assert.That(SuperiorClasses(schema, "alpha"), Is.EquivalentTo(new[] { "undefinedClass" }));
    }

    private static IEnumerable<string> SuperiorClasses(ConnectorSchema schema, string objectTypeName)
    {
        var objectType = schema.ObjectTypes.SingleOrDefault(type => type.Name.Equals(objectTypeName, StringComparison.OrdinalIgnoreCase));
        Assert.That(objectType, Is.Not.Null, $"{objectTypeName} was not discovered.");
        return objectType!.Tags.Where(tag => tag.Key == ObjectTypeTags.Keys.SuperiorClass).Select(tag => tag.Value).ToList();
    }

    private static async Task<ConnectorSchema> DiscoverActiveDirectoryAsync(params SearchResultEntry[] classes)
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) =>
            {
                var search = (SearchRequest)request;
                var filter = (string)search.Filter;
                if (search.Scope == SearchScope.Base && search.Attributes.Contains("schemaNamingContext"))
                    return LdapTestResponses.SearchResponseWith("", ("schemaNamingContext", SchemaNamingContext));

                // The object type list: the structural classes.
                if (filter.Contains("objectClassCategory"))
                    return LdapTestResponses.SearchResponseWithEntries(classes.Where(entry => (string)entry.Attributes["objectClassCategory"][0] == "1").ToArray());

                if (filter.Contains("objectClass=classSchema"))
                    return LdapTestResponses.SearchResponseWithEntries(classes);

                if (filter.Contains("objectClass=attributeSchema"))
                    return LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.Entry($"CN=cn,{SchemaNamingContext}",
                        ("lDAPDisplayName", "cn"), ("isSingleValued", "TRUE"), ("oMSyntax", "64"), ("systemOnly", "FALSE")));

                return LdapTestResponses.EmptySearchResponse();
            });

        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };
        return await new LdapConnectorSchema(executor.Object, Logger, rootDse).GetSchemaAsync();
    }

    private static SearchResultEntry ClassSchema(string ldapDisplayName, string subClassOf, string category) =>
        LdapTestResponses.Entry($"CN={ldapDisplayName},{SchemaNamingContext}",
            ("name", ldapDisplayName), ("lDAPDisplayName", ldapDisplayName), ("objectClassCategory", category),
            ("subClassOf", subClassOf), ("systemMayContain", "cn"));

    private static async Task<ConnectorSchema> DiscoverRfc4512Async(params string[] objectClasses)
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
                        ("objectClasses", objectClasses),
                        ("attributeTypes",
                        [
                            "( 2.5.4.0 NAME 'objectClass' SYNTAX 1.3.6.1.4.1.1466.115.121.1.38 )",
                            "( 2.5.4.3 NAME 'cn' SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
                            "( 2.5.4.4 NAME 'sn' SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
                            "( 2.5.4.12 NAME 'title' SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
                            "( 0.9.2342.19200300.100.1.3 NAME 'mail' SYNTAX 1.3.6.1.4.1.1466.115.121.1.26 )",
                            "( 0.9.2342.19200300.100.1.1 NAME 'uid' SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )"
                        ])));

                return LdapTestResponses.EmptySearchResponse();
            });

        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };
        return await new LdapConnectorSchema(executor.Object, Logger, rootDse, includeAuxiliaryClasses: true).GetSchemaAsync();
    }
}
