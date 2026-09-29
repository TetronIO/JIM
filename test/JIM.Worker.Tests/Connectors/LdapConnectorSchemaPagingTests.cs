// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Active Directory schema discovery reads its classSchema and attributeSchema caches page by page (#1853). A stock
/// forest has more attributeSchema entries than the directory answers in one unpaged search, so a cache read in one
/// request stops at the directory's MaxPageSize; here the second page holds what the first object type needs, so a
/// discovery that only reads the first page has nothing to show for it.
/// </summary>
[TestFixture]
public class LdapConnectorSchemaPagingTests
{
    private const string SchemaNamingContext = "CN=Schema,CN=Configuration,DC=corp,DC=local";
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    [Test]
    public async Task GetSchemaAsync_ActiveDirectory_ReadsEveryPageOfTheClassAndAttributeCachesAsync()
    {
        var continuedRequests = 0;
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) =>
            {
                var search = (SearchRequest)request;
                var continuing = search.Controls.OfType<PageResultRequestControl>().SingleOrDefault()?.Cookie is { Length: > 0 };
                if (continuing)
                    continuedRequests++;

                if (search.Scope == SearchScope.Base && search.Attributes.Contains("schemaNamingContext"))
                    return LdapTestResponses.SearchResponseWith("", ("schemaNamingContext", SchemaNamingContext));

                // The object type list: structural classes only, one page.
                if (((string)search.Filter).Contains("objectClassCategory"))
                    return LdapTestResponses.SearchResponseWithEntries(UserClass());

                // The class cache: 'user' on the first page, its parent 'top' only on the second.
                if (((string)search.Filter).Contains("objectClass=classSchema"))
                    return continuing
                        ? LdapTestResponses.SearchResponseWithPagingCookie([], TopClass())
                        : LdapTestResponses.SearchResponseWithPagingCookie([1], UserClass());

                // The attribute cache: sAMAccountName on the first page, cn only on the second.
                if (((string)search.Filter).Contains("objectClass=attributeSchema"))
                    return continuing
                        ? LdapTestResponses.SearchResponseWithPagingCookie([], AttributeEntry("cn"))
                        : LdapTestResponses.SearchResponseWithPagingCookie([1], AttributeEntry("sAMAccountName"));

                return LdapTestResponses.EmptySearchResponse();
            });

        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.ActiveDirectory };
        var schema = await new LdapConnectorSchema(executor.Object, Logger, rootDse).GetSchemaAsync();

        var user = schema.ObjectTypes.SingleOrDefault(objectType => objectType.Name == "user");
        Assert.That(user, Is.Not.Null, "user needs its parent class 'top', which is on the second page of the class cache.");
        Assert.Multiple(() =>
        {
            Assert.That(user!.Attributes.Select(attribute => attribute.Name), Is.EquivalentTo(new[] { "cn", "sAMAccountName" }),
                "cn is on the second page of the attribute cache.");
            Assert.That(continuedRequests, Is.EqualTo(2), "Both caches have a second page to fetch.");
        });
    }

    private static SearchResultEntry UserClass() => LdapTestResponses.Entry($"CN=User,{SchemaNamingContext}",
        ("name", "user"), ("lDAPDisplayName", "user"), ("objectClassCategory", "1"), ("subClassOf", "top"), ("mayContain", "sAMAccountName"));

    private static SearchResultEntry TopClass() => LdapTestResponses.Entry($"CN=Top,{SchemaNamingContext}",
        ("name", "top"), ("lDAPDisplayName", "top"), ("objectClassCategory", "2"), ("subClassOf", "top"), ("systemMayContain", "cn"));

    private static SearchResultEntry AttributeEntry(string name) => LdapTestResponses.Entry($"CN={name},{SchemaNamingContext}",
        ("lDAPDisplayName", name), ("isSingleValued", "TRUE"), ("oMSyntax", "64"), ("systemOnly", "FALSE"));
}
