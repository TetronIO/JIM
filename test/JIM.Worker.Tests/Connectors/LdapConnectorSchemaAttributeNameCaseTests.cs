// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Active Directory attribute names ignore case (#2051): a class that lists an attribute its parent class already
/// listed, in a different case, contributes the same attribute, not a second one.
/// </summary>
[TestFixture]
public class LdapConnectorSchemaAttributeNameCaseTests
{
    private const string SchemaNamingContext = "CN=Schema,CN=Configuration,DC=corp,DC=local";
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    [Test]
    public async Task GetSchemaAsync_ActiveDirectory_AttributeListedByTwoClassesInDifferentCase_IsDiscoveredOnceAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) =>
            {
                var search = (SearchRequest)request;
                if (search.Scope == SearchScope.Base && search.Attributes.Contains("schemaNamingContext"))
                    return LdapTestResponses.SearchResponseWith("", ("schemaNamingContext", SchemaNamingContext));

                if (((string)search.Filter).Contains("objectClassCategory"))
                    return LdapTestResponses.SearchResponseWithEntries(UserClass());

                if (((string)search.Filter).Contains("objectClass=classSchema"))
                    return LdapTestResponses.SearchResponseWithEntries(UserClass(), TopClass());

                if (((string)search.Filter).Contains("objectClass=attributeSchema"))
                    return LdapTestResponses.SearchResponseWithEntries(AttributeEntry("cn"), AttributeEntry("sAMAccountName"));

                return LdapTestResponses.EmptySearchResponse();
            });

        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };
        var schema = await new LdapConnectorSchema(executor.Object, Logger, rootDse).GetSchemaAsync();

        var user = schema.ObjectTypes.Single(objectType => objectType.Name == "user");
        Assert.That(user.Attributes.Count(a => string.Equals(a.Name, "sAMAccountName", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1));
    }

    private static SearchResultEntry UserClass() => LdapTestResponses.Entry($"CN=User,{SchemaNamingContext}",
        ("name", "user"), ("lDAPDisplayName", "user"), ("objectClassCategory", "1"), ("subClassOf", "top"), ("mayContain", "sAMAccountName"));

    private static SearchResultEntry TopClass() => LdapTestResponses.Entry($"CN=Top,{SchemaNamingContext}",
        ("name", "top"), ("lDAPDisplayName", "top"), ("objectClassCategory", "2"), ("subClassOf", "top"), ("systemMayContain", "cn"), ("mayContain", "samaccountname"));

    private static SearchResultEntry AttributeEntry(string name) => LdapTestResponses.Entry($"CN={name},{SchemaNamingContext}",
        ("lDAPDisplayName", name), ("isSingleValued", "TRUE"), ("oMSyntax", "64"), ("systemOnly", "FALSE"));
}
