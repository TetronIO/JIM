// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// An import converts an entry whose multi-valued attribute the directory answered in ranges (#1853): the
/// range-qualified description resolves to the schema attribute it belongs to, and every range's values reach the
/// import object under the plain name.
/// </summary>
[TestFixture]
public class LdapConnectorImportRangedAttributeTests
{
    private const string GroupDn = "CN=Everyone,OU=Groups,DC=corp,DC=local";
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    [Test]
    public void ConvertEntries_GroupAnsweredInRanges_ImportsEveryMemberUnderThePlainAttributeName()
    {
        var imported = ConvertGroup(("member;range=0-1", ["CN=a", "CN=b"]));

        Assert.That(imported.ErrorType, Is.Null, imported.ErrorMessage);
        var member = imported.Attributes.SingleOrDefault(attribute => attribute.Name == "member");
        Assert.That(member, Is.Not.Null, "The ranged attribute must be imported under its plain name.");
        Assert.That(member!.ReferenceValues, Is.EqualTo(new[] { "CN=a", "CN=b", "CN=c", "CN=d" }));
    }

    [Test]
    public void ConvertEntries_GroupAnsweredInRangesBesideAnEmptyPlainMember_ImportsOneMemberAttributeWithEveryValue()
    {
        // A directory may answer a group over MaxValRange with an empty plain member beside member;range=0-1499
        // (#2041); the empty one must neither stand in for the ranged one nor be imported as a second member.
        var imported = ConvertGroup(("member", []), ("member;range=0-1", ["CN=a", "CN=b"]));

        Assert.That(imported.ErrorType, Is.Null, imported.ErrorMessage);
        var members = imported.Attributes.Where(attribute => attribute.Name == "member").ToList();
        Assert.That(members, Has.Count.EqualTo(1), "One directory attribute must import as one attribute.");
        Assert.That(members[0].ReferenceValues, Is.EqualTo(new[] { "CN=a", "CN=b", "CN=c", "CN=d" }));
    }

    private static ConnectedSystemImportObject ConvertGroup(params (string Name, string[] Values)[] memberAttributes)
    {
        var groupType = new ConnectedSystemObjectType { Id = 1, Name = "group", Selected = true };
        groupType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "objectClass", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued, Selected = true, ConnectedSystemObjectType = groupType });
        groupType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "cn", Type = AttributeDataType.Text, Selected = true, ConnectedSystemObjectType = groupType });
        groupType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, Selected = true, ConnectedSystemObjectType = groupType });
        var connectedSystem = new ConnectedSystem
        {
            Name = "Active Directory",
            ObjectTypes = [groupType],
            Partitions = [new ConnectedSystemPartition { ExternalId = "DC=corp,DC=local", Name = "corp", Selected = true, Containers = [] }]
        };
        var import = new LdapConnectorImport(connectedSystem, new ConnectedSystemRunProfile { Name = "Full Import", RunType = ConnectedSystemRunType.FullImport },
            new LdapConnection("localhost"), null, 1, [], null, null, "dc1.corp.local", _ => true, Logger, CancellationToken.None, new RecordingConnectorProgress());

        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>()))
            .Returns((DirectoryRequest request, TimeSpan _) =>
            {
                var search = (SearchRequest)request;
                Assert.That(search.Attributes[0], Is.EqualTo("member;range=2-*"));
                return LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.EntryWithValues(GroupDn, ("member;range=2-*", ["CN=c", "CN=d"])));
            });
        import.Executor = executor.Object;

        (string Name, string[] Values)[] attributes = [("objectClass", ["top", "group"]), ("cn", ["Everyone"]), .. memberAttributes];
        var entries = LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.EntryWithValues(GroupDn, attributes)).Entries;

        return ((ILdapDeltaImportHost)import).ConvertEntries(entries, ObjectChangeType.NotSet, groupType).Single();
    }
}
