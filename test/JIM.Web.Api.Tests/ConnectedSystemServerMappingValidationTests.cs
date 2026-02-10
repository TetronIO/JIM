using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for attribute type compatibility validation in ConnectedSystemServer.
/// Validates that sync rule mappings enforce type and plurality compatibility
/// at the Application layer (GH-308).
/// </summary>
[TestFixture]
public class ConnectedSystemServerMappingValidationTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private JimApplication _application = null!;
    private ApiKey _testApiKey = null!;
    private MetaverseObject? _testInitiator;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _application = new JimApplication(_mockRepository.Object);

        _testApiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };

        _testInitiator = new MetaverseObject
        {
            Id = Guid.NewGuid()
        };
        _testInitiator.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            MetaverseObject = _testInitiator,
            Attribute = new MetaverseAttribute { Name = "Display Name", Type = AttributeDataType.Text },
            StringValue = "Test User"
        });
    }

    #region Helper methods

    private static SyncRule CreateImportSyncRule()
    {
        return new SyncRule
        {
            Id = 1,
            Name = "Test Import Rule",
            Direction = SyncRuleDirection.Import
        };
    }

    private static SyncRule CreateExportSyncRule()
    {
        return new SyncRule
        {
            Id = 2,
            Name = "Test Export Rule",
            Direction = SyncRuleDirection.Export
        };
    }

    private static MetaverseAttribute CreateMetaverseAttribute(
        string name,
        AttributeDataType type,
        AttributePlurality plurality = AttributePlurality.SingleValued)
    {
        return new MetaverseAttribute
        {
            Id = Random.Shared.Next(1, 10000),
            Name = name,
            Type = type,
            AttributePlurality = plurality
        };
    }

    private static ConnectedSystemObjectTypeAttribute CreateCsAttribute(
        string name,
        AttributeDataType type,
        AttributePlurality plurality = AttributePlurality.SingleValued)
    {
        return new ConnectedSystemObjectTypeAttribute
        {
            Id = Random.Shared.Next(1, 10000),
            Name = name,
            Type = type,
            AttributePlurality = plurality
        };
    }

    private static SyncRuleMapping CreateImportMapping(
        SyncRule syncRule,
        ConnectedSystemObjectTypeAttribute sourceAttribute,
        MetaverseAttribute targetAttribute)
    {
        var mapping = new SyncRuleMapping
        {
            SyncRule = syncRule,
            TargetMetaverseAttribute = targetAttribute,
            TargetMetaverseAttributeId = targetAttribute.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            Order = 0,
            ConnectedSystemAttribute = sourceAttribute,
            ConnectedSystemAttributeId = sourceAttribute.Id
        });
        return mapping;
    }

    private static SyncRuleMapping CreateExportMapping(
        SyncRule syncRule,
        MetaverseAttribute sourceAttribute,
        ConnectedSystemObjectTypeAttribute targetAttribute)
    {
        var mapping = new SyncRuleMapping
        {
            SyncRule = syncRule,
            TargetConnectedSystemAttribute = targetAttribute,
            TargetConnectedSystemAttributeId = targetAttribute.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            Order = 0,
            MetaverseAttribute = sourceAttribute,
            MetaverseAttributeId = sourceAttribute.Id
        });
        return mapping;
    }

    private static SyncRuleMapping CreateImportExpressionMapping(
        SyncRule syncRule,
        MetaverseAttribute targetAttribute)
    {
        var mapping = new SyncRuleMapping
        {
            SyncRule = syncRule,
            TargetMetaverseAttribute = targetAttribute,
            TargetMetaverseAttributeId = targetAttribute.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            Order = 0,
            Expression = "cs[\"FirstName\"] + \" \" + cs[\"LastName\"]"
        });
        return mapping;
    }

    private static SyncRuleMapping CreateExportExpressionMapping(
        SyncRule syncRule,
        ConnectedSystemObjectTypeAttribute targetAttribute)
    {
        var mapping = new SyncRuleMapping
        {
            SyncRule = syncRule,
            TargetConnectedSystemAttribute = targetAttribute,
            TargetConnectedSystemAttributeId = targetAttribute.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            Order = 0,
            Expression = "mv[\"DisplayName\"]"
        });
        return mapping;
    }

    #endregion

    #region Import - Type compatibility

    [Test]
    public async Task CreateSyncRuleMappingAsync_ImportDirectMapping_MatchingTypes_SucceedsAsync()
    {
        // Arrange
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("csDisplayName", AttributeDataType.Text);
        var targetAttr = CreateMetaverseAttribute("DisplayName", AttributeDataType.Text);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert - should not throw
        await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator);

        _mockConnectedSystemRepo.Verify(
            r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
    }

    [Test]
    public void CreateSyncRuleMappingAsync_ImportDirectMapping_MismatchedTypes_ThrowsArgumentException()
    {
        // Arrange
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("csDisplayName", AttributeDataType.Text);
        var targetAttr = CreateMetaverseAttribute("IsActive", AttributeDataType.Boolean);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator));

        Assert.That(ex!.Message, Does.Contain("Text"));
        Assert.That(ex.Message, Does.Contain("Boolean"));
    }

    [Test]
    public void CreateSyncRuleMappingAsync_ImportDirectMapping_MultiValuedToSingleValued_ThrowsArgumentException()
    {
        // Arrange
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("csGroups", AttributeDataType.Text, AttributePlurality.MultiValued);
        var targetAttr = CreateMetaverseAttribute("Group", AttributeDataType.Text, AttributePlurality.SingleValued);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator));

        Assert.That(ex!.Message, Does.Contain("multi-valued"));
        Assert.That(ex.Message, Does.Contain("single-valued"));
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_ImportDirectMapping_SingleValuedToMultiValued_SucceedsAsync()
    {
        // Arrange
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("csEmail", AttributeDataType.Text, AttributePlurality.SingleValued);
        var targetAttr = CreateMetaverseAttribute("Emails", AttributeDataType.Text, AttributePlurality.MultiValued);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert - should not throw
        await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator);

        _mockConnectedSystemRepo.Verify(
            r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_ImportExpressionMapping_SkipsTypeValidationAsync()
    {
        // Arrange
        var syncRule = CreateImportSyncRule();
        var targetAttr = CreateMetaverseAttribute("DisplayName", AttributeDataType.Text);
        var mapping = CreateImportExpressionMapping(syncRule, targetAttr);

        // Act & Assert - should not throw (expression sources skip type validation)
        await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator);

        _mockConnectedSystemRepo.Verify(
            r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
    }

    #endregion

    #region Export - Type compatibility

    [Test]
    public async Task CreateSyncRuleMappingAsync_ExportDirectMapping_MatchingTypes_SucceedsAsync()
    {
        // Arrange
        var syncRule = CreateExportSyncRule();
        var sourceAttr = CreateMetaverseAttribute("DisplayName", AttributeDataType.Text);
        var targetAttr = CreateCsAttribute("csDisplayName", AttributeDataType.Text);
        var mapping = CreateExportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert - should not throw
        await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator);

        _mockConnectedSystemRepo.Verify(
            r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
    }

    [Test]
    public void CreateSyncRuleMappingAsync_ExportDirectMapping_MismatchedTypes_ThrowsArgumentException()
    {
        // Arrange
        var syncRule = CreateExportSyncRule();
        var sourceAttr = CreateMetaverseAttribute("EmployeeId", AttributeDataType.Number);
        var targetAttr = CreateCsAttribute("csIsActive", AttributeDataType.Boolean);
        var mapping = CreateExportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator));

        Assert.That(ex!.Message, Does.Contain("Number"));
        Assert.That(ex.Message, Does.Contain("Boolean"));
    }

    [Test]
    public void CreateSyncRuleMappingAsync_ExportDirectMapping_MultiValuedToSingleValued_ThrowsArgumentException()
    {
        // Arrange
        var syncRule = CreateExportSyncRule();
        var sourceAttr = CreateMetaverseAttribute("Groups", AttributeDataType.Reference, AttributePlurality.MultiValued);
        var targetAttr = CreateCsAttribute("csGroup", AttributeDataType.Reference, AttributePlurality.SingleValued);
        var mapping = CreateExportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator));

        Assert.That(ex!.Message, Does.Contain("multi-valued"));
        Assert.That(ex.Message, Does.Contain("single-valued"));
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_ExportDirectMapping_SingleValuedToMultiValued_SucceedsAsync()
    {
        // Arrange
        var syncRule = CreateExportSyncRule();
        var sourceAttr = CreateMetaverseAttribute("Email", AttributeDataType.Text, AttributePlurality.SingleValued);
        var targetAttr = CreateCsAttribute("csEmails", AttributeDataType.Text, AttributePlurality.MultiValued);
        var mapping = CreateExportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert - should not throw
        await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator);

        _mockConnectedSystemRepo.Verify(
            r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_ExportExpressionMapping_SkipsTypeValidationAsync()
    {
        // Arrange
        var syncRule = CreateExportSyncRule();
        var targetAttr = CreateCsAttribute("csDisplayName", AttributeDataType.Text);
        var mapping = CreateExportExpressionMapping(syncRule, targetAttr);

        // Act & Assert - should not throw (expression sources skip type validation)
        await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator);

        _mockConnectedSystemRepo.Verify(
            r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
    }

    #endregion

    #region Update - Type compatibility

    [Test]
    public void UpdateSyncRuleMappingAsync_DirectMapping_MismatchedTypes_ThrowsArgumentExceptionAsync()
    {
        // Arrange
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("csStartDate", AttributeDataType.DateTime);
        var targetAttr = CreateMetaverseAttribute("DisplayName", AttributeDataType.Text);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.UpdateSyncRuleMappingAsync(mapping, _testInitiator));

        Assert.That(ex!.Message, Does.Contain("DateTime"));
        Assert.That(ex.Message, Does.Contain("Text"));
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_DirectMapping_MatchingTypes_SucceedsAsync()
    {
        // Arrange
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("csEmail", AttributeDataType.Text);
        var targetAttr = CreateMetaverseAttribute("Email", AttributeDataType.Text);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert - should not throw
        await _application.ConnectedSystems.UpdateSyncRuleMappingAsync(mapping, _testInitiator);

        _mockConnectedSystemRepo.Verify(
            r => r.UpdateSyncRuleMappingAsync(mapping), Times.Once);
    }

    #endregion

    #region NotSet type validation

    [Test]
    public void CreateSyncRuleMappingAsync_ImportSourceTypeNotSet_ThrowsArgumentException()
    {
        // Arrange
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("csUnknown", AttributeDataType.NotSet);
        var targetAttr = CreateMetaverseAttribute("DisplayName", AttributeDataType.Text);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator));

        Assert.That(ex!.Message, Does.Contain("NotSet"));
    }

    [Test]
    public void CreateSyncRuleMappingAsync_ImportTargetTypeNotSet_ThrowsArgumentException()
    {
        // Arrange
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("csDisplayName", AttributeDataType.Text);
        var targetAttr = CreateMetaverseAttribute("Unknown", AttributeDataType.NotSet);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator));

        Assert.That(ex!.Message, Does.Contain("NotSet"));
    }

    [Test]
    public void CreateSyncRuleMappingAsync_ExportSourceTypeNotSet_ThrowsArgumentException()
    {
        // Arrange
        var syncRule = CreateExportSyncRule();
        var sourceAttr = CreateMetaverseAttribute("Unknown", AttributeDataType.NotSet);
        var targetAttr = CreateCsAttribute("csDisplayName", AttributeDataType.Text);
        var mapping = CreateExportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator));

        Assert.That(ex!.Message, Does.Contain("NotSet"));
    }

    [Test]
    public void CreateSyncRuleMappingAsync_ExportTargetTypeNotSet_ThrowsArgumentException()
    {
        // Arrange
        var syncRule = CreateExportSyncRule();
        var sourceAttr = CreateMetaverseAttribute("DisplayName", AttributeDataType.Text);
        var targetAttr = CreateCsAttribute("csUnknown", AttributeDataType.NotSet);
        var mapping = CreateExportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator));

        Assert.That(ex!.Message, Does.Contain("NotSet"));
    }

    #endregion

    #region Error message quality

    [Test]
    public void CreateSyncRuleMappingAsync_TypeMismatch_ErrorMessageIncludesAttributeNames()
    {
        // Arrange
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("EmployeeStartDate", AttributeDataType.DateTime);
        var targetAttr = CreateMetaverseAttribute("IsActive", AttributeDataType.Boolean);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator));

        Assert.That(ex!.Message, Does.Contain("EmployeeStartDate"));
        Assert.That(ex.Message, Does.Contain("IsActive"));
        Assert.That(ex.Message, Does.Contain("DateTime"));
        Assert.That(ex.Message, Does.Contain("Boolean"));
    }

    [Test]
    public void CreateSyncRuleMappingAsync_PluralityMismatch_ErrorMessageIncludesAttributeNames()
    {
        // Arrange
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("csMembers", AttributeDataType.Reference, AttributePlurality.MultiValued);
        var targetAttr = CreateMetaverseAttribute("Manager", AttributeDataType.Reference, AttributePlurality.SingleValued);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator));

        Assert.That(ex!.Message, Does.Contain("csMembers"));
        Assert.That(ex.Message, Does.Contain("Manager"));
        Assert.That(ex.Message, Does.Contain("multi-valued"));
        Assert.That(ex.Message, Does.Contain("single-valued"));
    }

    #endregion

    #region API key overload

    [Test]
    public void CreateSyncRuleMappingAsync_ApiKeyOverload_MismatchedTypes_ThrowsArgumentException()
    {
        // Arrange
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("csDisplayName", AttributeDataType.Text);
        var targetAttr = CreateMetaverseAttribute("IsActive", AttributeDataType.Boolean);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert - verify the API key overload also validates
        var ex = Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testApiKey));

        Assert.That(ex!.Message, Does.Contain("Text"));
        Assert.That(ex.Message, Does.Contain("Boolean"));
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_ApiKeyOverload_MatchingTypes_SucceedsAsync()
    {
        // Arrange
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("csDisplayName", AttributeDataType.Text);
        var targetAttr = CreateMetaverseAttribute("DisplayName", AttributeDataType.Text);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert - should not throw
        await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testApiKey);

        _mockConnectedSystemRepo.Verify(
            r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
    }

    #endregion

    #region All AttributeDataType combinations

    [Test]
    [TestCase(AttributeDataType.Text)]
    [TestCase(AttributeDataType.Number)]
    [TestCase(AttributeDataType.DateTime)]
    [TestCase(AttributeDataType.Binary)]
    [TestCase(AttributeDataType.Reference)]
    [TestCase(AttributeDataType.Guid)]
    [TestCase(AttributeDataType.Boolean)]
    [TestCase(AttributeDataType.LongNumber)]
    public async Task CreateSyncRuleMappingAsync_ImportDirectMapping_SameType_SucceedsAsync(AttributeDataType dataType)
    {
        // Arrange
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("csAttr", dataType);
        var targetAttr = CreateMetaverseAttribute("mvAttr", dataType);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert - should not throw for any matching type
        await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator);

        _mockConnectedSystemRepo.Verify(
            r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
    }

    #endregion
}
