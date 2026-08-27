// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
/// Validates that Synchronisation Rule mappings enforce type and plurality compatibility
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

        // Auto-assign priority (#91) reads the target attribute's contributor list on every import-mapping create.
        // These tests exercise mapping validation, not priority, so default the list to empty: the new mapping is the
        // sole contributor, auto-assign no-ops, and the safe-addition sentinel is left untouched.
        _mockConnectedSystemRepo
            .Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<SyncRuleMapping>());

        // The duplicate-target check (#1532) reads the Synchronisation Rule's existing mappings on every
        // mapping create/update. Default to none; the duplicate-target tests override this per-test.
        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleMappingsAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<SyncRuleMapping>());

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
    public async Task CreateSyncRuleMappingAsync_ImportDirectMapping_MultiValuedToSingleValued_SucceedsAsync()
    {
        // Arrange — MVA to SVA is now allowed; the runtime selects the first value and warns (#435)
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("csGroups", AttributeDataType.Text, AttributePlurality.MultiValued);
        var targetAttr = CreateMetaverseAttribute("Group", AttributeDataType.Text, AttributePlurality.SingleValued);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert — should not throw
        await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator);

        _mockConnectedSystemRepo.Verify(
            r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
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
    public async Task CreateSyncRuleMappingAsync_ExportDirectMapping_MultiValuedToSingleValued_SucceedsAsync()
    {
        // Arrange — MVA to SVA is now allowed; the runtime selects the first value and warns (#435)
        var syncRule = CreateExportSyncRule();
        var sourceAttr = CreateMetaverseAttribute("Groups", AttributeDataType.Reference, AttributePlurality.MultiValued);
        var targetAttr = CreateCsAttribute("csGroup", AttributeDataType.Reference, AttributePlurality.SingleValued);
        var mapping = CreateExportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert — should not throw
        await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator);

        _mockConnectedSystemRepo.Verify(
            r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
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
    public async Task CreateSyncRuleMappingAsync_PluralityMismatch_MultiValuedToSingleValued_SucceedsAsync()
    {
        // Arrange — MVA to SVA is now allowed (#435). The runtime handles truncation with a warning.
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("csMembers", AttributeDataType.Reference, AttributePlurality.MultiValued);
        var targetAttr = CreateMetaverseAttribute("Manager", AttributeDataType.Reference, AttributePlurality.SingleValued);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert — should not throw
        await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator);

        _mockConnectedSystemRepo.Verify(
            r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
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

    #region Export - Target writability

    /// <summary>
    /// A read-only target cannot be written at all, so authoring an export Attribute Flow to it is refused.
    /// </summary>
    [Test]
    public void CreateSyncRuleMappingAsync_ExportDirectMapping_ReadOnlyTarget_ThrowsArgumentException()
    {
        // Arrange
        var syncRule = CreateExportSyncRule();
        var sourceAttr = CreateMetaverseAttribute("Display Name", AttributeDataType.Text);
        var targetAttr = CreateCsAttribute("whenCreated", AttributeDataType.Text);
        targetAttr.Writability = AttributeWritability.ReadOnly;
        var mapping = CreateExportMapping(syncRule, sourceAttr, targetAttr);

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator));
        Assert.That(ex!.Message, Does.Contain("read-only"));
    }

    /// <summary>
    /// A Writable On Create target must be accepted at authoring time: the value has to flow during
    /// provisioning, or the object can never be created. Keeping it out of Update Pending Exports is the
    /// export path's job, not the authoring validator's.
    /// </summary>
    [Test]
    public async Task CreateSyncRuleMappingAsync_ExportDirectMapping_WritableOnCreateTarget_SucceedsAsync()
    {
        // Arrange
        var syncRule = CreateExportSyncRule();
        var sourceAttr = CreateMetaverseAttribute("Employee ID", AttributeDataType.Text);
        var targetAttr = CreateCsAttribute("employee_number", AttributeDataType.Text);
        targetAttr.Writability = AttributeWritability.WritableOnCreate;
        var mapping = CreateExportMapping(syncRule, sourceAttr, targetAttr);

        // Act
        await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator);

        // Assert
        _mockConnectedSystemRepo.Verify(
            r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
    }

    /// <summary>
    /// The same acceptance must hold when an existing mapping is updated, not only when one is created.
    /// </summary>
    [Test]
    public async Task UpdateSyncRuleMappingAsync_ExportDirectMapping_WritableOnCreateTarget_SucceedsAsync()
    {
        // Arrange
        var syncRule = CreateExportSyncRule();
        var sourceAttr = CreateMetaverseAttribute("Employee ID", AttributeDataType.Text);
        var targetAttr = CreateCsAttribute("employee_number", AttributeDataType.Text);
        targetAttr.Writability = AttributeWritability.WritableOnCreate;
        var mapping = CreateExportMapping(syncRule, sourceAttr, targetAttr);

        // Act
        await _application.ConnectedSystems.UpdateSyncRuleMappingAsync(mapping, _testInitiator);

        // Assert
        _mockConnectedSystemRepo.Verify(
            r => r.UpdateSyncRuleMappingAsync(mapping), Times.Once);
    }

    #endregion

    #region Duplicate target validation (#1532)

    /// <summary>
    /// Builds a persisted-looking mapping already targeting the given Metaverse Attribute, for stubbing the
    /// Synchronisation Rule's existing-mappings list.
    /// </summary>
    private static SyncRuleMapping CreateExistingImportMapping(int id, MetaverseAttribute targetAttribute, bool enabled = true)
    {
        return new SyncRuleMapping
        {
            Id = id,
            SyncRuleId = 1,
            TargetMetaverseAttribute = targetAttribute,
            TargetMetaverseAttributeId = targetAttribute.Id,
            Enabled = enabled
        };
    }

    [Test]
    public void CreateSyncRuleMappingAsync_ImportMapping_TargetAlreadyMapped_ThrowsArgumentException()
    {
        // Arrange: the rule already flows another source to the same Metaverse Attribute.
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("roomNumber", AttributeDataType.Text);
        var targetAttr = CreateMetaverseAttribute("Badge Colour", AttributeDataType.Text);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleMappingsAsync(syncRule.Id))
            .ReturnsAsync(new List<SyncRuleMapping> { CreateExistingImportMapping(50, targetAttr) });

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain("Test Import Rule"), "the message must name the Synchronisation Rule");
            Assert.That(ex.Message, Does.Contain("Badge Colour"), "the message must name the target attribute");
            Assert.That(ex.Message, Does.Contain("??"), "the message must offer an expression mapping as the in-rule fallback");
            Assert.That(ex.Message, Does.Contain("Synchronisation Rule"), "the message must offer a second, differently-scoped Synchronisation Rule for priority");
        }
        _mockConnectedSystemRepo.Verify(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    [Test]
    public void CreateSyncRuleMappingAsync_ImportMapping_DisabledDuplicate_ThrowsArgumentException()
    {
        // Arrange: a disabled duplicate still counts; re-enabling it later would recreate the trap.
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("roomNumber", AttributeDataType.Text);
        var targetAttr = CreateMetaverseAttribute("Badge Colour", AttributeDataType.Text);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleMappingsAsync(syncRule.Id))
            .ReturnsAsync(new List<SyncRuleMapping> { CreateExistingImportMapping(50, targetAttr, enabled: false) });

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator));
        _mockConnectedSystemRepo.Verify(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_ImportMapping_DifferentTarget_SucceedsAsync()
    {
        // Arrange: an existing mapping targets a different attribute, so the new one is fine.
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("roomNumber", AttributeDataType.Text);
        var targetAttr = CreateMetaverseAttribute("Badge Colour", AttributeDataType.Text);
        var otherAttr = CreateMetaverseAttribute("Display Name", AttributeDataType.Text);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleMappingsAsync(syncRule.Id))
            .ReturnsAsync(new List<SyncRuleMapping> { CreateExistingImportMapping(50, otherAttr) });

        // Act & Assert - should not throw
        await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator);

        _mockConnectedSystemRepo.Verify(r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
    }

    [Test]
    public void CreateSyncRuleMappingAsync_ExportMapping_TargetAlreadyMapped_ThrowsArgumentException()
    {
        // Arrange: the export-side sibling; the collision key is the target Connected System attribute.
        var syncRule = CreateExportSyncRule();
        var sourceAttr = CreateMetaverseAttribute("Display Name", AttributeDataType.Text);
        var targetAttr = CreateCsAttribute("displayName", AttributeDataType.Text);
        var mapping = CreateExportMapping(syncRule, sourceAttr, targetAttr);

        var existing = new SyncRuleMapping
        {
            Id = 51,
            SyncRuleId = syncRule.Id,
            TargetConnectedSystemAttribute = targetAttr,
            TargetConnectedSystemAttributeId = targetAttr.Id
        };
        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleMappingsAsync(syncRule.Id))
            .ReturnsAsync(new List<SyncRuleMapping> { existing });

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testInitiator));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain("Test Export Rule"));
            Assert.That(ex.Message, Does.Contain("displayName"));
        }
        _mockConnectedSystemRepo.Verify(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    [Test]
    public void CreateSyncRuleMappingAsync_ApiKeyOverload_TargetAlreadyMapped_ThrowsArgumentException()
    {
        // Arrange: both initiator overloads share the check.
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("roomNumber", AttributeDataType.Text);
        var targetAttr = CreateMetaverseAttribute("Badge Colour", AttributeDataType.Text);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);

        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleMappingsAsync(syncRule.Id))
            .ReturnsAsync(new List<SyncRuleMapping> { CreateExistingImportMapping(50, targetAttr) });

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _testApiKey));
        _mockConnectedSystemRepo.Verify(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    [Test]
    public void UpdateSyncRuleMappingAsync_RetargetedOntoExistingTarget_ThrowsArgumentException()
    {
        // Arrange: an update can move a mapping's target onto an attribute another mapping already flows to.
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("roomNumber", AttributeDataType.Text);
        var targetAttr = CreateMetaverseAttribute("Badge Colour", AttributeDataType.Text);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);
        mapping.Id = 60;

        var itselfBeforeTheRetarget = CreateExistingImportMapping(60, CreateMetaverseAttribute("Room Number", AttributeDataType.Text));
        var otherMapping = CreateExistingImportMapping(61, targetAttr);
        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleMappingsAsync(syncRule.Id))
            .ReturnsAsync(new List<SyncRuleMapping> { itselfBeforeTheRetarget, otherMapping });

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.UpdateSyncRuleMappingAsync(mapping, _testInitiator));
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_OwnTargetUnchanged_SucceedsAsync()
    {
        // Arrange: the mapping's own persisted row must not read as a collision with itself.
        var syncRule = CreateImportSyncRule();
        var sourceAttr = CreateCsAttribute("roomNumber", AttributeDataType.Text);
        var targetAttr = CreateMetaverseAttribute("Badge Colour", AttributeDataType.Text);
        var mapping = CreateImportMapping(syncRule, sourceAttr, targetAttr);
        mapping.Id = 60;

        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleMappingsAsync(syncRule.Id))
            .ReturnsAsync(new List<SyncRuleMapping> { CreateExistingImportMapping(60, targetAttr) });

        // Act & Assert - should not throw
        await _application.ConnectedSystems.UpdateSyncRuleMappingAsync(mapping, _testInitiator);

        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleMappingAsync(mapping), Times.Once);
    }

    /// <summary>
    /// Builds a Synchronisation Rule that passes <see cref="SyncRule.IsValid"/>, so the whole-rule save path
    /// reaches the duplicate-target check rather than returning false first.
    /// </summary>
    private static SyncRule CreateValidImportSyncRule()
    {
        return new SyncRule
        {
            Id = 5,
            Name = "HR Import Rule",
            Direction = SyncRuleDirection.Import,
            ConnectedSystem = new ConnectedSystem { Id = 3, Name = "HR" },
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 7, Name = "person" },
            MetaverseObjectType = new MetaverseObjectType { Id = 9, Name = "User" }
        };
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_DuplicateTargetsInRule_ThrowsArgumentException()
    {
        // Arrange: the whole-rule save path (the portal's Attribute Flow dialog composes the collection
        // in-memory and saves the rule) must refuse two mappings targeting the same attribute.
        var syncRule = CreateValidImportSyncRule();
        var targetAttr = CreateMetaverseAttribute("Badge Colour", AttributeDataType.Text);
        syncRule.AttributeFlowRules.Add(CreateImportMapping(syncRule, CreateCsAttribute("jimBadgeColour", AttributeDataType.Text), targetAttr));
        syncRule.AttributeFlowRules.Add(CreateImportMapping(syncRule, CreateCsAttribute("roomNumber", AttributeDataType.Text), targetAttr));

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _testInitiator));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain("HR Import Rule"));
            Assert.That(ex.Message, Does.Contain("Badge Colour"));
            Assert.That(ex.Message, Does.Contain("??"));
        }
        _mockConnectedSystemRepo.Verify(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_ApiKeyOverload_DuplicateTargetsInRule_ThrowsArgumentException()
    {
        // Arrange
        var syncRule = CreateValidImportSyncRule();
        var targetAttr = CreateMetaverseAttribute("Badge Colour", AttributeDataType.Text);
        syncRule.AttributeFlowRules.Add(CreateImportMapping(syncRule, CreateCsAttribute("jimBadgeColour", AttributeDataType.Text), targetAttr));
        syncRule.AttributeFlowRules.Add(CreateImportMapping(syncRule, CreateCsAttribute("roomNumber", AttributeDataType.Text), targetAttr));

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _testApiKey));
        _mockConnectedSystemRepo.Verify(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_DuplicateDisabledMappingInRule_ThrowsArgumentException()
    {
        // Arrange: a disabled duplicate in the collection still counts.
        var syncRule = CreateValidImportSyncRule();
        var targetAttr = CreateMetaverseAttribute("Badge Colour", AttributeDataType.Text);
        var disabledDuplicate = CreateImportMapping(syncRule, CreateCsAttribute("jimBadgeColour", AttributeDataType.Text), targetAttr);
        disabledDuplicate.Enabled = false;
        syncRule.AttributeFlowRules.Add(disabledDuplicate);
        syncRule.AttributeFlowRules.Add(CreateImportMapping(syncRule, CreateCsAttribute("roomNumber", AttributeDataType.Text), targetAttr));

        // Act & Assert
        Assert.ThrowsAsync<ArgumentException>(
            async () => await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _testInitiator));
        _mockConnectedSystemRepo.Verify(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
    }

    #endregion
}
