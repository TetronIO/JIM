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
/// A connector-defined attribute (an Exchange attribute, #2021) is never written to the directory: the declared recipient
/// state is the Exchange step's to act on, and the directory has no such attribute to write. An add or modify carries
/// every other change as before.
/// </summary>
[TestFixture]
public class LdapConnectorExportConnectorDefinedAttributeTests
{
    private const string UserDn = "CN=Alice Smith,OU=Users,DC=panoply,DC=local";
    private static readonly ConnectedSystemObjectType UserType = new() { Name = "user" };

    [Test]
    public void BuildAddRequestWithOverflow_CreateDeclaringAMailbox_SendsEverythingButTheExchangeAttributes()
    {
        var pendingExport = Export(PendingExportChangeType.Create, Change("sAMAccountName", "asmith"), Change("Exchange Recipient Type", "Mailbox", connectorDefined: true),
            Change("Exchange Alias", "alice.smith", connectorDefined: true));

        var (addRequest, overflow, _) = Exporter().BuildAddRequestWithOverflow(pendingExport, UserDn);

        var sent = addRequest.Attributes.Cast<DirectoryAttribute>().Select(a => a.Name).Concat(overflow.SelectMany(m => m.Modifications.Cast<DirectoryAttributeModification>().Select(x => x.Name)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent, Does.Contain("sAMAccountName"));
            Assert.That(sent, Does.Not.Contain("Exchange Recipient Type").And.Not.Contain("Exchange Alias"));
        }
    }

    [Test]
    public void ConsolidateModifications_UpdateChangingOnlyAnExchangeAttribute_HasNothingForTheDirectory()
    {
        var pendingExport = Export(PendingExportChangeType.Update, Change("Exchange Primary SMTP Address", "alice@panoply.local", connectorDefined: true));

        Assert.That(LdapConnectorExport.ConsolidateModifications(pendingExport, UserDn), Is.Empty);
    }

    [Test]
    public void ConsolidateModifications_UpdateChangingBoth_SendsOnlyTheDirectoryAttribute()
    {
        var pendingExport = Export(PendingExportChangeType.Update, Change("department", "Finance"), Change("Exchange Alias", "alice.smith", connectorDefined: true));

        Assert.That(LdapConnectorExport.ConsolidateModifications(pendingExport, UserDn).Select(m => m.AttributeName), Is.EqualTo(new[] { "department" }));
    }

    private static LdapConnectorExport Exporter() =>
        new(new Mock<ILdapOperationExecutor>().Object,
            [new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = "Delete Behaviour" }, StringValue = "Delete" }],
            Log.Logger, 1, LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE, LdapDirectoryType.ActiveDirectory, placeholderMemberDn: null);

    private static PendingExport Export(PendingExportChangeType changeType, params PendingExportAttributeValueChange[] changes) => new()
    {
        Id = Guid.NewGuid(),
        ChangeType = changeType,
        ConnectedSystemObject = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = UserType },
        AttributeValueChanges =
        [
            new PendingExportAttributeValueChange
            {
                Attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = UserType },
                ChangeType = PendingExportAttributeChangeType.Add,
                StringValue = UserDn
            },
            .. changes
        ]
    };

    private static int _nextAttributeId = 10;

    private static PendingExportAttributeValueChange Change(string attributeName, string value, bool connectorDefined = false) => new()
    {
        Attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = Interlocked.Increment(ref _nextAttributeId), Name = attributeName, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, IsConnectorDefined = connectorDefined, ConnectedSystemObjectType = UserType
        },
        ChangeType = PendingExportAttributeChangeType.Update,
        StringValue = value
    };
}
