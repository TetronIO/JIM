// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Processors;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using NUnit.Framework;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Tests Decimal attribute handling in the import pipeline (#1046): null-value stripping must keep
/// attributes whose only values are decimals, duplicate decimals (including scale variants) must
/// collapse to one value, the CSO create path must materialise DecimalValues into DecimalValue rows,
/// and the update diff must be numeric (a scale-only difference is not a change).
/// </summary>
[TestFixture]
public class ImportDecimalAttributeTests
{
    #region accessors
    private MetaverseObject InitiatedBy { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; } = null!;
    private List<ConnectedSystemRunProfile> ConnectedSystemRunProfilesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemRunProfile>> MockDbSetConnectedSystemRunProfiles { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; } = null!;
    private List<ConnectedSystemPartition> ConnectedSystemPartitionsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemPartition>> MockDbSetConnectedSystemPartitions { get; set; } = null!;
    private List<Activity> ActivitiesData { get; set; } = null!;
    private Mock<DbSet<Activity>> MockDbSetActivities { get; set; } = null!;
    private List<ServiceSetting> ServiceSettingsData { get; set; } = null!;
    private Mock<DbSet<ServiceSetting>> MockDbSetServiceSettings { get; set; } = null!;
    private List<PendingExport> PendingExportsData { get; set; } = null!;
    private Mock<DbSet<PendingExport>> MockDbSetPendingExports { get; set; } = null!;
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;
    #endregion

    private static readonly MethodInfo RemoveNullsMethod = typeof(SyncImportTaskProcessor).GetMethod(
        "RemoveNullImportObjectAttributes",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("RemoveNullImportObjectAttributes method not found via reflection - has it been renamed?");

    private static readonly MethodInfo DeduplicateMethod = typeof(SyncImportTaskProcessor).GetMethod(
        "DeduplicateImportObjectAttributes",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("DeduplicateImportObjectAttributes method not found via reflection - has it been renamed?");

    private static readonly MethodInfo UpdateMethod = typeof(SyncImportTaskProcessor).GetMethod(
        "UpdateConnectedSystemObjectFromImportObject",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("UpdateConnectedSystemObjectFromImportObject method not found via reflection - has it been renamed?");

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
    }

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        InitiatedBy = TestUtilities.GetInitiatedBy();

        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.BuildMockDbSet();

        ConnectedSystemRunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        MockDbSetConnectedSystemRunProfiles = ConnectedSystemRunProfilesData.BuildMockDbSet();

        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.BuildMockDbSet();

        ConnectedSystemPartitionsData = TestUtilities.GetConnectedSystemPartitionData();
        MockDbSetConnectedSystemPartitions = ConnectedSystemPartitionsData.BuildMockDbSet();

        var fullImportRunProfile = ConnectedSystemRunProfilesData[0];
        ActivitiesData = TestUtilities.GetActivityData(fullImportRunProfile.RunType, fullImportRunProfile.Id);
        MockDbSetActivities = ActivitiesData.BuildMockDbSet();

        ServiceSettingsData = TestUtilities.GetServiceSettingsData();
        MockDbSetServiceSettings = ServiceSettingsData.BuildMockDbSet();

        PendingExportsData = new List<PendingExport>();
        MockDbSetPendingExports = PendingExportsData.BuildMockDbSet();

        MockJimDbContext = new Mock<JimDbContext>();
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(MockJimDbContext);
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
        MockJimDbContext.Setup(m => m.ServiceSettingItems).Returns(MockDbSetServiceSettings.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);

        SyncRepo = TestUtilities.CreateSyncRepository(activity: ActivitiesData.First());
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);

        ConnectedSystemObjectsData = new List<ConnectedSystemObject>();
        var mockDbSetConnectedSystemObject = ConnectedSystemObjectsData.BuildMockDbSet();
        mockDbSetConnectedSystemObject.Setup(set => set.AddRange(It.IsAny<IEnumerable<ConnectedSystemObject>>())).Callback((IEnumerable<ConnectedSystemObject> entities) =>
        {
            var connectedSystemObjects = entities as ConnectedSystemObject[] ?? entities.ToArray();
            foreach (var entity in connectedSystemObjects)
                entity.Id = Guid.NewGuid();
            ConnectedSystemObjectsData.AddRange(connectedSystemObjects);
        });
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(mockDbSetConnectedSystemObject.Object);
    }

    #region CSO create path (full pipeline)

    [Test]
    public async Task FullImport_WithDecimalValues_MaterialisesDecimalValueRowsAsync()
    {
        // Arrange: an import object carrying a single-valued and a multi-valued Decimal attribute.
        // The multi-valued list includes a scale variant (2.50 vs 2.5) that must dedupe to one value.
        var mockFileConnector = new MockFileConnector();
        mockFileConnector.TestImportObjects.Add(new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new()
                {
                    Name = MockSourceSystemAttributeNames.HR_ID.ToString(),
                    GuidValues = new List<Guid> { Guid.NewGuid() }
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.DISPLAY_NAME.ToString(),
                    StringValues = new List<string> { "Decimal Import User" }
                },
                new()
                {
                    // Attribute whose ONLY values are decimals: proves the noDecimals guard in
                    // RemoveNullImportObjectAttributes does not strip it from the import.
                    Name = MockSourceSystemAttributeNames.SALARY.ToString(),
                    DecimalValues = new List<decimal> { 51234.56m }
                },
                new()
                {
                    Name = MockSourceSystemAttributeNames.COURSE_FEES.ToString(),
                    DecimalValues = new List<decimal> { 1.5m, 2.50m, 2.5m }
                }
            }
        });

        var connectedSystem = await Jim.ConnectedSystems.GetConnectedSystemAsync(1);
        Assert.That(connectedSystem, Is.Not.Null);
        var runProfile = ConnectedSystemRunProfilesData.Single(q => q.ConnectedSystemId == connectedSystem!.Id && q.RunType == ConnectedSystemRunType.FullImport);
        var activity = ActivitiesData[0];

        // Act
        var syncImportTaskProcessor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new JIM.Application.Servers.SyncEngine(), mockFileConnector, connectedSystem, runProfile,
            TestUtilities.CreateTestWorkerTask(activity, InitiatedBy), new CancellationTokenSource());
        await syncImportTaskProcessor.PerformImportAsync();

        // Assert
        Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(1));
        var createdCso = SyncRepo.ConnectedSystemObjects.Values.First();

        var salaryAttribute = ConnectedSystemObjectTypesData[0].Attributes
            .Single(a => a.Name == MockSourceSystemAttributeNames.SALARY.ToString());
        var salaryValues = createdCso.AttributeValues
            .Where(av => av.AttributeId == salaryAttribute.Id)
            .Select(av => av.DecimalValue)
            .ToList();
        Assert.That(salaryValues, Has.Count.EqualTo(1), "The decimal-only attribute must survive the import (noDecimals guard)");
        Assert.That(salaryValues[0], Is.EqualTo(51234.56m));

        var courseFeesAttribute = ConnectedSystemObjectTypesData[0].Attributes
            .Single(a => a.Name == MockSourceSystemAttributeNames.COURSE_FEES.ToString());
        var courseFeeValues = createdCso.AttributeValues
            .Where(av => av.AttributeId == courseFeesAttribute.Id)
            .Select(av => av.DecimalValue)
            .ToList();
        Assert.That(courseFeeValues, Has.Count.EqualTo(2), "2.50 and 2.5 are numerically equal and must dedupe to one value");
        Assert.That(courseFeeValues, Does.Contain(1.5m));
        Assert.That(courseFeeValues, Does.Contain(2.5m));
    }

    #endregion

    #region RemoveNullImportObjectAttributes (noDecimals guard)

    [Test]
    public void RemoveNullImportObjectAttributes_AttributeWithOnlyDecimalValues_IsKept()
    {
        // Arrange: without the noDecimals term in the all-empty condition, an attribute whose only
        // values are decimals would look empty and be silently stripped from the import (data loss).
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new()
                {
                    Name = MockSourceSystemAttributeNames.SALARY.ToString(),
                    DecimalValues = new List<decimal> { 51234.56m }
                }
            }
        };

        // Act
        RemoveNullsMethod.Invoke(null, new object?[] { importObject });

        // Assert
        Assert.That(importObject.Attributes, Has.Count.EqualTo(1),
            "An attribute whose only values are decimals must not be treated as empty");
        Assert.That(importObject.Attributes[0].DecimalValues, Is.EqualTo(new List<decimal> { 51234.56m }));
    }

    [Test]
    public void RemoveNullImportObjectAttributes_AttributeWithNoValuesAtAll_IsRemoved()
    {
        // Arrange: a genuinely empty attribute (no values of any type) is still stripped.
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = MockSourceSystemAttributeNames.SALARY.ToString() }
            }
        };

        // Act
        RemoveNullsMethod.Invoke(null, new object?[] { importObject });

        // Assert
        Assert.That(importObject.Attributes, Is.Empty);
    }

    #endregion

    #region DeduplicateImportObjectAttributes

    [Test]
    public void DeduplicateImportObjectAttributes_DuplicateDecimals_CollapsesToUniqueSet()
    {
        // Arrange: exact duplicates and scale variants (5.0 vs 5.00 are numerically equal).
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new()
                {
                    Name = MockSourceSystemAttributeNames.COURSE_FEES.ToString(),
                    DecimalValues = new List<decimal> { 5.0m, 1.5m, 5.00m, 1.5m, 2.5m }
                }
            }
        };

        // Act
        DeduplicateMethod.Invoke(null, new object?[] { importObject, "test-external-id" });

        // Assert: 5 values collapse to 3 unique (5.0/5.00 are one value, 1.5 duplicates removed)
        var values = importObject.Attributes[0].DecimalValues;
        Assert.That(values, Has.Count.EqualTo(3));
        Assert.That(values, Does.Contain(5.0m));
        Assert.That(values, Does.Contain(1.5m));
        Assert.That(values, Does.Contain(2.5m));
    }

    [Test]
    public void DeduplicateImportObjectAttributes_UniqueDecimals_RetainsAllValues()
    {
        // Arrange
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new()
                {
                    Name = MockSourceSystemAttributeNames.COURSE_FEES.ToString(),
                    DecimalValues = new List<decimal> { 1.5m, 2.5m, 3.5m }
                }
            }
        };

        // Act
        DeduplicateMethod.Invoke(null, new object?[] { importObject, "test-external-id" });

        // Assert
        Assert.That(importObject.Attributes[0].DecimalValues, Has.Count.EqualTo(3));
    }

    #endregion

    #region UpdateConnectedSystemObjectFromImportObject (numeric diff)

    /// <summary>
    /// Builds a CSO of the SOURCE_USER type holding the supplied decimal values for the given
    /// attribute, plus its HR_ID external identity, ready for a direct update-diff invocation.
    /// </summary>
    private static (ConnectedSystemObject Cso, ConnectedSystemObjectType ObjectType, Guid HrId) BuildCsoWithDecimalValues(
        MockSourceSystemAttributeNames attributeName, params decimal[] values)
    {
        var userObjectType = TestUtilities.GetConnectedSystemObjectTypeData().Single(t => t.Name == "SOURCE_USER");
        var decimalAttribute = userObjectType.Attributes.Single(a => a.Name == attributeName.ToString());
        var hrIdAttribute = userObjectType.Attributes.Single(a => a.IsExternalId);

        var hrId = Guid.NewGuid();
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Type = userObjectType,
            ExternalIdAttributeId = hrIdAttribute.Id
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = hrIdAttribute.Id,
            GuidValue = hrId,
            Attribute = hrIdAttribute,
            ConnectedSystemObject = cso
        });
        foreach (var value in values)
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = decimalAttribute.Id,
                DecimalValue = value,
                Attribute = decimalAttribute,
                ConnectedSystemObject = cso
            });
        }

        return (cso, userObjectType, hrId);
    }

    private static ConnectedSystemImportObject BuildDecimalImportObject(
        Guid hrId, MockSourceSystemAttributeNames attributeName, params decimal[] values)
    {
        return new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = MockSourceSystemAttributeNames.HR_ID.ToString(), GuidValues = new List<Guid> { hrId }, Type = AttributeDataType.Guid },
                new() { Name = attributeName.ToString(), DecimalValues = values.ToList(), Type = AttributeDataType.Decimal }
            }
        };
    }

    [Test]
    public void UpdateConnectedSystemObjectFromImportObject_DecimalScaleOnlyDifference_StagesNoChange()
    {
        // Arrange: CSO holds 5.00, import supplies 5.0. Numerically identical; the diff must be
        // numeric, so no removal or addition may be staged.
        var (cso, objectType, hrId) = BuildCsoWithDecimalValues(MockSourceSystemAttributeNames.SALARY, 5.00m);
        var importObject = BuildDecimalImportObject(hrId, MockSourceSystemAttributeNames.SALARY, 5.0m);
        var rpei = new ActivityRunProfileExecutionItem();

        // Act
        UpdateMethod.Invoke(null, new object?[] { importObject, cso, objectType, rpei, null });

        // Assert
        var salaryName = MockSourceSystemAttributeNames.SALARY.ToString();
        Assert.That(cso.PendingAttributeValueRemovals.Where(av => av.Attribute?.Name == salaryName), Is.Empty,
            "A scale-only difference must not stage a removal");
        Assert.That(cso.PendingAttributeValueAdditions.Where(av => av.Attribute?.Name == salaryName), Is.Empty,
            "A scale-only difference must not stage an addition");
    }

    [Test]
    public void UpdateConnectedSystemObjectFromImportObject_DecimalValueChanged_StagesRemovalAndAddition()
    {
        // Arrange: CSO holds 5.0, import supplies 5.5 - a genuine change.
        var (cso, objectType, hrId) = BuildCsoWithDecimalValues(MockSourceSystemAttributeNames.SALARY, 5.0m);
        var importObject = BuildDecimalImportObject(hrId, MockSourceSystemAttributeNames.SALARY, 5.5m);
        var rpei = new ActivityRunProfileExecutionItem();

        // Act
        UpdateMethod.Invoke(null, new object?[] { importObject, cso, objectType, rpei, null });

        // Assert
        var salaryName = MockSourceSystemAttributeNames.SALARY.ToString();
        var removals = cso.PendingAttributeValueRemovals.Where(av => av.Attribute?.Name == salaryName).ToList();
        var additions = cso.PendingAttributeValueAdditions.Where(av => av.Attribute?.Name == salaryName).ToList();
        Assert.That(removals, Has.Count.EqualTo(1));
        Assert.That(removals[0].DecimalValue, Is.EqualTo(5.0m));
        Assert.That(additions, Has.Count.EqualTo(1));
        Assert.That(additions[0].DecimalValue, Is.EqualTo(5.5m));
    }

    [Test]
    public void UpdateConnectedSystemObjectFromImportObject_DecimalMvaSetDiff_StagesCorrectAddsAndRemoves()
    {
        // Arrange: CSO holds {1.5, 2.5}; import supplies {2.50, 3.5}. 2.50 numerically matches 2.5
        // (kept), 1.5 is obsolete (removed), 3.5 is new (added).
        var (cso, objectType, hrId) = BuildCsoWithDecimalValues(MockSourceSystemAttributeNames.COURSE_FEES, 1.5m, 2.5m);
        var importObject = BuildDecimalImportObject(hrId, MockSourceSystemAttributeNames.COURSE_FEES, 2.50m, 3.5m);
        var rpei = new ActivityRunProfileExecutionItem();

        // Act
        UpdateMethod.Invoke(null, new object?[] { importObject, cso, objectType, rpei, null });

        // Assert
        var courseFeesName = MockSourceSystemAttributeNames.COURSE_FEES.ToString();
        var removals = cso.PendingAttributeValueRemovals.Where(av => av.Attribute?.Name == courseFeesName).ToList();
        var additions = cso.PendingAttributeValueAdditions.Where(av => av.Attribute?.Name == courseFeesName).ToList();
        Assert.That(removals, Has.Count.EqualTo(1));
        Assert.That(removals[0].DecimalValue, Is.EqualTo(1.5m));
        Assert.That(additions, Has.Count.EqualTo(1));
        Assert.That(additions[0].DecimalValue, Is.EqualTo(3.5m));
    }

    #endregion
}
