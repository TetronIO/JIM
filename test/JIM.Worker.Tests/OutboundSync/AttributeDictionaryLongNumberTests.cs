// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.PostgresData;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Verifies that the expression-context attribute dictionaries used by export evaluation and
/// Drift Detection expose every attribute data type, including LongNumber. A missing switch arm
/// silently presents the attribute as null inside expressions, so expression-based mappings
/// evaluate against a phantom missing value rather than failing loudly.
/// </summary>
public class AttributeDictionaryLongNumberTests
{
    private Mock<JimDbContext> _mockJimDbContext = null!;
    private JimApplication _jim = null!;
    private SyncRepository _syncRepo = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        _mockJimDbContext = new Mock<JimDbContext>();
        _syncRepo = TestUtilities.CreateSyncRepository();
        _jim = new JimApplication(new PostgresDataRepository(_mockJimDbContext.Object), syncRepository: _syncRepo);
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    /// <summary>
    /// Builds an MVO with one attribute value per numeric data type, each with the Attribute
    /// navigation populated, so the dictionary builders can resolve names and types.
    /// </summary>
    private static MetaverseObject BuildMvoWithTypedValues()
    {
        var textAttr = new MetaverseAttribute { Id = 1, Name = "displayName", Type = AttributeDataType.Text };
        var numberAttr = new MetaverseAttribute { Id = 2, Name = "employeeNumber", Type = AttributeDataType.Number };
        var longAttr = new MetaverseAttribute { Id = 3, Name = "usnChanged", Type = AttributeDataType.LongNumber };
        var decimalAttr = new MetaverseAttribute { Id = 4, Name = "salary", Type = AttributeDataType.Decimal };

        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 1, Name = "User" }
        };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = textAttr, AttributeId = 1, StringValue = "Jo Bloggs" });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = numberAttr, AttributeId = 2, IntValue = 42 });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = longAttr, AttributeId = 3, LongValue = 9999999999L });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = decimalAttr, AttributeId = 4, DecimalValue = 1.5m });
        return mvo;
    }

    [Test]
    public void ExportEvaluationServer_BuildAttributeDictionary_LongNumberAttribute_ExposedAsLong()
    {
        // Arrange
        var server = new ExportEvaluationServer(_jim, _syncRepo);
        var mvo = BuildMvoWithTypedValues();

        // Act
        var dictionary = server.BuildAttributeDictionary(mvo);

        // Assert - a long beyond int range proves no narrowing occurred anywhere
        Assert.That(dictionary["usnChanged"], Is.EqualTo(9999999999L));
        Assert.That(dictionary["usnChanged"], Is.TypeOf<long>());
        Assert.That(dictionary["displayName"], Is.EqualTo("Jo Bloggs"));
        Assert.That(dictionary["employeeNumber"], Is.EqualTo(42));
        Assert.That(dictionary["salary"], Is.EqualTo(1.5m));
    }

    [Test]
    public void DriftDetectionService_BuildAttributeDictionary_LongNumberAttribute_ExposedAsLong()
    {
        // Arrange
        var mvo = BuildMvoWithTypedValues();

        // Act
        var dictionary = DriftDetectionService.BuildAttributeDictionary(mvo);

        // Assert
        Assert.That(dictionary["usnChanged"], Is.EqualTo(9999999999L));
        Assert.That(dictionary["usnChanged"], Is.TypeOf<long>());
        Assert.That(dictionary["displayName"], Is.EqualTo("Jo Bloggs"));
        Assert.That(dictionary["employeeNumber"], Is.EqualTo(42));
        Assert.That(dictionary["salary"], Is.EqualTo(1.5m));
    }
}
