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
/// The LDAP Connector reports, on every successful export, whether it wrote the attribute through which an Active
/// Directory or Samba AD account is enabled or disabled (<see cref="ConnectedSystemExportResult.SetsAccountEnabledState"/>,
/// #2064). JIM records that on the account's owed Initial Password, so delivering the password leaves an enabled state
/// an Attribute Flow set alone: an account the flow creates disabled (a pre-hire, or a person archived at creation) is
/// no longer enabled with a password JIM knows.
/// <para>
/// The Connector observes and reports; it decides nothing. Only the Active Directory family reports it, because only
/// there does a password delivery's Enable Account mean anything. Both export paths are covered, sequential
/// (concurrency 1) and concurrent, because each builds and sends its own requests.
/// </para>
/// </summary>
[TestFixture]
public class LdapConnectorExportAccountEnabledStateTests
{
    private const string UserDn = "CN=Joe Bloggs,OU=Users,DC=corp,DC=local";
    private const string UserAccountControl = "userAccountControl";

    #region Create

    [TestCase(nameof(LdapDirectoryType.SambaAD), 1)]
    [TestCase(nameof(LdapDirectoryType.SambaAD), 4)]
    [TestCase(nameof(LdapDirectoryType.ActiveDirectory), 1)]
    [TestCase(nameof(LdapDirectoryType.ActiveDirectory), 4)]
    public async Task ExecuteAsync_CreateCarryingUserAccountControl_ReportsItSetTheEnabledStateAsync(string directoryType, int concurrency)
    {
        // The case #2064 is about: a pre-hire the flow creates disabled.
        var results = await Export(Enum.Parse<LdapDirectoryType>(directoryType), concurrency)
            .ExecuteAsync([UserCreate(initialPasswordFollows: true, Change(UserAccountControl, intValue: 514))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].SetsAccountEnabledState, Is.True);
        }
    }

    [Test]
    public async Task ExecuteAsync_ActiveDirectoryCreateWithheldUntilInitialPassword_ReportsBothFactsAsync()
    {
        // A held enable is a flow-set enabled state by definition, which is what lets the delivery complete it.
        var results = await Export(LdapDirectoryType.ActiveDirectory)
            .ExecuteAsync([UserCreate(initialPasswordFollows: true, Change(UserAccountControl, intValue: 512))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].AttributesAwaitingInitialPassword, Is.EqualTo(new[] { UserAccountControl }));
            Assert.That(results[0].SetsAccountEnabledState, Is.True);
        }
    }

    [TestCase(1)]
    [TestCase(4)]
    public async Task ExecuteAsync_CreateWithoutUserAccountControl_ReportsNothingAsync(int concurrency)
    {
        // The directory creates the account disabled on its own, and the Initial Password's Enable Account decides.
        var results = await Export(LdapDirectoryType.ActiveDirectory, concurrency)
            .ExecuteAsync([UserCreate(initialPasswordFollows: true)], CancellationToken.None);

        Assert.That(results[0].SetsAccountEnabledState, Is.False);
    }

    [TestCase(nameof(LdapDirectoryType.OpenLDAP))]
    [TestCase(nameof(LdapDirectoryType.DirectoryServer389))]
    [TestCase(nameof(LdapDirectoryType.Generic))]
    public async Task ExecuteAsync_CreateOutsideTheActiveDirectoryFamily_ReportsNothingAsync(string directoryType)
    {
        var results = await Export(Enum.Parse<LdapDirectoryType>(directoryType))
            .ExecuteAsync([UserCreate(initialPasswordFollows: true, Change(UserAccountControl, intValue: 514))], CancellationToken.None);

        Assert.That(results[0].SetsAccountEnabledState, Is.False);
    }

    #endregion

    #region Update

    [TestCase(nameof(LdapDirectoryType.SambaAD), 1)]
    [TestCase(nameof(LdapDirectoryType.SambaAD), 4)]
    [TestCase(nameof(LdapDirectoryType.ActiveDirectory), 1)]
    [TestCase(nameof(LdapDirectoryType.ActiveDirectory), 4)]
    public async Task ExecuteAsync_UpdateWritingUserAccountControl_ReportsItSetTheEnabledStateAsync(string directoryType, int concurrency)
    {
        // A leaver disabled while the first password is still owed: the delivery must not re-enable it.
        var results = await Export(Enum.Parse<LdapDirectoryType>(directoryType), concurrency)
            .ExecuteAsync([UserUpdate(Change(UserAccountControl, intValue: 514, changeType: PendingExportAttributeChangeType.Update))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].SetsAccountEnabledState, Is.True);
        }
    }

    [TestCase(1)]
    [TestCase(4)]
    public async Task ExecuteAsync_UpdateNotWritingUserAccountControl_ReportsNothingAsync(int concurrency)
    {
        var results = await Export(LdapDirectoryType.ActiveDirectory, concurrency)
            .ExecuteAsync([UserUpdate(Change("description", stringValue: "Contractor", changeType: PendingExportAttributeChangeType.Update))], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].SetsAccountEnabledState, Is.False);
        }
    }

    #endregion

    #region Delete

    [TestCase(nameof(LdapDirectoryType.SambaAD), 1)]
    [TestCase(nameof(LdapDirectoryType.SambaAD), 4)]
    [TestCase(nameof(LdapDirectoryType.ActiveDirectory), 1)]
    [TestCase(nameof(LdapDirectoryType.ActiveDirectory), 4)]
    public async Task ExecuteAsync_DeleteDoneAsADisableThroughUserAccountControl_ReportsItSetTheEnabledStateAsync(string directoryType, int concurrency)
    {
        // An account deprovisioned while its first password is still owed must not be re-enabled by the delivery.
        var results = await Export(Enum.Parse<LdapDirectoryType>(directoryType), concurrency, deleteBehaviour: LdapConnectorConstants.DELETE_BEHAVIOUR_DISABLE)
            .ExecuteAsync([UserDelete()], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].SetsAccountEnabledState, Is.True);
        }
    }

    [TestCase(1)]
    [TestCase(4)]
    public async Task ExecuteAsync_DeleteDoneAsADisableThroughAnotherAttribute_ReportsNothingAsync(int concurrency)
    {
        var results = await Export(LdapDirectoryType.ActiveDirectory, concurrency, deleteBehaviour: LdapConnectorConstants.DELETE_BEHAVIOUR_DISABLE, disableAttribute: "msDS-UserAccountDisabled")
            .ExecuteAsync([UserDelete()], CancellationToken.None);

        Assert.That(results[0].SetsAccountEnabledState, Is.False);
    }

    [TestCase(1)]
    [TestCase(4)]
    public async Task ExecuteAsync_HardDelete_ReportsNothingAsync(int concurrency)
    {
        var results = await Export(LdapDirectoryType.ActiveDirectory, concurrency)
            .ExecuteAsync([UserDelete()], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].SetsAccountEnabledState, Is.False);
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// An export over a directory that accepts every request, on whichever path the concurrency selects. A read of the
    /// entry answers with an enabled account, which is what the disable path reads before it sets ACCOUNTDISABLE.
    /// </summary>
    private static LdapConnectorExport Export(
        LdapDirectoryType directoryType,
        int concurrency = 1,
        string deleteBehaviour = "Delete",
        string? disableAttribute = null)
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<AddRequest>())).Returns(LdapTestResponses.Create<AddResponse>(ResultCode.Success));
        executor.Setup(e => e.SendRequest(It.IsAny<ModifyRequest>())).Returns(LdapTestResponses.Create<ModifyResponse>(ResultCode.Success));
        executor.Setup(e => e.SendRequest(It.IsAny<DeleteRequest>())).Returns(LdapTestResponses.Create<DeleteResponse>(ResultCode.Success));
        executor.Setup(e => e.SendRequest(It.IsAny<SearchRequest>())).Returns(() => LdapTestResponses.SearchResponseWith(UserDn, (UserAccountControl, "512")));
        executor.Setup(e => e.SendRequestAsync(It.IsAny<AddRequest>())).ReturnsAsync(LdapTestResponses.Create<AddResponse>(ResultCode.Success));
        executor.Setup(e => e.SendRequestAsync(It.IsAny<ModifyRequest>())).ReturnsAsync(LdapTestResponses.Create<ModifyResponse>(ResultCode.Success));
        executor.Setup(e => e.SendRequestAsync(It.IsAny<DeleteRequest>())).ReturnsAsync(LdapTestResponses.Create<DeleteResponse>(ResultCode.Success));
        executor.Setup(e => e.SendRequestAsync(It.IsAny<SearchRequest>())).ReturnsAsync(() => LdapTestResponses.SearchResponseWith(UserDn, (UserAccountControl, "512")));

        List<ConnectedSystemSettingValue> settings =
        [
            new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = "Delete Behaviour" }, StringValue = deleteBehaviour }
        ];
        if (disableAttribute != null)
            settings.Add(new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = "Disable Attribute" }, StringValue = disableAttribute });

        return new LdapConnectorExport(executor.Object, settings, Log.Logger, concurrency,
            LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE, directoryType, placeholderMemberDn: null);
    }

    private static readonly ConnectedSystemObjectType UserType = new() { Name = "user" };
    private static readonly ConnectedSystemObjectTypeAttribute DistinguishedName = new() { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = UserType };
    private static readonly List<string> AttributeIds = [UserAccountControl, "sAMAccountName", "description"];

    private static PendingExportAttributeValueChange Change(
        string attributeName,
        int? intValue = null,
        string? stringValue = null,
        PendingExportAttributeChangeType changeType = PendingExportAttributeChangeType.Add) => new()
    {
        Attribute = new ConnectedSystemObjectTypeAttribute { Id = AttributeIds.IndexOf(attributeName) + 2, Name = attributeName, ConnectedSystemObjectType = UserType },
        ChangeType = changeType,
        IntValue = intValue,
        StringValue = stringValue
    };

    private static PendingExport UserCreate(bool initialPasswordFollows, params PendingExportAttributeValueChange[] changes) => new()
    {
        Id = Guid.NewGuid(),
        ChangeType = PendingExportChangeType.Create,
        InitialPasswordFollows = initialPasswordFollows,
        ConnectedSystemObject = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = UserType },
        AttributeValueChanges =
        [
            new PendingExportAttributeValueChange { Attribute = DistinguishedName, ChangeType = PendingExportAttributeChangeType.Add, StringValue = UserDn },
            Change("sAMAccountName", stringValue: "jbloggs"),
            .. changes
        ]
    };

    private static ConnectedSystemObject ExistingUser() => new()
    {
        Id = Guid.NewGuid(),
        Type = UserType,
        SecondaryExternalIdAttributeId = DistinguishedName.Id,
        AttributeValues = [new ConnectedSystemObjectAttributeValue { Attribute = DistinguishedName, AttributeId = DistinguishedName.Id, StringValue = UserDn }]
    };

    private static PendingExport UserUpdate(params PendingExportAttributeValueChange[] changes) => new()
    {
        Id = Guid.NewGuid(),
        ChangeType = PendingExportChangeType.Update,
        ConnectedSystemObject = ExistingUser(),
        AttributeValueChanges = [.. changes]
    };

    private static PendingExport UserDelete() => new()
    {
        Id = Guid.NewGuid(),
        ChangeType = PendingExportChangeType.Delete,
        ConnectedSystemObject = ExistingUser(),
        AttributeValueChanges = []
    };

    #endregion
}
