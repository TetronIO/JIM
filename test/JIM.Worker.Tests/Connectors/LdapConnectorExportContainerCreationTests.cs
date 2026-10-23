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
/// Tests for "Create containers as needed?" when another writer creates the same container first. Each parallel export
/// batch has a connector of its own, so two batches can both find a parent OU missing and both try to add it; the
/// directory then answers the second add with entryAlreadyExists. The container exists, which is all the object's
/// create needs, so that answer must not fail the object.
/// </summary>
[TestFixture]
public class LdapConnectorExportContainerCreationTests
{
    private const string ContainerDn = "ou=new,ou=people,dc=test,dc=local";
    private const string ParentContainerDn = "ou=people,dc=test,dc=local";
    private const string UserDn = "cn=user1," + ContainerDn;

    private Mock<ILdapOperationExecutor> _mockExecutor = null!;
    private List<AddRequest> _addsSent = null!;

    [SetUp]
    public void SetUp()
    {
        _mockExecutor = new Mock<ILdapOperationExecutor>();
        _addsSent = [];
    }

    [TestCase(1, TestName = "ExecuteAsync_ContainerCreatedByAnotherWriterFirst_StillCreatesTheObject (sequential)")]
    [TestCase(2, TestName = "ExecuteAsync_ContainerCreatedByAnotherWriterFirst_StillCreatesTheObject (concurrent)")]
    public async Task ExecuteAsync_ContainerCreatedByAnotherWriterFirst_StillCreatesTheObjectAsync(int exportConcurrency)
    {
        SetUpDirectory(containerAddResult: ResultCode.EntryAlreadyExists);
        var export = CreateExport(exportConcurrency);

        var results = await export.ExecuteAsync([CreateUserPendingExport()], CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True, results[0].ErrorMessage);
            Assert.That(_addsSent.Select(a => a.DistinguishedName), Does.Contain(UserDn), "The object itself should still be added");
            Assert.That(export.CreatedContainerExternalIds, Is.Empty,
                "A container another writer created was not created by this connector, so it must not be reported as created");
        }
    }

    [TestCase(1, TestName = "ExecuteAsync_ContainerAddRefusedForAnotherReason_FailsTheObject (sequential)")]
    [TestCase(2, TestName = "ExecuteAsync_ContainerAddRefusedForAnotherReason_FailsTheObject (concurrent)")]
    public async Task ExecuteAsync_ContainerAddRefusedForAnotherReason_FailsTheObjectAsync(int exportConcurrency)
    {
        SetUpDirectory(containerAddResult: ResultCode.InsufficientAccessRights);
        var export = CreateExport(exportConcurrency);

        var results = await export.ExecuteAsync([CreateUserPendingExport()], CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False, "A container that could not be created must still fail the object");
            Assert.That(_addsSent.Select(a => a.DistinguishedName), Does.Not.Contain(UserDn));
        }
    }

    [TestCase(1, TestName = "ExecuteAsync_ContainerMissing_CreatesAndReportsIt (sequential)")]
    [TestCase(2, TestName = "ExecuteAsync_ContainerMissing_CreatesAndReportsIt (concurrent)")]
    public async Task ExecuteAsync_ContainerMissing_CreatesAndReportsItAsync(int exportConcurrency)
    {
        SetUpDirectory(containerAddResult: ResultCode.Success);
        var export = CreateExport(exportConcurrency);

        var results = await export.ExecuteAsync([CreateUserPendingExport()], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True, results[0].ErrorMessage);
            Assert.That(export.CreatedContainerExternalIds, Is.EqualTo(new[] { ContainerDn }));
        }
    }

    /// <summary>
    /// The parent OU exists, the new OU does not, and its add answers with the given result. Every other add (the
    /// object itself) succeeds. Both the blocking and the asynchronous executor entry points behave the same way, the
    /// way the real executor does: a refused operation throws <see cref="DirectoryOperationException"/>.
    /// </summary>
    private void SetUpDirectory(ResultCode containerAddResult)
    {
        DirectoryResponse Respond(DirectoryRequest request)
        {
            switch (request)
            {
                case SearchRequest search when search.DistinguishedName == ParentContainerDn:
                    return LdapTestResponses.SearchResponseWith(ParentContainerDn, ("objectClass", "organizationalUnit"));
                case SearchRequest search when search.DistinguishedName == ContainerDn:
                    throw new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.NoSuchObject), "no such object");
                case SearchRequest:
                    return LdapTestResponses.SearchResponseWithEntries();
                case AddRequest add:
                    _addsSent.Add(add);
                    if (add.DistinguishedName == ContainerDn && containerAddResult != ResultCode.Success)
                        throw new DirectoryOperationException(LdapTestResponses.Create<AddResponse>(containerAddResult), containerAddResult.ToString());
                    return LdapTestResponses.Create<AddResponse>(ResultCode.Success);
                default:
                    throw new InvalidOperationException($"Unexpected request {request.GetType().Name}");
            }
        }

        _mockExecutor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>())).Returns((DirectoryRequest r) => Respond(r));
        _mockExecutor.Setup(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>())).Returns((DirectoryRequest r) => Task.FromResult(Respond(r)));
    }

    private LdapConnectorExport CreateExport(int exportConcurrency)
    {
        var settings = new List<ConnectedSystemSettingValue>
        {
            new() { Setting = new ConnectorDefinitionSetting { Name = "Delete Behaviour" }, StringValue = "Delete" },
            new() { Setting = new ConnectorDefinitionSetting { Name = "Create containers as needed?" }, CheckboxValue = true }
        };

        return new LdapConnectorExport(_mockExecutor.Object, settings, Log.Logger, exportConcurrency,
            directoryType: LdapDirectoryType.OpenLDAP);
    }

    private static PendingExport CreateUserPendingExport()
    {
        var csoType = new ConnectedSystemObjectType { Name = "inetOrgPerson" };
        var dnAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = csoType };
        var cnAttr = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "cn", ConnectedSystemObjectType = csoType };

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Create,
            ConnectedSystemObject = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = csoType },
            AttributeValueChanges =
            [
                new() { Attribute = dnAttr, ChangeType = PendingExportAttributeChangeType.Add, StringValue = UserDn },
                new() { Attribute = cnAttr, ChangeType = PendingExportAttributeChangeType.Add, StringValue = "user1" }
            ]
        };
    }
}
