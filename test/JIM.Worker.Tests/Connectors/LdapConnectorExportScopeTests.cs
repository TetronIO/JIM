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
using System.Reflection;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Container selection said which parts of a directory JIM imports, and export ignored it entirely. An Export
/// Attribute Flow that moves an account into an unselected container (the routine "move disabled accounts to
/// OU=Disabled" pattern) therefore wrote the object somewhere JIM cannot read back: the Pending Export was never
/// confirmed, the next Full Import found the object missing and marked it obsolete, and the following
/// synchronisation disconnected it and either orphaned the directory entry or deleted and re-provisioned it. JIM
/// churned objects it had exported itself, and said nothing.
///
/// Export now refuses to write outside the scope JIM manages, per object, so the rest of the export proceeds.
/// </summary>
[TestFixture]
public class LdapConnectorExportScopeTests
{
    private Mock<ILdapOperationExecutor> _mockExecutor = null!;
    private IList<ConnectedSystemSettingValue> _defaultSettings = null!;

    [SetUp]
    public void SetUp()
    {
        _mockExecutor = new Mock<ILdapOperationExecutor>();
        _defaultSettings =
        [
            new ConnectedSystemSettingValue
            {
                Setting = new ConnectorDefinitionSetting { Name = "Delete Behaviour" },
                StringValue = "Delete"
            }
        ];
    }

    [Test]
    public async Task ExecuteAsync_TargetOutsideSelectedContainers_FailsThatObjectWithoutWritingAsync()
    {
        var export = CreateExport();
        export.SetManagedScope([Managed("OU=Users,DC=test,DC=local")]);

        var results = await export.ExecuteAsync(
            [CreateUpdatePendingExport("CN=Bob,OU=Disabled,DC=test,DC=local")],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.OutsideManagedScope));
            Assert.That(results[0].ErrorMessage, Does.Contain("OU=Disabled,DC=test,DC=local"));
        }
        _mockExecutor.Verify(e => e.SendRequest(It.IsAny<ModifyRequest>()), Times.Never);
        _mockExecutor.Verify(e => e.SendRequestAsync(It.IsAny<ModifyRequest>()), Times.Never);
    }

    [Test]
    public async Task ExecuteAsync_TargetInsideASelectedContainer_WritesAsUsualAsync()
    {
        SetupModifyResponse(ResultCode.Success);
        var export = CreateExport();
        export.SetManagedScope([Managed("OU=Users,DC=test,DC=local")]);

        var results = await export.ExecuteAsync(
            [CreateUpdatePendingExport("CN=Bob,OU=Users,DC=test,DC=local")],
            CancellationToken.None);

        Assert.That(results[0].Success, Is.True);
    }

    [Test]
    public async Task ExecuteAsync_TargetInsideANestedSelectedContainer_WritesAsUsualAsync()
    {
        // Selecting a container selects its subtree for import, so export must treat descendants as in scope too.
        SetupModifyResponse(ResultCode.Success);
        var export = CreateExport();
        export.SetManagedScope([Managed("OU=Corp,DC=test,DC=local")]);

        var results = await export.ExecuteAsync(
            [CreateUpdatePendingExport("CN=Bob,OU=Contractors,OU=Users,OU=Corp,DC=test,DC=local")],
            CancellationToken.None);

        Assert.That(results[0].Success, Is.True);
    }

    [Test]
    public async Task ExecuteAsync_NoScopeSupplied_WritesAsUsualAsync()
    {
        // A Connected System with no container selections, and any Connector JIM has not told about scope, must
        // behave exactly as before; an empty scope is "not stated", never "nothing is permitted".
        SetupModifyResponse(ResultCode.Success);
        var export = CreateExport();

        var results = await export.ExecuteAsync(
            [CreateUpdatePendingExport("CN=Bob,OU=Anywhere,DC=test,DC=local")],
            CancellationToken.None);

        Assert.That(results[0].Success, Is.True);
    }

    [Test]
    public async Task ExecuteAsync_ScopeMatchIsCaseInsensitiveAsync()
    {
        // Distinguished Names are compared case-insensitively everywhere else in the Connector, and a directory
        // that returns a differently-cased DN must not make every export fail.
        SetupModifyResponse(ResultCode.Success);
        var export = CreateExport();
        export.SetManagedScope([Managed("ou=users,dc=test,dc=local")]);

        var results = await export.ExecuteAsync(
            [CreateUpdatePendingExport("CN=Bob,OU=Users,DC=test,DC=local")],
            CancellationToken.None);

        Assert.That(results[0].Success, Is.True);
    }

    [Test]
    public async Task ExecuteAsync_TargetIsASelectedContainerItselfAsync()
    {
        // An object whose parent IS the selected container, expressed with no intervening container.
        SetupModifyResponse(ResultCode.Success);
        var export = CreateExport();
        export.SetManagedScope([Managed("DC=test,DC=local")]);

        var results = await export.ExecuteAsync(
            [CreateUpdatePendingExport("CN=Bob,DC=test,DC=local")],
            CancellationToken.None);

        Assert.That(results[0].Success, Is.True);
    }

    [Test]
    public async Task ExecuteAsync_TargetBeneathAOneLevelContainer_FailsThatObjectWithoutWritingAsync()
    {
        // Container Scope (#351) narrows what an import returns, and the export guard has to narrow with it. A One
        // Level container imports only what sits directly within it, so writing to something a level deeper is the
        // very unreadable write this guard exists to refuse, even though the Distinguished Name sits under a
        // selected container.
        var export = CreateExport();
        export.SetManagedScope([Managed("OU=Users,DC=test,DC=local", ConnectedSystemContainerScope.OneLevel)]);

        var results = await export.ExecuteAsync(
            [CreateUpdatePendingExport("CN=Bob,OU=Contractors,OU=Users,DC=test,DC=local")],
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorType, Is.EqualTo(ConnectedSystemExportErrorType.OutsideManagedScope));
        }
        _mockExecutor.Verify(e => e.SendRequest(It.IsAny<ModifyRequest>()), Times.Never);
    }

    [Test]
    public async Task ExecuteAsync_TargetDirectlyWithinAOneLevelContainer_WritesAsUsualAsync()
    {
        SetupModifyResponse(ResultCode.Success);
        var export = CreateExport();
        export.SetManagedScope([Managed("OU=Users,DC=test,DC=local", ConnectedSystemContainerScope.OneLevel)]);

        var results = await export.ExecuteAsync(
            [CreateUpdatePendingExport("CN=Bob,OU=Users,DC=test,DC=local")],
            CancellationToken.None);

        Assert.That(results[0].Success, Is.True);
    }

    #region Helper methods

    private LdapConnectorExport CreateExport()
    {
        return new LdapConnectorExport(_mockExecutor.Object, _defaultSettings, Log.Logger, 1);
    }

    private void SetupModifyResponse(ResultCode resultCode)
    {
        _mockExecutor.Setup(e => e.SendRequest(It.IsAny<ModifyRequest>()))
            .Returns(CreateDirectoryResponse<ModifyResponse>(resultCode));
    }

    private static PendingExport CreateUpdatePendingExport(string dn)
    {
        var csoType = new ConnectedSystemObjectType { Name = "user" };
        var dnAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "distinguishedName",
            ConnectedSystemObjectType = csoType
        };
        var testAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 2,
            Name = "displayName",
            ConnectedSystemObjectType = csoType
        };

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = csoType,
                SecondaryExternalIdAttributeId = dnAttribute.Id,
                AttributeValues =
                [
                    new ConnectedSystemObjectAttributeValue { Attribute = dnAttribute, AttributeId = dnAttribute.Id, StringValue = dn }
                ]
            },
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Attribute = testAttribute,
                    AttributeId = testAttribute.Id,
                    ChangeType = PendingExportAttributeChangeType.Update,
                    StringValue = "Bob Example"
                }
            ]
        };
    }

    /// <summary>
    /// DirectoryResponse types have no public constructor, so tests build them reflectively; this mirrors the
    /// approach in the sibling export test fixtures.
    /// </summary>
    private static T CreateDirectoryResponse<T>(ResultCode resultCode) where T : DirectoryResponse
    {
        return (T)Activator.CreateInstance(
            typeof(T),
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            args: ["", Array.Empty<DirectoryControl>(), resultCode, "", Array.Empty<Uri>()],
            culture: null)!;
    }

    #endregion

    /// <summary>
    /// A selected container as JIM states it to the Connector. Subtree by default, which is what every container
    /// selected before Container Scope (#351) existed behaves as.
    /// </summary>
    private static ConnectedSystemContainer Managed(
        string externalId,
        ConnectedSystemContainerScope scope = ConnectedSystemContainerScope.Subtree) =>
        new() { ExternalId = externalId, Scope = scope };
}
