// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using Moq;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Schema discovery states the step of time each date attribute is held to (#2153, decision D2): Active Directory and
/// Samba AD hold GeneralizedTime and UTC-Time to whole seconds; an RFC 4512 directory holds GeneralizedTime to the
/// microsecond JIM holds, and UTC Time to whole seconds.
/// </summary>
[TestFixture]
public class LdapConnectorSchemaDateTimeResolutionTests
{
    private const string SchemaNamingContext = "CN=Schema,CN=Configuration,DC=corp,DC=local";
    private const string SubschemaDn = "cn=Subschema";
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    [TestCase(false)]
    [TestCase(true)]
    public async Task GetSchemaAsync_ActiveDirectoryFamily_HoldsDatesToTheSecondAsync(bool samba)
    {
        var schema = await DiscoverActiveDirectoryAsync(samba ? LdapDirectoryType.SambaAD : LdapDirectoryType.ActiveDirectory,
            AttributeSchema("whenChanged", omSyntax: 24),
            AttributeSchema("meetingStartTime", omSyntax: 23),
            AttributeSchema("description", omSyntax: 64));

        var user = schema.ObjectTypes.Single(t => t.Name == "user");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Resolution(user, "whenChanged"), Is.EqualTo(DateTimeResolution.Second));
            Assert.That(Resolution(user, "meetingStartTime"), Is.EqualTo(DateTimeResolution.Second));
            Assert.That(Resolution(user, "description"), Is.Null, "Only a date carries a resolution.");
        }
    }

    [Test]
    public async Task GetSchemaAsync_Rfc4512_HoldsGeneralizedTimeToTheMicrosecondAndUtcTimeToTheSecondAsync()
    {
        var schema = await DiscoverRfc4512Async(
            "( 2.5.4.0 NAME 'objectClass' EQUALITY objectIdentifierMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.38 )",
            "( 0.9.2342.19200300.100.1.1 NAME 'uid' EQUALITY caseExactMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )",
            "( 1.1.1 NAME 'reviewDate' EQUALITY generalizedTimeMatch SYNTAX 1.3.6.1.4.1.1466.115.121.1.24 )",
            "( 1.1.2 NAME 'nextReviewDate' SUP reviewDate )",
            "( 1.1.3 NAME 'legacyDate' SYNTAX 1.3.6.1.4.1.1466.115.121.1.53 )");

        var account = schema.ObjectTypes.Single(t => t.Name == "account");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Resolution(account, "reviewDate"), Is.EqualTo(DateTimeResolution.Microsecond));
            Assert.That(Resolution(account, "nextReviewDate"), Is.EqualTo(DateTimeResolution.Microsecond), "A subtype inherits its supertype's SYNTAX.");
            Assert.That(Resolution(account, "legacyDate"), Is.EqualTo(DateTimeResolution.Second), "UTC Time holds whole seconds at most.");
            Assert.That(Resolution(account, "uid"), Is.Null);
        }
    }

    private static DateTimeResolution? Resolution(ConnectorSchemaObjectType objectType, string attributeName) =>
        objectType.Attributes.Single(a => a.Name.Equals(attributeName, StringComparison.OrdinalIgnoreCase)).DateTimeResolution;

    private static SearchResultEntry AttributeSchema(string name, int omSyntax) =>
        LdapTestResponses.Entry($"CN={name},{SchemaNamingContext}",
            ("lDAPDisplayName", name), ("isSingleValued", "TRUE"), ("oMSyntax", omSyntax.ToString()), ("systemOnly", "FALSE"));

    private static async Task<ConnectorSchema> DiscoverActiveDirectoryAsync(LdapDirectoryType directoryType, params SearchResultEntry[] attributes)
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

        var rootDse = new LdapConnectorRootDse { DirectoryType = directoryType };
        return await new LdapConnectorSchema(executor.Object, Logger, rootDse).GetSchemaAsync();
    }

    private static async Task<ConnectorSchema> DiscoverRfc4512Async(params string[] attributeTypes)
    {
        string[] objectClasses =
        [
            "( 2.5.6.0 NAME 'top' ABSTRACT MUST objectClass )",
            "( 0.9.2342.19200300.100.4.5 NAME 'account' SUP top STRUCTURAL MUST uid MAY ( reviewDate $ nextReviewDate $ legacyDate ) )"
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
