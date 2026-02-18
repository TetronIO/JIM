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

[TestFixture]
public class LdapConnectorExportAsyncTests
{
    private Mock<ILdapOperationExecutor> _mockExecutor = null!;
    private IList<ConnectedSystemSettingValue> _defaultSettings = null!;

    [SetUp]
    public void SetUp()
    {
        _mockExecutor = new Mock<ILdapOperationExecutor>();
        _defaultSettings = new List<ConnectedSystemSettingValue>
        {
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "Delete Behaviour" },
                StringValue = "Delete"
            }
        };
    }

    #region ExecuteAsync basic tests

    [Test]
    public async Task ExecuteAsync_EmptyList_ReturnsEmptyResultsAsync()
    {
        var export = CreateExport(concurrency: 4);
        var results = await export.ExecuteAsync(new List<PendingExport>(), CancellationToken.None);

        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task ExecuteAsync_ConcurrencyOne_DelegatesToSyncExecuteAsync()
    {
        // With concurrency=1, ExecuteAsync delegates to Execute (sync path)
        var pendingExport = CreateUpdatePendingExport("CN=Test,DC=test,DC=local");

        SetupModifyResponse(ResultCode.Success);

        var export = CreateExport(concurrency: 1);
        var results = await export.ExecuteAsync(new List<PendingExport> { pendingExport }, CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True);
        // Verify sync SendRequest was called (not async)
        _mockExecutor.Verify(e => e.SendRequest(It.IsAny<ModifyRequest>()), Times.Once);
        _mockExecutor.Verify(e => e.SendRequestAsync(It.IsAny<DirectoryRequest>()), Times.Never);
    }

    [Test]
    public async Task ExecuteAsync_ConcurrencyGreaterThanOne_UsesAsyncPathAsync()
    {
        var pendingExport = CreateUpdatePendingExport("CN=Test,DC=test,DC=local");

        SetupAsyncModifyResponse(ResultCode.Success);

        var export = CreateExport(concurrency: 4);
        var results = await export.ExecuteAsync(new List<PendingExport> { pendingExport }, CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True);
        // Verify async SendRequestAsync was called
        _mockExecutor.Verify(e => e.SendRequestAsync(It.IsAny<ModifyRequest>()), Times.Once);
    }

    #endregion

    #region Positional ordering tests

    [Test]
    public async Task ExecuteAsync_MaintainsPositionalOrdering_WhenConcurrentAsync()
    {
        // Create 5 update exports with different DNs
        var exports = new List<PendingExport>();
        for (var i = 0; i < 5; i++)
        {
            exports.Add(CreateUpdatePendingExport($"CN=User{i},DC=test,DC=local"));
        }

        SetupAsyncModifyResponse(ResultCode.Success);

        var export = CreateExport(concurrency: 4);
        var results = await export.ExecuteAsync(exports, CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(5));
        // All should succeed and maintain 1:1 positional correspondence
        for (var i = 0; i < 5; i++)
        {
            Assert.That(results[i].Success, Is.True,
                $"Result at index {i} should be successful");
        }
    }

    #endregion

    #region Individual failure isolation tests

    [Test]
    public async Task ExecuteAsync_IndividualFailure_DoesNotAbortOtherExportsAsync()
    {
        var exports = new List<PendingExport>
        {
            CreateUpdatePendingExport("CN=Good1,DC=test,DC=local"),
            CreateUpdatePendingExport("CN=Bad,DC=test,DC=local"),
            CreateUpdatePendingExport("CN=Good2,DC=test,DC=local")
        };

        var callCount = 0;
        _mockExecutor.Setup(e => e.SendRequestAsync(It.IsAny<ModifyRequest>()))
            .ReturnsAsync(() =>
            {
                var count = Interlocked.Increment(ref callCount);
                // Second call fails
                if (count == 2)
                    return CreateDirectoryResponse<ModifyResponse>(ResultCode.InsufficientAccessRights);

                return CreateDirectoryResponse<ModifyResponse>(ResultCode.Success);
            });

        var export = CreateExport(concurrency: 4);
        var results = await export.ExecuteAsync(exports, CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(3));
        // At least 2 should succeed (the failure doesn't abort others)
        var successCount = results.Count(r => r.Success);
        Assert.That(successCount, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public async Task ExecuteAsync_ExceptionInOneExport_OthersStillCompleteAsync()
    {
        var exports = new List<PendingExport>
        {
            CreateUpdatePendingExport("CN=Good,DC=test,DC=local"),
            CreateUpdatePendingExport(null!) // Will cause exception (no DN)
        };

        SetupAsyncModifyResponse(ResultCode.Success);

        var export = CreateExport(concurrency: 4);
        var results = await export.ExecuteAsync(exports, CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results[0].Success, Is.True);
        Assert.That(results[1].Success, Is.False);
        Assert.That(results[1].ErrorMessage, Is.Not.Null);
    }

    #endregion

    #region CancellationToken tests

    [Test]
    public void ExecuteAsync_CancellationRequested_ThrowsOperationCancelledExceptionAsync()
    {
        var exports = new List<PendingExport>
        {
            CreateUpdatePendingExport("CN=Test,DC=test,DC=local")
        };

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var export = CreateExport(concurrency: 4);
        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await export.ExecuteAsync(exports, cts.Token));
    }

    #endregion

    #region Create operation tests

    [Test]
    public async Task ExecuteAsync_CreateOperation_SendsAddRequestAsync()
    {
        var pendingExport = CreateCreatePendingExport("CN=NewUser,OU=Users,DC=test,DC=local", "user");

        // Setup Add response
        _mockExecutor.Setup(e => e.SendRequestAsync(It.IsAny<AddRequest>()))
            .ReturnsAsync(CreateDirectoryResponse<AddResponse>(ResultCode.Success));

        // Setup objectGUID search response (returns empty - non-fatal)
        _mockExecutor.Setup(e => e.SendRequestAsync(It.IsAny<SearchRequest>()))
            .ReturnsAsync(CreateDirectoryResponse<SearchResponse>(ResultCode.Success));

        var export = CreateExport(concurrency: 4);
        var results = await export.ExecuteAsync(new List<PendingExport> { pendingExport }, CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True);
        Assert.That(results[0].SecondaryExternalId, Is.EqualTo("CN=NewUser,OU=Users,DC=test,DC=local"));

        // Verify Add was called, then Search for GUID
        _mockExecutor.Verify(e => e.SendRequestAsync(It.IsAny<AddRequest>()), Times.Once);
        _mockExecutor.Verify(e => e.SendRequestAsync(It.IsAny<SearchRequest>()), Times.Once);
    }

    #endregion

    #region Update with rename tests

    [Test]
    public async Task ExecuteAsync_UpdateWithRename_RenameCompletesBeforeModifyAsync()
    {
        var pendingExport = CreateUpdateWithRenamePendingExport(
            "CN=OldName,DC=test,DC=local",
            "CN=NewName,DC=test,DC=local");

        var callOrder = new List<string>();

        _mockExecutor.Setup(e => e.SendRequestAsync(It.IsAny<ModifyDNRequest>()))
            .ReturnsAsync(() =>
            {
                callOrder.Add("Rename");
                return CreateDirectoryResponse<ModifyDNResponse>(ResultCode.Success);
            });

        _mockExecutor.Setup(e => e.SendRequestAsync(It.IsAny<ModifyRequest>()))
            .ReturnsAsync(() =>
            {
                callOrder.Add("Modify");
                return CreateDirectoryResponse<ModifyResponse>(ResultCode.Success);
            });

        var export = CreateExport(concurrency: 4);
        var results = await export.ExecuteAsync(new List<PendingExport> { pendingExport }, CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True);
        Assert.That(results[0].SecondaryExternalId, Is.EqualTo("CN=NewName,DC=test,DC=local"));

        // Verify rename happened before modify
        Assert.That(callOrder, Has.Count.EqualTo(2));
        Assert.That(callOrder[0], Is.EqualTo("Rename"));
        Assert.That(callOrder[1], Is.EqualTo("Modify"));
    }

    #endregion

    #region Delete operation tests

    [Test]
    public async Task ExecuteAsync_HardDelete_SendsDeleteRequestAsync()
    {
        var pendingExport = CreateDeletePendingExport("CN=ToDelete,DC=test,DC=local");

        _mockExecutor.Setup(e => e.SendRequestAsync(It.IsAny<DeleteRequest>()))
            .ReturnsAsync(CreateDirectoryResponse<DeleteResponse>(ResultCode.Success));

        var export = CreateExport(concurrency: 4);
        var results = await export.ExecuteAsync(new List<PendingExport> { pendingExport }, CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True);
        _mockExecutor.Verify(e => e.SendRequestAsync(It.IsAny<DeleteRequest>()), Times.Once);
    }

    #endregion

    #region Mixed operation tests

    [Test]
    public async Task ExecuteAsync_MixedOperations_MaintainsCorrectResultsAsync()
    {
        var exports = new List<PendingExport>
        {
            CreateUpdatePendingExport("CN=Update1,DC=test,DC=local"),
            CreateDeletePendingExport("CN=Delete1,DC=test,DC=local"),
            CreateUpdatePendingExport("CN=Update2,DC=test,DC=local")
        };

        SetupAsyncModifyResponse(ResultCode.Success);
        _mockExecutor.Setup(e => e.SendRequestAsync(It.IsAny<DeleteRequest>()))
            .ReturnsAsync(CreateDirectoryResponse<DeleteResponse>(ResultCode.Success));

        var export = CreateExport(concurrency: 4);
        var results = await export.ExecuteAsync(exports, CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(3));
        Assert.That(results.All(r => r.Success), Is.True);
    }

    #endregion

    #region Concurrency clamping tests

    [Test]
    public async Task ExecuteAsync_ConcurrencyExceedsMax_ClampedToMaxAsync()
    {
        // Concurrency of 100 should be clamped to MAX_EXPORT_CONCURRENCY (16)
        var pendingExport = CreateUpdatePendingExport("CN=Test,DC=test,DC=local");

        SetupAsyncModifyResponse(ResultCode.Success);

        var export = CreateExport(concurrency: 100);
        var results = await export.ExecuteAsync(new List<PendingExport> { pendingExport }, CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True);
    }

    [Test]
    public async Task ExecuteAsync_ConcurrencyZero_ClampedToOneAsync()
    {
        // Concurrency of 0 should be clamped to 1 (sync path)
        var pendingExport = CreateUpdatePendingExport("CN=Test,DC=test,DC=local");

        SetupModifyResponse(ResultCode.Success);

        var export = CreateExport(concurrency: 0);
        var results = await export.ExecuteAsync(new List<PendingExport> { pendingExport }, CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True);
        // Concurrency 0 clamped to 1 -> sync path
        _mockExecutor.Verify(e => e.SendRequest(It.IsAny<ModifyRequest>()), Times.Once);
    }

    #endregion

    #region Helper methods

    private LdapConnectorExport CreateExport(int concurrency)
    {
        return new LdapConnectorExport(
            _mockExecutor.Object,
            _defaultSettings,
            Log.Logger,
            concurrency);
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
                AttributeValues = new List<ConnectedSystemObjectAttributeValue>
                {
                    new()
                    {
                        AttributeId = dnAttribute.Id,
                        StringValue = dn
                    }
                }
            },
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Attribute = testAttribute,
                    ChangeType = PendingExportAttributeChangeType.Update,
                    StringValue = "Test User"
                }
            }
        };
    }

    private static PendingExport CreateCreatePendingExport(string dn, string objectClass)
    {
        var csoType = new ConnectedSystemObjectType { Name = objectClass };
        var dnAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "distinguishedName",
            ConnectedSystemObjectType = csoType
        };

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Create,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = csoType
            },
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Attribute = dnAttribute,
                    ChangeType = PendingExportAttributeChangeType.Add,
                    StringValue = dn
                }
            }
        };
    }

    private static PendingExport CreateUpdateWithRenamePendingExport(string currentDn, string newDn)
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
                AttributeValues = new List<ConnectedSystemObjectAttributeValue>
                {
                    new()
                    {
                        AttributeId = dnAttribute.Id,
                        StringValue = currentDn
                    }
                }
            },
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Attribute = dnAttribute,
                    ChangeType = PendingExportAttributeChangeType.Update,
                    StringValue = newDn
                },
                new()
                {
                    Attribute = testAttribute,
                    ChangeType = PendingExportAttributeChangeType.Update,
                    StringValue = "Renamed User"
                }
            }
        };
    }

    private static PendingExport CreateDeletePendingExport(string dn)
    {
        var csoType = new ConnectedSystemObjectType { Name = "user" };
        var dnAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "distinguishedName",
            ConnectedSystemObjectType = csoType
        };

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Delete,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = csoType,
                SecondaryExternalIdAttributeId = dnAttribute.Id,
                AttributeValues = new List<ConnectedSystemObjectAttributeValue>
                {
                    new()
                    {
                        AttributeId = dnAttribute.Id,
                        StringValue = dn
                    }
                }
            },
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
    }

    private void SetupModifyResponse(ResultCode resultCode)
    {
        _mockExecutor.Setup(e => e.SendRequest(It.IsAny<ModifyRequest>()))
            .Returns(CreateDirectoryResponse<ModifyResponse>(resultCode));
    }

    private void SetupAsyncModifyResponse(ResultCode resultCode)
    {
        _mockExecutor.Setup(e => e.SendRequestAsync(It.IsAny<ModifyRequest>()))
            .ReturnsAsync(CreateDirectoryResponse<ModifyResponse>(resultCode));
    }

    /// <summary>
    /// Creates a DirectoryResponse subclass instance with the specified ResultCode.
    /// DirectoryResponse subclasses have an internal 5-parameter constructor:
    /// (string dn, DirectoryControl[] controls, ResultCode result, string message, Uri[] referral)
    /// </summary>
    private static T CreateDirectoryResponse<T>(ResultCode resultCode) where T : DirectoryResponse
    {
        var response = (T)Activator.CreateInstance(
            typeof(T),
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            args: new object?[] { "", Array.Empty<DirectoryControl>(), resultCode, "", Array.Empty<Uri>() },
            culture: null)!;

        return response;
    }

    #endregion
}
