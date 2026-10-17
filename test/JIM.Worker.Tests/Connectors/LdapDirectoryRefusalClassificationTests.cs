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
/// An export the directory answered with a failing result code is the directory refusing the change, not a fault in
/// JIM (#2118). The LDAP Connector classifies it as <see cref="ConnectedSystemExportErrorType.AccessDenied"/> where the
/// bind account lacks the rights (insufficientAccessRights, 50), and as
/// <see cref="ConnectedSystemExportErrorType.RejectedByConnectedSystem"/> for every other refusal, so the administrator
/// reading the Activity is told what the directory said rather than that JIM failed.
/// <para>
/// What stays unclassified is what is not a refusal: a directory that was busy or unavailable, a connection that
/// failed before the directory answered, and any exception that did not come from the directory at all.
/// </para>
/// </summary>
[TestFixture]
public class LdapDirectoryRefusalClassificationTests
{
    private const string UserDn = "CN=Joe Bloggs,OU=Users,DC=corp,DC=local";
    private const string AccessDeniedMessage = "00002098: SecErr: DSID-03150F94, problem 4003 (INSUFF_ACCESS_RIGHTS), data 0\n";
    private const string ObjectClassViolationMessage = "00002014: objectclass_attrs: attribute 'sn' on entry is not a valid attribute\n";

    [Test]
    public async Task ExecuteAsync_InsufficientAccessRights_IsClassifiedAsAccessDeniedAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<AddResponse>(ResultCode.InsufficientAccessRights, AccessDeniedMessage)));

        var results = await Export(executor).ExecuteAsync([UserCreate()], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.AccessDenied));
            Assert.That(results[0].ErrorMessage, Does.Contain("INSUFF_ACCESS_RIGHTS"),
                "the directory's own words are kept, so an administrator can see what it said");
        }
    }

    [Test]
    public async Task ExecuteAsync_InsufficientAccessRightsInAResponseTheConnectorRaises_IsClassifiedAsAccessDeniedAsync()
    {
        // A non-success AddResponse is turned into an LdapException by the Connector itself.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Returns(LdapTestResponses.Create<AddResponse>(ResultCode.InsufficientAccessRights, AccessDeniedMessage));

        var results = await Export(executor).ExecuteAsync([UserCreate()], CancellationToken.None);

        Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.AccessDenied));
    }

    [Test]
    public async Task ExecuteAsync_InsufficientAccessRightsOnTheConcurrentPath_IsClassifiedTheSameWayAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequestAsync(It.IsAny<AddRequest>()))
            .ThrowsAsync(new DirectoryOperationException(LdapTestResponses.Create<AddResponse>(ResultCode.InsufficientAccessRights, AccessDeniedMessage)));

        var results = await Export(executor, concurrency: 2).ExecuteAsync([UserCreate()], CancellationToken.None);

        Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.AccessDenied));
    }

    [Test]
    public async Task ExecuteAsync_AccessDenied_NamesTheBindAccountsRightsAsTheRemedyAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<AddResponse>(ResultCode.InsufficientAccessRights, AccessDeniedMessage)));

        var results = await Export(executor).ExecuteAsync([UserCreate()], CancellationToken.None);

        Assert.That(results[0].ErrorMessage, Does.Contain("permission").IgnoreCase);
    }

    [TestCase(ResultCode.ObjectClassViolation)]
    [TestCase(ResultCode.ConstraintViolation)]
    [TestCase(ResultCode.UndefinedAttributeType)]
    [TestCase(ResultCode.InvalidAttributeSyntax)]
    [TestCase(ResultCode.NamingViolation)]
    [TestCase(ResultCode.UnwillingToPerform)]
    [TestCase(ResultCode.NoSuchObject)]
    public async Task ExecuteAsync_DirectoryRefusesTheChange_IsClassifiedAsRejectedByConnectedSystemAsync(ResultCode resultCode)
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<AddResponse>(resultCode, ObjectClassViolationMessage)));

        var results = await Export(executor).ExecuteAsync([UserCreate()], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.RejectedByConnectedSystem));
            Assert.That(results[0].ErrorMessage, Does.Contain("objectclass_attrs"),
                "the directory's own words are kept, so an administrator can see what it said");
        }
    }

    [TestCase(ResultCode.Busy)]
    [TestCase(ResultCode.Unavailable)]
    public async Task ExecuteAsync_DirectoryBusyOrUnavailable_IsNotClassifiedAsARefusalAsync(ResultCode resultCode)
    {
        // The directory did not refuse the change; it could not consider it. Nothing about the change is wrong.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<AddResponse>(resultCode, "server is busy")));

        var results = await Export(executor).ExecuteAsync([UserCreate()], CancellationToken.None);

        Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.General));
    }

    [Test]
    public async Task ExecuteAsync_ConnectionFailsBeforeTheDirectoryAnswers_IsNotClassifiedAsARefusalAsync()
    {
        // 81 (LDAP_SERVER_DOWN) is the client library's code, not a result the directory returned.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Throws(new LdapException(81, "The LDAP server is unavailable."));

        var results = await Export(executor).ExecuteAsync([UserCreate()], CancellationToken.None);

        Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.General));
    }

    [Test]
    public async Task ExecuteAsync_ExceptionNotFromTheDirectory_IsNotClassifiedAsARefusalAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Throws(new InvalidOperationException("Something inside the Connector went wrong"));

        var results = await Export(executor).ExecuteAsync([UserCreate()], CancellationToken.None);

        Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.General));
    }

    [Test]
    public async Task ExecuteAsync_ValueAlreadyInUse_KeepsItsMoreSpecificClassificationAsync()
    {
        // A uniqueness rejection is a refusal too, but its own classification carries more: the attribute in use.
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>()))
            .Throws(new DirectoryOperationException(LdapTestResponses.Create<AddResponse>(ResultCode.EntryAlreadyExists,
                "00000524: UpdErr: DSID-031A11E2, problem 6005 (ENTRY_EXISTS), data 0\n")));

        var results = await Export(executor).ExecuteAsync([UserCreate()], CancellationToken.None);

        Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.UniqueValueAlreadyInUse));
    }

    private static LdapConnectorExport Export(Mock<ILdapOperationExecutor> executor, int concurrency = 1) =>
        new(executor.Object,
            [new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = "Delete Behaviour" }, StringValue = "Delete" }],
            Log.Logger,
            concurrency,
            LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE,
            LdapDirectoryType.ActiveDirectory,
            placeholderMemberDn: null);

    private static PendingExport UserCreate()
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
                    StringValue = UserDn
                },
                new PendingExportAttributeValueChange
                {
                    Attribute = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "sn", ConnectedSystemObjectType = type },
                    ChangeType = PendingExportAttributeChangeType.Add,
                    StringValue = "Bloggs"
                }
            ]
        };
    }
}
