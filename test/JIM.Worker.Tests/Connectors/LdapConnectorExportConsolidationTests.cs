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
public class LdapConnectorExportConsolidationTests
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

    #region ConsolidateModifications tests

    [Test]
    public void ConsolidateModifications_SingleAttributeChange_ReturnsSingleConsolidationAsync()
    {
        var pendingExport = CreateMemberAddPendingExport("CN=Group,DC=test,DC=local", 1);

        var result = LdapConnectorExport.ConsolidateModifications(pendingExport);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].AttributeName, Is.EqualTo("member"));
        Assert.That(result[0].Operation, Is.EqualTo(DirectoryAttributeOperation.Add));
        Assert.That(result[0].AttributeChanges, Has.Count.EqualTo(1));
    }

    [Test]
    public void ConsolidateModifications_MultipleSameAttributeAdds_ConsolidatesIntoOneAsync()
    {
        var pendingExport = CreateMemberAddPendingExport("CN=Group,DC=test,DC=local", 5);

        var result = LdapConnectorExport.ConsolidateModifications(pendingExport);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].AttributeName, Is.EqualTo("member"));
        Assert.That(result[0].Operation, Is.EqualTo(DirectoryAttributeOperation.Add));
        Assert.That(result[0].AttributeChanges, Has.Count.EqualTo(5));
    }

    [Test]
    public void ConsolidateModifications_MixedAddAndRemove_CreatesSeparateConsolidationsAsync()
    {
        var pendingExport = CreateMemberMixedPendingExport("CN=Group,DC=test,DC=local", addCount: 3, removeCount: 2);

        var result = LdapConnectorExport.ConsolidateModifications(pendingExport);

        Assert.That(result, Has.Count.EqualTo(2));

        var addGroup = result.First(r => r.Operation == DirectoryAttributeOperation.Add);
        var removeGroup = result.First(r => r.Operation == DirectoryAttributeOperation.Delete);

        Assert.That(addGroup.AttributeChanges, Has.Count.EqualTo(3));
        Assert.That(removeGroup.AttributeChanges, Has.Count.EqualTo(2));
    }

    [Test]
    public void ConsolidateModifications_DifferentAttributes_NotConsolidatedAsync()
    {
        var csoType = new ConnectedSystemObjectType { Name = "user" };
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 2, Name = "displayName", ConnectedSystemObjectType = csoType
        };
        var descriptionAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 3, Name = "description", ConnectedSystemObjectType = csoType
        };

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new() { Attribute = displayNameAttr, ChangeType = PendingExportAttributeChangeType.Update, StringValue = "New Name" },
                new() { Attribute = descriptionAttr, ChangeType = PendingExportAttributeChangeType.Update, StringValue = "New Desc" }
            }
        };

        var result = LdapConnectorExport.ConsolidateModifications(pendingExport);

        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public void ConsolidateModifications_RdnAttributes_SkippedAsync()
    {
        var csoType = new ConnectedSystemObjectType { Name = "user" };
        var dnAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = csoType
        };
        var cnAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 2, Name = "cn", ConnectedSystemObjectType = csoType
        };
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 3, Name = "displayName", ConnectedSystemObjectType = csoType
        };

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new() { Attribute = dnAttr, ChangeType = PendingExportAttributeChangeType.Update, StringValue = "CN=New,DC=test,DC=local" },
                new() { Attribute = cnAttr, ChangeType = PendingExportAttributeChangeType.Update, StringValue = "New" },
                new() { Attribute = displayNameAttr, ChangeType = PendingExportAttributeChangeType.Update, StringValue = "Display Name" }
            }
        };

        var result = LdapConnectorExport.ConsolidateModifications(pendingExport);

        // Only displayName should remain — dn and cn are RDN attributes
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].AttributeName, Is.EqualTo("displayName"));
    }

    [Test]
    public void ConsolidateModifications_NullAttribute_SkippedAsync()
    {
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new() { Attribute = null!, ChangeType = PendingExportAttributeChangeType.Update, StringValue = "value" }
            }
        };

        var result = LdapConnectorExport.ConsolidateModifications(pendingExport);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ConsolidateModifications_LargeMemberCount_ConsolidatesAllAsync()
    {
        var pendingExport = CreateMemberAddPendingExport("CN=Group,DC=test,DC=local", 200);

        var result = LdapConnectorExport.ConsolidateModifications(pendingExport);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].AttributeChanges, Has.Count.EqualTo(200));
    }

    #endregion

    #region BuildModifyRequests tests (consolidation + chunking)

    [Test]
    public void BuildModifyRequests_SmallMemberAdd_ReturnsSingleRequestAsync()
    {
        var export = CreateExport(batchSize: 100);
        var pendingExport = CreateMemberAddPendingExport("CN=Group,DC=test,DC=local", 5);

        var requests = export.BuildModifyRequests(pendingExport, "CN=Group,DC=test,DC=local");

        Assert.That(requests, Has.Count.EqualTo(1));
        // All 5 member adds should be consolidated into a single modification
        Assert.That(requests[0].Modifications, Has.Count.EqualTo(1));
        var mod = (DirectoryAttributeModification)requests[0].Modifications[0]!;
        Assert.That(mod.Name, Is.EqualTo("member"));
        Assert.That(mod.Operation, Is.EqualTo(DirectoryAttributeOperation.Add));
        Assert.That(mod.Count, Is.EqualTo(5));
    }

    [Test]
    public void BuildModifyRequests_LargeMemberAdd_ChunksIntoMultipleRequestsAsync()
    {
        var export = CreateExport(batchSize: 50);
        var pendingExport = CreateMemberAddPendingExport("CN=Group,DC=test,DC=local", 120);

        var requests = export.BuildModifyRequests(pendingExport, "CN=Group,DC=test,DC=local");

        // 120 members / 50 batch size = 3 chunks (50 + 50 + 20)
        Assert.That(requests, Has.Count.EqualTo(3));

        // First request gets the first chunk
        var mod1 = (DirectoryAttributeModification)requests[0].Modifications[0]!;
        Assert.That(mod1.Count, Is.EqualTo(50));

        // Second request gets the second chunk
        var mod2 = (DirectoryAttributeModification)requests[1].Modifications[0]!;
        Assert.That(mod2.Count, Is.EqualTo(50));

        // Third request gets the remainder
        var mod3 = (DirectoryAttributeModification)requests[2].Modifications[0]!;
        Assert.That(mod3.Count, Is.EqualTo(20));
    }

    [Test]
    public void BuildModifyRequests_ExactBatchSize_ReturnsSingleRequestAsync()
    {
        var export = CreateExport(batchSize: 100);
        var pendingExport = CreateMemberAddPendingExport("CN=Group,DC=test,DC=local", 100);

        var requests = export.BuildModifyRequests(pendingExport, "CN=Group,DC=test,DC=local");

        Assert.That(requests, Has.Count.EqualTo(1));
        var mod = (DirectoryAttributeModification)requests[0].Modifications[0]!;
        Assert.That(mod.Count, Is.EqualTo(100));
    }

    [Test]
    public void BuildModifyRequests_BatchSizePlusOne_ChunksIntoTwoRequestsAsync()
    {
        var export = CreateExport(batchSize: 100);
        var pendingExport = CreateMemberAddPendingExport("CN=Group,DC=test,DC=local", 101);

        var requests = export.BuildModifyRequests(pendingExport, "CN=Group,DC=test,DC=local");

        Assert.That(requests, Has.Count.EqualTo(2));
        var mod1 = (DirectoryAttributeModification)requests[0].Modifications[0]!;
        var mod2 = (DirectoryAttributeModification)requests[1].Modifications[0]!;
        Assert.That(mod1.Count, Is.EqualTo(100));
        Assert.That(mod2.Count, Is.EqualTo(1));
    }

    [Test]
    public void BuildModifyRequests_NoChanges_ReturnsEmptyListAsync()
    {
        var export = CreateExport(batchSize: 100);
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };

        var requests = export.BuildModifyRequests(pendingExport, "CN=Group,DC=test,DC=local");

        Assert.That(requests, Is.Empty);
    }

    [Test]
    public void BuildModifyRequests_MixedAddsAndRemoves_ChunkedSeparatelyAsync()
    {
        var export = CreateExport(batchSize: 50);
        var pendingExport = CreateMemberMixedPendingExport("CN=Group,DC=test,DC=local", addCount: 80, removeCount: 30);

        var requests = export.BuildModifyRequests(pendingExport, "CN=Group,DC=test,DC=local");

        // 80 adds = 2 chunks (50 + 30), 30 removes = 1 chunk (30)
        // First request: first add chunk (50) + remove chunk (30)
        // Second request: second add chunk (30)
        Assert.That(requests, Has.Count.EqualTo(2));

        // First request should have 2 modifications (first add chunk + removes)
        Assert.That(requests[0].Modifications, Has.Count.EqualTo(2));

        // Second request should have 1 modification (second add chunk)
        Assert.That(requests[1].Modifications, Has.Count.EqualTo(1));
    }

    [Test]
    public void BuildModifyRequests_SingleValuedAttribute_NotChunkedAsync()
    {
        var export = CreateExport(batchSize: 50);
        var csoType = new ConnectedSystemObjectType { Name = "user" };
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 2, Name = "displayName", ConnectedSystemObjectType = csoType
        };

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = CreateCsoWithDn("CN=User,DC=test,DC=local"),
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new() { Attribute = displayNameAttr, ChangeType = PendingExportAttributeChangeType.Update, StringValue = "New Name" }
            }
        };

        var requests = export.BuildModifyRequests(pendingExport, "CN=User,DC=test,DC=local");

        Assert.That(requests, Has.Count.EqualTo(1));
        Assert.That(requests[0].Modifications, Has.Count.EqualTo(1));
    }

    [Test]
    public void BuildModifyRequests_ConsolidatesMultipleMemberAdds_IntoSingleModificationAsync()
    {
        // Verify that 5 separate "member Add" changes become 1 DirectoryAttributeModification with 5 values
        // (not 5 separate modifications with 1 value each)
        var export = CreateExport(batchSize: 100);
        var pendingExport = CreateMemberAddPendingExport("CN=Group,DC=test,DC=local", 5);

        var requests = export.BuildModifyRequests(pendingExport, "CN=Group,DC=test,DC=local");

        Assert.That(requests, Has.Count.EqualTo(1));
        Assert.That(requests[0].Modifications, Has.Count.EqualTo(1)); // 1 consolidated modification, not 5

        var mod = (DirectoryAttributeModification)requests[0].Modifications[0]!;
        Assert.That(mod.Name, Is.EqualTo("member"));
        Assert.That(mod.Operation, Is.EqualTo(DirectoryAttributeOperation.Add));
        Assert.That(mod.Count, Is.EqualTo(5)); // 5 values within the single modification
    }

    #endregion

    #region End-to-end export with chunking tests

    [Test]
    public async Task ExecuteAsync_LargeGroupMemberAdd_SendsMultipleModifyRequestsAsync()
    {
        // 120 member adds with batch size 50 = 3 modify requests
        var pendingExport = CreateMemberAddPendingExport("CN=Group,DC=test,DC=local", 120);

        SetupModifyResponse(ResultCode.Success);

        var export = CreateExport(batchSize: 50, concurrency: 1);
        var results = await export.ExecuteAsync(new List<PendingExport> { pendingExport }, CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True);

        // Should have sent 3 modify requests (50 + 50 + 20)
        _mockExecutor.Verify(e => e.SendRequest(It.IsAny<ModifyRequest>()), Times.Exactly(3));
    }

    [Test]
    public async Task ExecuteAsync_SmallGroupMemberAdd_SendsSingleModifyRequestAsync()
    {
        var pendingExport = CreateMemberAddPendingExport("CN=Group,DC=test,DC=local", 10);

        SetupModifyResponse(ResultCode.Success);

        var export = CreateExport(batchSize: 100, concurrency: 1);
        var results = await export.ExecuteAsync(new List<PendingExport> { pendingExport }, CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True);

        // Should have sent 1 modify request (all 10 members in one consolidated modification)
        _mockExecutor.Verify(e => e.SendRequest(It.IsAny<ModifyRequest>()), Times.Once);
    }

    #endregion

    #region Batch size clamping tests

    [Test]
    public void Constructor_BatchSizeBelowMinimum_ClampedToMinimumAsync()
    {
        var export = CreateExport(batchSize: 1);
        var pendingExport = CreateMemberAddPendingExport("CN=Group,DC=test,DC=local", 15);

        var requests = export.BuildModifyRequests(pendingExport, "CN=Group,DC=test,DC=local");

        // With minimum batch size of 10, 15 members = 2 requests (10 + 5)
        Assert.That(requests, Has.Count.EqualTo(2));
    }

    [Test]
    public void Constructor_BatchSizeAboveMaximum_ClampedToMaximumAsync()
    {
        var export = CreateExport(batchSize: 10000);
        var pendingExport = CreateMemberAddPendingExport("CN=Group,DC=test,DC=local", 5001);

        var requests = export.BuildModifyRequests(pendingExport, "CN=Group,DC=test,DC=local");

        // With maximum batch size of 5000, 5001 members = 2 requests (5000 + 1)
        Assert.That(requests, Has.Count.EqualTo(2));
    }

    #endregion

    #region Helper methods

    private LdapConnectorExport CreateExport(int batchSize = LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE, int concurrency = 1)
    {
        return new LdapConnectorExport(
            _mockExecutor.Object,
            _defaultSettings,
            Log.Logger,
            concurrency,
            batchSize);
    }

    private static PendingExport CreateMemberAddPendingExport(string groupDn, int memberCount)
    {
        var csoType = new ConnectedSystemObjectType { Name = "group" };
        var memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 10,
            Name = "member",
            ConnectedSystemObjectType = csoType,
            AttributePlurality = AttributePlurality.MultiValued
        };

        var changes = new List<PendingExportAttributeValueChange>();
        for (var i = 0; i < memberCount; i++)
        {
            changes.Add(new PendingExportAttributeValueChange
            {
                Attribute = memberAttr,
                ChangeType = PendingExportAttributeChangeType.Add,
                StringValue = $"CN=User{i:D4},OU=Users,DC=test,DC=local"
            });
        }

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = CreateCsoWithDn(groupDn),
            AttributeValueChanges = changes
        };
    }

    private static PendingExport CreateMemberMixedPendingExport(string groupDn, int addCount, int removeCount)
    {
        var csoType = new ConnectedSystemObjectType { Name = "group" };
        var memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 10,
            Name = "member",
            ConnectedSystemObjectType = csoType,
            AttributePlurality = AttributePlurality.MultiValued
        };

        var changes = new List<PendingExportAttributeValueChange>();
        for (var i = 0; i < addCount; i++)
        {
            changes.Add(new PendingExportAttributeValueChange
            {
                Attribute = memberAttr,
                ChangeType = PendingExportAttributeChangeType.Add,
                StringValue = $"CN=NewUser{i:D4},OU=Users,DC=test,DC=local"
            });
        }

        for (var i = 0; i < removeCount; i++)
        {
            changes.Add(new PendingExportAttributeValueChange
            {
                Attribute = memberAttr,
                ChangeType = PendingExportAttributeChangeType.Remove,
                StringValue = $"CN=OldUser{i:D4},OU=Users,DC=test,DC=local"
            });
        }

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = CreateCsoWithDn(groupDn),
            AttributeValueChanges = changes
        };
    }

    private static ConnectedSystemObject CreateCsoWithDn(string dn)
    {
        var csoType = new ConnectedSystemObjectType { Name = "group" };
        var dnAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "distinguishedName",
            ConnectedSystemObjectType = csoType
        };

        return new ConnectedSystemObject
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
        };
    }

    private void SetupModifyResponse(ResultCode resultCode)
    {
        _mockExecutor.Setup(e => e.SendRequest(It.IsAny<ModifyRequest>()))
            .Returns(CreateDirectoryResponse<ModifyResponse>(resultCode));
    }

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
