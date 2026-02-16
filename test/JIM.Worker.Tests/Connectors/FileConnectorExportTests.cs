using JIM.Connectors.File;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class FileConnectorExportTests
{
    private FileConnector _connector = null!;
    private string _testExportPath = null!;
    private ILogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _connector = new FileConnector();
        var testOutputDir = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestOutput");

        // Ensure output directory exists
        if (!Directory.Exists(testOutputDir))
            Directory.CreateDirectory(testOutputDir);

        _testExportPath = Path.Combine(testOutputDir, $"export_{Guid.NewGuid():N}.csv");
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();

        // Clean up export file
        if (File.Exists(_testExportPath))
            File.Delete(_testExportPath);
    }

    #region No-op and Error Tests

    [Test]
    public void Export_WithNoPendingExports_CreatesNoFile()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = new List<PendingExport>();

        // Act
        var results = _connector.Export(settingValues, pendingExports);

        // Assert
        Assert.That(File.Exists(_testExportPath), Is.False);
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Export_WithMissingFilePath_ThrowsException()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "File Path" },
                StringValue = null
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "Delimiter" },
                StringValue = ","
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "Multi-Value Delimiter" },
                StringValue = "|"
            }
        };
        var pendingExports = CreateSingleCreatePendingExport("emp001");

        // Act & Assert
        Assert.Throws<JIM.Models.Exceptions.InvalidSettingValuesException>(() => _connector.Export(settingValues, pendingExports));
    }

    [Test]
    public void Export_WithNoExternalIdAttribute_ReturnsFailedResults()
    {
        // Arrange - create exports with no IsExternalId attribute
        var settingValues = CreateExportSettingValues(_testExportPath);
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "User" };
        var attr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "displayName",
            Type = AttributeDataType.Text,
            IsExternalId = false,
            ConnectedSystemObjectType = objectType
        };

        var pendingExports = new List<PendingExport>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ChangeType = PendingExportChangeType.Create,
                AttributeValueChanges = new List<PendingExportAttributeValueChange>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Attribute = attr,
                        AttributeId = attr.Id,
                        StringValue = "John Smith"
                    }
                }
            }
        };

        // Act
        var results = _connector.Export(settingValues, pendingExports);

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.False);
        Assert.That(results[0].ErrorMessage, Does.Contain("No External ID attribute"));
    }

    #endregion

    #region Create Export Tests

    [Test]
    public void Export_Create_WritesFileWithCorrectHeaders()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateSingleCreatePendingExport("emp001");

        // Act
        _connector.Export(settingValues, pendingExports);

        // Assert
        var lines = File.ReadAllLines(_testExportPath);
        Assert.That(lines, Has.Length.GreaterThan(0));

        var header = lines[0];
        // Should contain attribute names, NOT system columns
        Assert.That(header, Does.Contain("displayName"));
        Assert.That(header, Does.Contain("email"));
        Assert.That(header, Does.Contain("employeeId"));

        // Must NOT contain system columns from old export
        Assert.That(header, Does.Not.Contain("_objectType"));
        Assert.That(header, Does.Not.Contain("_externalId"));
        Assert.That(header, Does.Not.Contain("_changeType"));
    }

    [Test]
    public void Export_Create_WritesCorrectData()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateSingleCreatePendingExport("emp001");

        // Act
        _connector.Export(settingValues, pendingExports);

        // Assert
        var content = File.ReadAllText(_testExportPath);
        Assert.That(content, Does.Contain("John Smith"));
        Assert.That(content, Does.Contain("jsmith@example.com"));
        Assert.That(content, Does.Contain("emp001"));
    }

    [Test]
    public void Export_Create_ReturnsSuccessWithExternalId()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateSingleCreatePendingExport("emp001");

        // Act
        var results = _connector.Export(settingValues, pendingExports);

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True);
        Assert.That(results[0].ExternalId, Is.EqualTo("emp001"));
        Assert.That(results[0].ErrorMessage, Is.Null);
    }

    [Test]
    public void Export_Create_WithNoExternalIdValue_ReturnsFailed()
    {
        // Arrange - create export where external ID attribute has no value
        var settingValues = CreateExportSettingValues(_testExportPath);
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "User" };
        var externalIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 100, Name = "employeeId", Type = AttributeDataType.Text,
            IsExternalId = true, ConnectedSystemObjectType = objectType
        };
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1, Name = "displayName", Type = AttributeDataType.Text,
            ConnectedSystemObjectType = objectType
        };

        var pendingExports = new List<PendingExport>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ChangeType = PendingExportChangeType.Create,
                AttributeValueChanges = new List<PendingExportAttributeValueChange>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Attribute = externalIdAttr,
                        AttributeId = externalIdAttr.Id,
                        StringValue = null // No value!
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Attribute = displayNameAttr,
                        AttributeId = displayNameAttr.Id,
                        StringValue = "John Smith"
                    }
                }
            }
        };

        // Act
        var results = _connector.Export(settingValues, pendingExports);

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.False);
        Assert.That(results[0].ErrorMessage, Does.Contain("no value for External ID"));
    }

    [Test]
    public void Export_MultipleCreates_WritesAllRows()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = new List<PendingExport>();
        pendingExports.AddRange(CreateSingleCreatePendingExport("emp001", "John Smith", "jsmith@example.com"));
        pendingExports.AddRange(CreateSingleCreatePendingExport("emp002", "Jane Doe", "jdoe@example.com"));
        pendingExports.AddRange(CreateSingleCreatePendingExport("emp003", "Bob Wilson", "bwilson@example.com"));

        // Act
        var results = _connector.Export(settingValues, pendingExports);

        // Assert
        Assert.That(results, Has.Count.EqualTo(3));
        Assert.That(results.All(r => r.Success), Is.True);

        var lines = File.ReadAllLines(_testExportPath);
        Assert.That(lines, Has.Length.EqualTo(4)); // 1 header + 3 data rows

        var content = File.ReadAllText(_testExportPath);
        Assert.That(content, Does.Contain("emp001"));
        Assert.That(content, Does.Contain("emp002"));
        Assert.That(content, Does.Contain("emp003"));
    }

    #endregion

    #region Update Export Tests

    [Test]
    public void Export_Update_MergesWithExistingFile()
    {
        // Arrange - create initial file with one row
        var settingValues = CreateExportSettingValues(_testExportPath);
        var createExports = CreateSingleCreatePendingExport("emp001", "John Smith", "jsmith@example.com");
        _connector.Export(settingValues, createExports);

        // Act - update the display name
        var updateExports = CreateSingleUpdatePendingExport("emp001", "displayName", "John Updated");
        var results = _connector.Export(settingValues, updateExports);

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True);
        Assert.That(results[0].ExternalId, Is.EqualTo("emp001"));

        var content = File.ReadAllText(_testExportPath);
        Assert.That(content, Does.Contain("John Updated"));
        Assert.That(content, Does.Not.Contain("John Smith")); // Old value replaced
    }

    [Test]
    public void Export_Update_PreservesUnchangedAttributes()
    {
        // Arrange - create initial file with one row
        var settingValues = CreateExportSettingValues(_testExportPath);
        var createExports = CreateSingleCreatePendingExport("emp001", "John Smith", "jsmith@example.com");
        _connector.Export(settingValues, createExports);

        // Act - update only display name, email should be preserved
        var updateExports = CreateSingleUpdatePendingExport("emp001", "displayName", "John Updated");
        _connector.Export(settingValues, updateExports);

        // Assert
        var content = File.ReadAllText(_testExportPath);
        Assert.That(content, Does.Contain("jsmith@example.com")); // Preserved from original
        Assert.That(content, Does.Contain("John Updated"));
    }

    [Test]
    public void Export_Update_PreservesOtherRows()
    {
        // Arrange - create initial file with two rows
        var settingValues = CreateExportSettingValues(_testExportPath);
        var createExports = new List<PendingExport>();
        createExports.AddRange(CreateSingleCreatePendingExport("emp001", "John Smith", "jsmith@example.com"));
        createExports.AddRange(CreateSingleCreatePendingExport("emp002", "Jane Doe", "jdoe@example.com"));
        _connector.Export(settingValues, createExports);

        // Act - update only emp001
        var updateExports = CreateSingleUpdatePendingExport("emp001", "displayName", "John Updated");
        _connector.Export(settingValues, updateExports);

        // Assert - emp002 should still be there
        var content = File.ReadAllText(_testExportPath);
        Assert.That(content, Does.Contain("emp002"));
        Assert.That(content, Does.Contain("Jane Doe"));
        Assert.That(content, Does.Contain("John Updated"));
    }

    #endregion

    #region Delete Export Tests

    [Test]
    public void Export_Delete_RemovesRowFromFile()
    {
        // Arrange - create initial file with two rows
        var settingValues = CreateExportSettingValues(_testExportPath);
        var createExports = new List<PendingExport>();
        createExports.AddRange(CreateSingleCreatePendingExport("emp001", "John Smith", "jsmith@example.com"));
        createExports.AddRange(CreateSingleCreatePendingExport("emp002", "Jane Doe", "jdoe@example.com"));
        _connector.Export(settingValues, createExports);

        // Act - delete emp001
        var deleteExports = CreateSingleDeletePendingExport("emp001");
        var results = _connector.Export(settingValues, deleteExports);

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True);

        var content = File.ReadAllText(_testExportPath);
        Assert.That(content, Does.Not.Contain("emp001"));
        Assert.That(content, Does.Not.Contain("John Smith"));
        Assert.That(content, Does.Contain("emp002")); // Other row preserved
        Assert.That(content, Does.Contain("Jane Doe"));
    }

    [Test]
    public void Export_Delete_ForNonExistentRow_StillSucceeds()
    {
        // Arrange - create file with one row
        var settingValues = CreateExportSettingValues(_testExportPath);
        var createExports = CreateSingleCreatePendingExport("emp001", "John Smith", "jsmith@example.com");
        _connector.Export(settingValues, createExports);

        // Act - delete a row that doesn't exist
        var deleteExports = CreateSingleDeletePendingExport("emp999");
        var results = _connector.Export(settingValues, deleteExports);

        // Assert - should still succeed (idempotent)
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True);
    }

    #endregion

    #region Mixed Operations Tests

    [Test]
    public void Export_MixedOperations_HandlesCreateUpdateDelete()
    {
        // Arrange - create initial file with two rows
        var settingValues = CreateExportSettingValues(_testExportPath);
        var createExports = new List<PendingExport>();
        createExports.AddRange(CreateSingleCreatePendingExport("emp001", "John Smith", "jsmith@example.com"));
        createExports.AddRange(CreateSingleCreatePendingExport("emp002", "Jane Doe", "jdoe@example.com"));
        _connector.Export(settingValues, createExports);

        // Act - create emp003, update emp001, delete emp002
        var mixedExports = new List<PendingExport>();
        mixedExports.AddRange(CreateSingleCreatePendingExport("emp003", "Bob Wilson", "bwilson@example.com"));
        mixedExports.AddRange(CreateSingleUpdatePendingExport("emp001", "displayName", "John Updated"));
        mixedExports.AddRange(CreateSingleDeletePendingExport("emp002"));
        var results = _connector.Export(settingValues, mixedExports);

        // Assert
        Assert.That(results, Has.Count.EqualTo(3));
        Assert.That(results.All(r => r.Success), Is.True);

        var content = File.ReadAllText(_testExportPath);
        Assert.That(content, Does.Contain("emp001"));
        Assert.That(content, Does.Contain("John Updated"));
        Assert.That(content, Does.Contain("emp003"));
        Assert.That(content, Does.Contain("Bob Wilson"));
        Assert.That(content, Does.Not.Contain("emp002"));
        Assert.That(content, Does.Not.Contain("Jane Doe"));
    }

    #endregion

    #region Delimiter Tests

    [Test]
    public void Export_WithCustomDelimiter_UsesCorrectDelimiter()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath, delimiter: ";");
        var pendingExports = CreateSingleCreatePendingExport("emp001");

        // Act
        _connector.Export(settingValues, pendingExports);

        // Assert
        var header = File.ReadLines(_testExportPath).First();
        Assert.That(header, Does.Contain(";"));
        Assert.That(header.Split(';'), Has.Length.GreaterThanOrEqualTo(3));
    }

    #endregion

    #region Full-State File Tests

    [Test]
    public void Export_WithNoExistingFile_CreatesNewFile()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateSingleCreatePendingExport("emp001");

        // Act
        _connector.Export(settingValues, pendingExports);

        // Assert
        Assert.That(File.Exists(_testExportPath), Is.True);
        var lines = File.ReadAllLines(_testExportPath);
        Assert.That(lines, Has.Length.EqualTo(2)); // Header + 1 data row
    }

    [Test]
    public void Export_CreateForExistingExternalId_TreatsAsUpdate()
    {
        // Arrange - create initial file
        var settingValues = CreateExportSettingValues(_testExportPath);
        var createExports = CreateSingleCreatePendingExport("emp001", "John Smith", "jsmith@example.com");
        _connector.Export(settingValues, createExports);

        // Act - create again with same External ID (should overwrite)
        var duplicateCreate = CreateSingleCreatePendingExport("emp001", "John Updated", "jupdated@example.com");
        var results = _connector.Export(settingValues, duplicateCreate);

        // Assert
        Assert.That(results[0].Success, Is.True);
        var lines = File.ReadAllLines(_testExportPath);
        Assert.That(lines, Has.Length.EqualTo(2)); // Still just 1 data row, not duplicated

        var content = File.ReadAllText(_testExportPath);
        Assert.That(content, Does.Contain("John Updated"));
        Assert.That(content, Does.Not.Contain("John Smith"));
    }

    [Test]
    public void Export_HeadersOrderedAlphabetically()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateSingleCreatePendingExport("emp001");

        // Act
        _connector.Export(settingValues, pendingExports);

        // Assert
        var header = File.ReadLines(_testExportPath).First();
        var columns = header.Split(',');

        // Columns should be in alphabetical order
        var sortedColumns = columns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.That(columns, Is.EqualTo(sortedColumns));
    }

    #endregion

    #region Attribute Type Tests

    [Test]
    public void Export_Create_HandlesIntegerValues()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "User" };
        var externalIdAttr = CreateExternalIdAttribute(objectType);
        var ageAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 3, Name = "age", Type = AttributeDataType.Number,
            ConnectedSystemObjectType = objectType
        };

        var pendingExports = new List<PendingExport>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ChangeType = PendingExportChangeType.Create,
                AttributeValueChanges = new List<PendingExportAttributeValueChange>
                {
                    new() { Id = Guid.NewGuid(), Attribute = externalIdAttr, AttributeId = externalIdAttr.Id, StringValue = "emp001" },
                    new() { Id = Guid.NewGuid(), Attribute = ageAttr, AttributeId = ageAttr.Id, IntValue = 42 }
                }
            }
        };

        // Act
        _connector.Export(settingValues, pendingExports);

        // Assert
        var content = File.ReadAllText(_testExportPath);
        Assert.That(content, Does.Contain("42"));
    }

    [Test]
    public void Export_Create_HandlesBooleanValues()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "User" };
        var externalIdAttr = CreateExternalIdAttribute(objectType);
        var activeAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 3, Name = "active", Type = AttributeDataType.Boolean,
            ConnectedSystemObjectType = objectType
        };

        var pendingExports = new List<PendingExport>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ChangeType = PendingExportChangeType.Create,
                AttributeValueChanges = new List<PendingExportAttributeValueChange>
                {
                    new() { Id = Guid.NewGuid(), Attribute = externalIdAttr, AttributeId = externalIdAttr.Id, StringValue = "emp001" },
                    new() { Id = Guid.NewGuid(), Attribute = activeAttr, AttributeId = activeAttr.Id, BoolValue = true }
                }
            }
        };

        // Act
        _connector.Export(settingValues, pendingExports);

        // Assert
        var content = File.ReadAllText(_testExportPath);
        Assert.That(content, Does.Contain("true"));
    }

    [Test]
    public void Export_Create_HandlesDateTimeValues()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "User" };
        var externalIdAttr = CreateExternalIdAttribute(objectType);
        var hiredAtAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 3, Name = "hiredAt", Type = AttributeDataType.DateTime,
            ConnectedSystemObjectType = objectType
        };

        var hiredDate = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var pendingExports = new List<PendingExport>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ChangeType = PendingExportChangeType.Create,
                AttributeValueChanges = new List<PendingExportAttributeValueChange>
                {
                    new() { Id = Guid.NewGuid(), Attribute = externalIdAttr, AttributeId = externalIdAttr.Id, StringValue = "emp001" },
                    new() { Id = Guid.NewGuid(), Attribute = hiredAtAttr, AttributeId = hiredAtAttr.Id, DateTimeValue = hiredDate }
                }
            }
        };

        // Act
        _connector.Export(settingValues, pendingExports);

        // Assert
        var content = File.ReadAllText(_testExportPath);
        Assert.That(content, Does.Contain("2025-06-15")); // ISO 8601 format
    }

    #endregion

    #region Remove Attribute Tests

    [Test]
    public void Export_Update_RemoveAttribute_SetsEmptyValue()
    {
        // Arrange - create initial file
        var settingValues = CreateExportSettingValues(_testExportPath);
        var createExports = CreateSingleCreatePendingExport("emp001", "John Smith", "jsmith@example.com");
        _connector.Export(settingValues, createExports);

        // Act - remove email attribute
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "User" };
        var externalIdAttr = CreateExternalIdAttribute(objectType);
        var emailAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 2, Name = "email", Type = AttributeDataType.Text,
            ConnectedSystemObjectType = objectType
        };

        var cso = CreateCsoWithExternalId("emp001", objectType, externalIdAttr);

        var updateExports = new List<PendingExport>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ChangeType = PendingExportChangeType.Update,
                ConnectedSystemObject = cso,
                AttributeValueChanges = new List<PendingExportAttributeValueChange>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Attribute = emailAttr,
                        AttributeId = emailAttr.Id,
                        ChangeType = PendingExportAttributeChangeType.Remove
                    }
                }
            }
        };

        _connector.Export(settingValues, updateExports);

        // Assert - the row should still exist but email should be empty
        var lines = File.ReadAllLines(_testExportPath);
        Assert.That(lines, Has.Length.GreaterThan(1));

        // emp001 row should not contain jsmith@example.com
        var content = File.ReadAllText(_testExportPath);
        Assert.That(content, Does.Contain("emp001"));
        Assert.That(content, Does.Not.Contain("jsmith@example.com"));
    }

    #endregion

    #region Capabilities Tests

    [Test]
    public void SupportsExport_ReturnsTrue()
    {
        Assert.That(_connector.SupportsExport, Is.True);
    }

    [Test]
    public void SupportsAutoConfirmExport_ReturnsTrue()
    {
        Assert.That(_connector.SupportsAutoConfirmExport, Is.True);
    }

    [Test]
    public void GetSettings_DoesNotIncludeFullStateSetting()
    {
        var settings = _connector.GetSettings();
        Assert.That(settings.Any(s => s.Name == "Include Full State"), Is.False);
    }

    #endregion

    #region Helper Methods

    private static List<ConnectedSystemSettingValue> CreateExportSettingValues(
        string exportPath,
        string delimiter = ",",
        string multiValueDelimiter = "|")
    {
        return new List<ConnectedSystemSettingValue>
        {
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "File Path" },
                StringValue = exportPath
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "Mode" },
                StringValue = "Export Only"
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "Delimiter" },
                StringValue = delimiter
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "Multi-Value Delimiter" },
                StringValue = multiValueDelimiter
            }
        };
    }

    private static ConnectedSystemObjectTypeAttribute CreateExternalIdAttribute(ConnectedSystemObjectType objectType)
    {
        return new ConnectedSystemObjectTypeAttribute
        {
            Id = 100,
            Name = "employeeId",
            Type = AttributeDataType.Text,
            IsExternalId = true,
            ConnectedSystemObjectType = objectType
        };
    }

    private static ConnectedSystemObject CreateCsoWithExternalId(
        string externalId,
        ConnectedSystemObjectType objectType,
        ConnectedSystemObjectTypeAttribute externalIdAttr)
    {
        // Ensure the object type's Attributes collection contains the external ID attribute
        // so FindExternalIdAttributeName can discover it via the CSO fallback path
        if (!objectType.Attributes.Any(a => a.Id == externalIdAttr.Id))
            objectType.Attributes.Add(externalIdAttr);

        return new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = objectType,
            ExternalIdAttributeId = externalIdAttr.Id,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new()
                {
                    Attribute = externalIdAttr,
                    AttributeId = externalIdAttr.Id,
                    StringValue = externalId
                }
            }
        };
    }

    private static List<PendingExport> CreateSingleCreatePendingExport(
        string externalId,
        string displayName = "John Smith",
        string email = "jsmith@example.com")
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "User" };
        var externalIdAttr = CreateExternalIdAttribute(objectType);

        var displayNameAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1, Name = "displayName", Type = AttributeDataType.Text,
            ConnectedSystemObjectType = objectType
        };

        var emailAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 2, Name = "email", Type = AttributeDataType.Text,
            ConnectedSystemObjectType = objectType
        };

        return new List<PendingExport>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ChangeType = PendingExportChangeType.Create,
                AttributeValueChanges = new List<PendingExportAttributeValueChange>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Attribute = externalIdAttr,
                        AttributeId = externalIdAttr.Id,
                        StringValue = externalId
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Attribute = displayNameAttr,
                        AttributeId = displayNameAttr.Id,
                        StringValue = displayName
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Attribute = emailAttr,
                        AttributeId = emailAttr.Id,
                        StringValue = email
                    }
                }
            }
        };
    }

    private static List<PendingExport> CreateSingleUpdatePendingExport(
        string externalId,
        string attributeName,
        string newValue)
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "User" };
        var externalIdAttr = CreateExternalIdAttribute(objectType);
        var updatedAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1, Name = attributeName, Type = AttributeDataType.Text,
            ConnectedSystemObjectType = objectType
        };

        var cso = CreateCsoWithExternalId(externalId, objectType, externalIdAttr);

        return new List<PendingExport>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ChangeType = PendingExportChangeType.Update,
                ConnectedSystemObject = cso,
                AttributeValueChanges = new List<PendingExportAttributeValueChange>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Attribute = updatedAttr,
                        AttributeId = updatedAttr.Id,
                        StringValue = newValue
                    }
                }
            }
        };
    }

    private static List<PendingExport> CreateSingleDeletePendingExport(string externalId)
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "User" };
        var externalIdAttr = CreateExternalIdAttribute(objectType);
        var cso = CreateCsoWithExternalId(externalId, objectType, externalIdAttr);

        return new List<PendingExport>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ChangeType = PendingExportChangeType.Delete,
                ConnectedSystemObject = cso,
                AttributeValueChanges = new List<PendingExportAttributeValueChange>()
            }
        };
    }

    #endregion
}
