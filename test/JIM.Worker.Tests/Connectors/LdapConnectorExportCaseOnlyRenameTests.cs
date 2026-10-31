// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A change of case to an object's name reaches the directory as a rename (#2051). A distinguished name is compared
/// ignoring case, because the directory owns the case of its parent components, so a case-only change is driven by the
/// object's own relative name: the RDN attribute (<c>cn</c>, compared exactly), or the leaf of a staged DN whose parent
/// is unchanged. Runs on the sequential (concurrency 1) and parallel paths, which carry separate copies of the logic.
/// </summary>
[TestFixture]
public class LdapConnectorExportCaseOnlyRenameTests
{
    private const string CurrentDn = "CN=joe bloggs,OU=Users,DC=panoply,DC=local";

    private Mock<ILdapOperationExecutor> _executor = null!;
    private readonly List<ModifyDNRequest> _renames = [];
    private readonly List<ModifyRequest> _modifies = [];

    [SetUp]
    public void SetUp()
    {
        _renames.Clear();
        _modifies.Clear();
        _executor = new Mock<ILdapOperationExecutor>();
        _executor.Setup(e => e.SendRequest(It.IsAny<ModifyDNRequest>()))
            .Callback<DirectoryRequest>(request => _renames.Add((ModifyDNRequest)request))
            .Returns(() => LdapTestResponses.Create<ModifyDNResponse>(ResultCode.Success));
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<ModifyDNRequest>()))
            .Callback<DirectoryRequest>(request => _renames.Add((ModifyDNRequest)request))
            .ReturnsAsync(() => LdapTestResponses.Create<ModifyDNResponse>(ResultCode.Success));
        _executor.Setup(e => e.SendRequest(It.IsAny<ModifyRequest>()))
            .Callback<DirectoryRequest>(request => _modifies.Add((ModifyRequest)request))
            .Returns(() => LdapTestResponses.Create<ModifyResponse>(ResultCode.Success));
        _executor.Setup(e => e.SendRequestAsync(It.IsAny<ModifyRequest>()))
            .Callback<DirectoryRequest>(request => _modifies.Add((ModifyRequest)request))
            .ReturnsAsync(() => LdapTestResponses.Create<ModifyResponse>(ResultCode.Success));
    }

    [TestCase(1)]
    [TestCase(4)]
    public async Task Export_RdnAttributeChangesOnlyInCase_RenamesTheObjectInPlaceAsync(int concurrency)
    {
        var result = await ExportAsync(concurrency, cn: "Joe Bloggs");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True);
            Assert.That(_renames.Select(r => (r.NewName, r.NewParentDistinguishedName)), Is.EqualTo(new[] { ("CN=Joe Bloggs", "OU=Users,DC=panoply,DC=local") }));
            Assert.That(result.SecondaryExternalId, Is.EqualTo("CN=Joe Bloggs,OU=Users,DC=panoply,DC=local"));
            Assert.That(_modifies.SelectMany(m => m.Modifications.Cast<DirectoryAttributeModification>()).Select(m => m.Name), Does.Not.Contain("cn"),
                "the rename carries the RDN attribute; a ModifyRequest for it would be refused");
        }
    }

    [TestCase(1)]
    [TestCase(4)]
    public async Task Export_StagedDnWhoseLeafChangesOnlyInCase_RenamesTheLeafAndLeavesTheParentAsync(int concurrency)
    {
        var result = await ExportAsync(concurrency, dn: "CN=Joe Bloggs,OU=Users,DC=PANOPLY,DC=LOCAL");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_renames.Select(r => (r.NewName, r.NewParentDistinguishedName)), Is.EqualTo(new[] { ("CN=Joe Bloggs", "OU=Users,DC=panoply,DC=local") }),
                "the parent stays as the directory holds it; only the object's own name is JIM's to change");
            Assert.That(result.SecondaryExternalId, Is.EqualTo("CN=Joe Bloggs,OU=Users,DC=panoply,DC=local"));
        }
    }

    [TestCase(1)]
    [TestCase(4)]
    public async Task Export_StagedDnDifferingOnlyInTheParentsCase_DoesNotRenameAsync(int concurrency)
    {
        await ExportAsync(concurrency, dn: "CN=joe bloggs,OU=USERS,DC=PANOPLY,DC=LOCAL");

        Assert.That(_renames, Is.Empty);
    }

    [TestCase(1)]
    [TestCase(4)]
    public async Task Export_RdnAttributeUnchanged_DoesNotRenameAsync(int concurrency)
    {
        await ExportAsync(concurrency, cn: "joe bloggs");

        Assert.That(_renames, Is.Empty);
    }

    [TestCase(1)]
    [TestCase(4)]
    public async Task Export_RdnAttributeValueWithSpecialCharacters_IsEscapedInTheNewRdnAsync(int concurrency)
    {
        await ExportAsync(concurrency, cn: "Bloggs, Joe", currentDn: "CN=bloggs\\, joe,OU=Users,DC=panoply,DC=local");

        Assert.That(_renames.Select(r => r.NewName), Is.EqualTo(new[] { "CN=Bloggs\\, Joe" }));
    }

    private async Task<ConnectedSystemExportResult> ExportAsync(int concurrency, string? dn = null, string? cn = null, string currentDn = CurrentDn)
    {
        var type = new ConnectedSystemObjectType { Name = "user" };
        var dnAttribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "distinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true, ConnectedSystemObjectType = type };
        var cnAttribute = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "cn", Type = AttributeDataType.Text, ConnectedSystemObjectType = type };
        var description = new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "description", Type = AttributeDataType.Text, ConnectedSystemObjectType = type };

        var changes = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = description, ChangeType = PendingExportAttributeChangeType.Update, StringValue = "Something" }
        };
        if (dn != null)
            changes.Add(new PendingExportAttributeValueChange { Attribute = dnAttribute, ChangeType = PendingExportAttributeChangeType.Update, StringValue = dn });
        if (cn != null)
            changes.Add(new PendingExportAttributeValueChange { Attribute = cnAttribute, ChangeType = PendingExportAttributeChangeType.Update, StringValue = cn });

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = type,
                SecondaryExternalIdAttributeId = dnAttribute.Id,
                AttributeValues = [new ConnectedSystemObjectAttributeValue { AttributeId = dnAttribute.Id, Attribute = dnAttribute, StringValue = currentDn }]
            },
            AttributeValueChanges = new(changes)
        };

        var settings = new List<ConnectedSystemSettingValue>
        {
            new() { Setting = new ConnectorDefinitionSetting { Name = "Delete Behaviour" }, StringValue = "Delete" }
        };
        var export = new LdapConnectorExport(_executor.Object, settings, Log.Logger, concurrency);
        var results = await export.ExecuteAsync([pendingExport], CancellationToken.None);
        return results.Single();
    }
}
