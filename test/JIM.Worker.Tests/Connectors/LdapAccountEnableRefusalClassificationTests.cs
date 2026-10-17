// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Moq;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Active Directory refuses to create or enable a user account that has no password, answering
/// <c>0000052D</c> (ERROR_PASSWORD_RESTRICTIONS) with WILL_NOT_PERFORM (#2039). The LDAP Connector classifies that
/// refusal as <see cref="ConnectedSystemExportErrorType.PasswordRequiredToEnableAccount"/>, so it reaches the
/// administrator as configuration to act on rather than as an unhandled error that reads as a JIM defect.
/// <para>
/// The refusal arrives in two shapes: the library's <see cref="DirectoryOperationException"/> carrying the response,
/// and the <see cref="LdapException"/> the Connector raises itself from a non-success response. Both are classified.
/// The message fixture is the documented shape of Windows Server's refusal: the extended error code, DSID and problem.
/// </para>
/// </summary>
[TestFixture]
public class LdapAccountEnableRefusalClassificationTests
{
    private const string ActiveDirectoryRefusal = "0000052D: SvcErr: DSID-031A12C5, problem 5003 (WILL_NOT_PERFORM), data 0\n";
    private const string UserDn = "CN=Joe Bloggs,OU=Users,DC=corp,DC=local";

    [Test]
    public async Task ExecuteAsync_CreateRefusedWithDirectoryOperationException_IsClassifiedAsPasswordRequiredToEnableAccountAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<AddResponse>(ResultCode.UnwillingToPerform, ActiveDirectoryRefusal)));

        var results = await Export(executor, LdapDirectoryType.ActiveDirectory).ExecuteAsync([EnabledUserCreate()], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.PasswordRequiredToEnableAccount));
            Assert.That(results[0].ErrorMessage, Does.Contain("0000052D: SvcErr: DSID-031A12C5"),
                "the directory's own words are kept, so an administrator can see what it said");
        }
    }

    [Test]
    public async Task ExecuteAsync_CreateRefusedWithAResponseTheConnectorRaises_IsClassifiedAsPasswordRequiredToEnableAccountAsync()
    {
        // A non-success AddResponse is turned into an LdapException by the Connector itself.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Returns(LdapTestResponses.Create<AddResponse>(ResultCode.UnwillingToPerform, ActiveDirectoryRefusal));

        var results = await Export(executor, LdapDirectoryType.ActiveDirectory).ExecuteAsync([EnabledUserCreate()], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.PasswordRequiredToEnableAccount));
            Assert.That(results[0].ErrorMessage, Does.Contain("0000052D"));
        }
    }

    [Test]
    public async Task ExecuteAsync_CreateRefusedOnTheConcurrentPath_IsClassifiedTheSameWayAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequestAsync(It.IsAny<AddRequest>()))
            .ThrowsAsync(new DirectoryOperationException(LdapTestResponses.Create<AddResponse>(ResultCode.UnwillingToPerform, ActiveDirectoryRefusal)));

        var results = await Export(executor, LdapDirectoryType.ActiveDirectory, concurrency: 4)
            .ExecuteAsync([EnabledUserCreate(), EnabledUserCreate("CN=Ada Lovelace,OU=Users,DC=corp,DC=local")], CancellationToken.None);

        Assert.That(results.Select(r => r.ErrorType), Is.All.EqualTo(ConnectedSystemExportErrorType.PasswordRequiredToEnableAccount));
    }

    [Test]
    public async Task ExecuteAsync_UpdateCarryingUserAccountControlRefused_IsClassifiedAsPasswordRequiredToEnableAccountAsync()
    {
        // Enabling an existing account that still has no password: the Update that writes the enabled value.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<ModifyRequest>()))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<ModifyResponse>(ResultCode.UnwillingToPerform, ActiveDirectoryRefusal)));

        var results = await Export(executor, LdapDirectoryType.ActiveDirectory).ExecuteAsync([UserUpdate("userAccountControl", intValue: 512)], CancellationToken.None);

        Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.PasswordRequiredToEnableAccount));

        // No password is owed to the account (the export is not flagged), so holding the enable back would leave it
        // disabled for good: the refusal is reported, with its remedies, and the modify is never sent again (#2158).
        executor.Verify(e => e.SendRequest(It.IsAny<ModifyRequest>()), Times.Once);
    }

    /// <summary>
    /// A flagged Update is sent again disabled only when Active Directory refused it for want of a password (#2158). The
    /// same code on another refusal, or any refusal outside the Active Directory family, is reported as it always was,
    /// and the modify is sent once.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_FlaggedUpdateRefusedForAnotherReason_IsNotResentAndStaysAnOrdinaryRefusalAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<ModifyRequest>()))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<ModifyResponse>(ResultCode.UnwillingToPerform,
                "00002035: SvcErr: DSID-03152E29, problem 5003 (WILL_NOT_PERFORM), data 0\n")));

        var results = await Export(executor, LdapDirectoryType.ActiveDirectory)
            .ExecuteAsync([UserUpdate("userAccountControl", intValue: 512, initialPasswordFollows: true)], CancellationToken.None);

        // Still the directory refusing the change (#2118), just not for the want of a password.
        Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.RejectedByConnectedSystem));
        executor.Verify(e => e.SendRequest(It.IsAny<ModifyRequest>()), Times.Once);
    }

    [Test]
    public async Task ExecuteAsync_FlaggedUpdateRefusedOutsideTheActiveDirectoryFamily_IsNotResentAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<ModifyRequest>()))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<ModifyResponse>(ResultCode.UnwillingToPerform, ActiveDirectoryRefusal)));

        var results = await Export(executor, LdapDirectoryType.OpenLDAP)
            .ExecuteAsync([UserUpdate("userAccountControl", intValue: 512, initialPasswordFollows: true)], CancellationToken.None);

        // Still the directory refusing the change (#2118), just not for the want of a password.
        Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.RejectedByConnectedSystem));
        executor.Verify(e => e.SendRequest(It.IsAny<ModifyRequest>()), Times.Once);
    }

    /// <summary>
    /// The refusal's message names every remedy, including the one for an account that already exists: set a password
    /// on it with the set-password action, enabling it there.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_UpdateRefused_NamesSettingAPasswordWithTheSetPasswordActionAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<ModifyRequest>()))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<ModifyResponse>(ResultCode.UnwillingToPerform, ActiveDirectoryRefusal)));

        var results = await Export(executor, LdapDirectoryType.ActiveDirectory).ExecuteAsync([UserUpdate("userAccountControl", intValue: 512)], CancellationToken.None);

        Assert.That(results[0].ErrorMessage, Does.Contain("set-password action"));
    }

    [Test]
    public async Task ExecuteAsync_UpdateCarryingUserAccountControlRefusedWithAResponseTheConnectorRaises_IsClassifiedAsPasswordRequiredToEnableAccountAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<ModifyRequest>()))
            .Returns(LdapTestResponses.Create<ModifyResponse>(ResultCode.UnwillingToPerform, ActiveDirectoryRefusal));

        var results = await Export(executor, LdapDirectoryType.ActiveDirectory).ExecuteAsync([UserUpdate("userAccountControl", intValue: 512)], CancellationToken.None);

        Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.PasswordRequiredToEnableAccount));
    }

    [Test]
    public async Task ExecuteAsync_UpdateNotCarryingUserAccountControl_StaysAnOrdinaryRefusalAsync()
    {
        // The same code on an Update that does not touch the account's enabled state is some other password
        // restriction; naming the enablement remedies would send an administrator to fix the wrong thing.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<ModifyRequest>()))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<ModifyResponse>(ResultCode.UnwillingToPerform, ActiveDirectoryRefusal)));

        var results = await Export(executor, LdapDirectoryType.ActiveDirectory).ExecuteAsync([UserUpdate("description", stringValue: "Contractor")], CancellationToken.None);

        // Still the directory refusing the change (#2118), just not for the want of a password.
        Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.RejectedByConnectedSystem));
    }

    [Test]
    public async Task ExecuteAsync_SambaAdCreateRefusedWithTheSameCode_IsClassifiedAsPasswordRequiredToEnableAccountAsync()
    {
        // Samba AD accepts an enabled account with no password today, but it speaks Active Directory's codes, so a
        // refusal in those terms means the same thing there.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<AddResponse>(ResultCode.UnwillingToPerform, ActiveDirectoryRefusal)));

        var results = await Export(executor, LdapDirectoryType.SambaAD).ExecuteAsync([EnabledUserCreate()], CancellationToken.None);

        Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.PasswordRequiredToEnableAccount));
    }

    [Test]
    public async Task ExecuteAsync_DirectoryOutsideTheActiveDirectoryFamily_IsNotClassifiedAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<AddResponse>(ResultCode.UnwillingToPerform, ActiveDirectoryRefusal)));

        var results = await Export(executor, LdapDirectoryType.OpenLDAP).ExecuteAsync([EnabledUserCreate("uid=jbloggs,ou=People,dc=yellowstone,dc=local")], CancellationToken.None);

        // Still the directory refusing the change (#2118), just not for the want of a password.
        Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.RejectedByConnectedSystem));
    }

    [Test]
    public async Task ExecuteAsync_CreateRefusedForAnotherReason_StaysAnOrdinaryRefusalAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<AddResponse>(ResultCode.UnwillingToPerform,
                "00002035: SvcErr: DSID-03152E29, problem 5003 (WILL_NOT_PERFORM), data 0\n")));

        var results = await Export(executor, LdapDirectoryType.ActiveDirectory).ExecuteAsync([EnabledUserCreate()], CancellationToken.None);

        // Still the directory refusing the change (#2118), just not for the want of a password.
        Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.RejectedByConnectedSystem));
    }

    private static LdapConnectorExport Export(Mock<ILdapOperationExecutor> executor, LdapDirectoryType directoryType, int concurrency = 1) =>
        new(executor.Object,
            [new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = "Delete Behaviour" }, StringValue = "Delete" }],
            Log.Logger,
            concurrency,
            LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE,
            directoryType,
            placeholderMemberDn: null);

    private static PendingExport EnabledUserCreate(string dn = UserDn)
    {
        var type = new ConnectedSystemObjectType { Name = "user" };
        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Create,
            ConnectedSystemObject = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = type },
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = type },
                    ChangeType = PendingExportAttributeChangeType.Add,
                    StringValue = dn
                },
                new PendingExportAttributeValueChange
                {
                    Attribute = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "userAccountControl", ConnectedSystemObjectType = type },
                    ChangeType = PendingExportAttributeChangeType.Add,
                    IntValue = 512
                }
            ]
        };
    }

    private static PendingExport UserUpdate(string attributeName, int? intValue = null, string? stringValue = null, bool initialPasswordFollows = false)
    {
        var type = new ConnectedSystemObjectType { Name = "user" };
        var dnAttribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = type };
        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            InitialPasswordFollows = initialPasswordFollows,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = type,
                SecondaryExternalIdAttributeId = dnAttribute.Id,
                AttributeValues = [new ConnectedSystemObjectAttributeValue { Attribute = dnAttribute, AttributeId = dnAttribute.Id, StringValue = UserDn }]
            },
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Attribute = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = attributeName, ConnectedSystemObjectType = type },
                    ChangeType = PendingExportAttributeChangeType.Update,
                    IntValue = intValue,
                    StringValue = stringValue
                }
            ]
        };
    }
}
