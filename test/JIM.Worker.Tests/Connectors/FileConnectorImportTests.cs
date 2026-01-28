using JIM.Connectors.File;
using JIM.Models.Core;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class FileConnectorImportTests
{
    private FileConnector _connector = null!;
    private string _testFilesPath = null!;
    private ILogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _connector = new FileConnector();
        _testFilesPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "Csv");
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();
    }

    #region GetSchemaAsync Tests

    [Test]
    public async Task GetSchemaAsync_WithValidCsv_ReturnsCorrectAttributesAsync()
    {
        // Arrange
        var filePath = Path.Combine(_testFilesPath, "valid_users.csv");
        var settingValues = CreateSettingValues(filePath, "User");

        // Act
        var schema = await _connector.GetSchemaAsync(settingValues, _logger);

        // Assert
        Assert.That(schema, Is.Not.Null);
        Assert.That(schema.ObjectTypes, Has.Count.EqualTo(1));
        Assert.That(schema.ObjectTypes[0].Name, Is.EqualTo("User"));
        Assert.That(schema.ObjectTypes[0].Attributes, Has.Count.EqualTo(5));

        var attributeNames = schema.ObjectTypes[0].Attributes.Select(a => a.Name).ToList();
        Assert.That(attributeNames, Does.Contain("Id"));
        Assert.That(attributeNames, Does.Contain("Name"));
        Assert.That(attributeNames, Does.Contain("Age"));
        Assert.That(attributeNames, Does.Contain("StartDate"));
        Assert.That(attributeNames, Does.Contain("IsActive"));
    }

    [Test]
    public async Task GetSchemaAsync_InfersCorrectDataTypesAsync()
    {
        // Arrange
        var filePath = Path.Combine(_testFilesPath, "all_data_types.csv");
        var settingValues = CreateSettingValues(filePath, "TestObject");

        // Act
        var schema = await _connector.GetSchemaAsync(settingValues, _logger);

        // Assert
        Assert.That(schema, Is.Not.Null);
        var objectType = schema.ObjectTypes[0];

        var textAttr = objectType.Attributes.Single(a => a.Name == "TextColumn");
        Assert.That(textAttr.Type, Is.EqualTo(AttributeDataType.Text));

        var numberAttr = objectType.Attributes.Single(a => a.Name == "NumberColumn");
        Assert.That(numberAttr.Type, Is.EqualTo(AttributeDataType.Number));

        var boolAttr = objectType.Attributes.Single(a => a.Name == "BoolColumn");
        Assert.That(boolAttr.Type, Is.EqualTo(AttributeDataType.Boolean));

        var dateAttr = objectType.Attributes.Single(a => a.Name == "DateColumn");
        Assert.That(dateAttr.Type, Is.EqualTo(AttributeDataType.DateTime));

        var guidAttr = objectType.Attributes.Single(a => a.Name == "GuidColumn");
        Assert.That(guidAttr.Type, Is.EqualTo(AttributeDataType.Guid));
    }

    [Test]
    public async Task GetSchemaAsync_WithObjectTypeColumn_DiscoversDifferentTypesAsync()
    {
        // Arrange
        var filePath = Path.Combine(_testFilesPath, "mixed_object_types.csv");
        var settingValues = CreateSettingValuesWithObjectTypeColumn(filePath, "ObjectType");

        // Act
        var schema = await _connector.GetSchemaAsync(settingValues, _logger);

        // Assert
        Assert.That(schema, Is.Not.Null);
        Assert.That(schema.ObjectTypes, Has.Count.EqualTo(2)); // User and Group

        var objectTypeNames = schema.ObjectTypes.Select(ot => ot.Name).ToList();
        Assert.That(objectTypeNames, Does.Contain("User"));
        Assert.That(objectTypeNames, Does.Contain("Group"));

        // Both object types should have the same attributes (minus the object type column)
        foreach (var objectType in schema.ObjectTypes)
        {
            Assert.That(objectType.Attributes.Select(a => a.Name), Does.Contain("Id"));
            Assert.That(objectType.Attributes.Select(a => a.Name), Does.Contain("Name"));
            Assert.That(objectType.Attributes.Select(a => a.Name), Does.Contain("Email"));
        }
    }

    #endregion

    #region ImportAsync Tests

    [Test]
    public async Task ImportAsync_WithValidData_CreatesObjectsAsync()
    {
        // Arrange
        var filePath = Path.Combine(_testFilesPath, "valid_users.csv");
        var connectedSystem = CreateConnectedSystem(filePath, "User");
        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };

        // Act
        var result = await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ImportObjects, Has.Count.EqualTo(3));

        var firstObject = result.ImportObjects[0];
        Assert.That(firstObject.ObjectType, Is.EqualTo("User"));
        Assert.That(firstObject.ErrorType, Is.Null);

        // Verify attributes were parsed
        var idAttr = firstObject.Attributes.Single(a => a.Name == "Id");
        Assert.That(idAttr.IntValues, Has.Count.EqualTo(1));
        Assert.That(idAttr.IntValues[0], Is.EqualTo(1));

        var nameAttr = firstObject.Attributes.Single(a => a.Name == "Name");
        Assert.That(nameAttr.StringValues, Has.Count.EqualTo(1));
        Assert.That(nameAttr.StringValues[0], Is.EqualTo("John Smith"));
    }

    [Test]
    public async Task ImportAsync_WithInvalidNumber_RecordsErrorAsync()
    {
        // Arrange
        var filePath = Path.Combine(_testFilesPath, "invalid_numbers.csv");
        var connectedSystem = CreateConnectedSystem(filePath, "User");
        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };

        // Act
        var result = await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ImportObjects, Has.Count.EqualTo(2));

        // First object should have an error due to "thirty" not being a valid number
        var firstObject = result.ImportObjects[0];
        Assert.That(firstObject.ErrorType, Is.EqualTo(ConnectedSystemImportObjectError.AttributeValueError));
        Assert.That(firstObject.ErrorMessage, Does.Contain("Age"));

        // Second object should be valid
        var secondObject = result.ImportObjects[1];
        Assert.That(secondObject.ErrorType, Is.Null);
    }

    [Test]
    public async Task ImportAsync_WithStopOnFirstError_StopsAfterFirstErrorAsync()
    {
        // Arrange - file has 3 rows, each with an error in a different column
        var filePath = Path.Combine(_testFilesPath, "multiple_errors.csv");
        var connectedSystem = CreateConnectedSystem(filePath, "User", stopOnFirstError: true);
        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };

        // Act
        var result = await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None);

        // Assert - should only have 1 object (the first one with the error), not all 3
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ImportObjects, Has.Count.EqualTo(1));
        Assert.That(result.ImportObjects[0].ErrorType, Is.EqualTo(ConnectedSystemImportObjectError.AttributeValueError));
    }

    [Test]
    public async Task ImportAsync_WithoutStopOnFirstError_ProcessesAllRowsAsync()
    {
        // Arrange - same file but without stop on first error
        var filePath = Path.Combine(_testFilesPath, "multiple_errors.csv");
        var connectedSystem = CreateConnectedSystem(filePath, "User", stopOnFirstError: false);
        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };

        // Act
        var result = await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None);

        // Assert - should have all 3 objects, each with errors
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ImportObjects, Has.Count.EqualTo(3));
    }

    [Test]
    public void ImportAsync_WithMissingFile_ThrowsException()
    {
        // Arrange
        var filePath = Path.Combine(_testFilesPath, "nonexistent.csv");
        var connectedSystem = CreateConnectedSystem(filePath, "User");
        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };

        // Act & Assert
        Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None));
    }

    [Test]
    public async Task ImportAsync_WithCustomDelimiter_ParsesCorrectlyAsync()
    {
        // Arrange
        var filePath = Path.Combine(_testFilesPath, "semicolon_delimiter.csv");
        var connectedSystem = CreateConnectedSystem(filePath, "User", delimiter: ";");
        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };

        // Act
        var result = await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ImportObjects, Has.Count.EqualTo(2));

        var firstObject = result.ImportObjects[0];
        var nameAttr = firstObject.Attributes.Single(a => a.Name == "Name");
        Assert.That(nameAttr.StringValues[0], Is.EqualTo("John Smith"));
    }

    [Test]
    public async Task ImportAsync_WithMultiValuedAttributes_ParsesPipeDelimitedValuesAsync()
    {
        // Arrange
        var filePath = Path.Combine(_testFilesPath, "multivalued_attrs.csv");
        var connectedSystem = CreateConnectedSystemWithMultiValuedAttrs(filePath, "User");
        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };

        // Act
        var result = await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ImportObjects, Has.Count.EqualTo(2));

        // First object should have 3 tags and 3 scores
        var firstObject = result.ImportObjects[0];
        var tagsAttr = firstObject.Attributes.Single(a => a.Name == "Tags");
        Assert.That(tagsAttr.StringValues, Has.Count.EqualTo(3));
        Assert.That(tagsAttr.StringValues, Does.Contain("admin"));
        Assert.That(tagsAttr.StringValues, Does.Contain("user"));
        Assert.That(tagsAttr.StringValues, Does.Contain("developer"));

        var scoresAttr = firstObject.Attributes.Single(a => a.Name == "Scores");
        Assert.That(scoresAttr.IntValues, Has.Count.EqualTo(3));
        Assert.That(scoresAttr.IntValues, Does.Contain(85));
        Assert.That(scoresAttr.IntValues, Does.Contain(90));
        Assert.That(scoresAttr.IntValues, Does.Contain(78));

        // Second object should have 1 tag and 1 score
        var secondObject = result.ImportObjects[1];
        var tagsAttr2 = secondObject.Attributes.Single(a => a.Name == "Tags");
        Assert.That(tagsAttr2.StringValues, Has.Count.EqualTo(1));
        Assert.That(tagsAttr2.StringValues[0], Is.EqualTo("user"));
    }

    [Test]
    public async Task ImportAsync_WithCustomMultiValueDelimiter_ParsesCorrectlyAsync()
    {
        // Arrange - file uses semicolon as multi-value delimiter
        var filePath = Path.Combine(_testFilesPath, "multivalued_semicolon.csv");
        var connectedSystem = CreateConnectedSystemWithMultiValuedAttrs(filePath, "User", multiValueDelimiter: ";");
        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };

        // Act
        var result = await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ImportObjects, Has.Count.EqualTo(2));

        // First object should have 3 tags and 3 scores (semicolon delimited)
        var firstObject = result.ImportObjects[0];
        var tagsAttr = firstObject.Attributes.Single(a => a.Name == "Tags");
        Assert.That(tagsAttr.StringValues, Has.Count.EqualTo(3));
        Assert.That(tagsAttr.StringValues, Does.Contain("admin"));
        Assert.That(tagsAttr.StringValues, Does.Contain("user"));
        Assert.That(tagsAttr.StringValues, Does.Contain("developer"));

        var scoresAttr = firstObject.Attributes.Single(a => a.Name == "Scores");
        Assert.That(scoresAttr.IntValues, Has.Count.EqualTo(3));
        Assert.That(scoresAttr.IntValues, Does.Contain(85));
        Assert.That(scoresAttr.IntValues, Does.Contain(90));
        Assert.That(scoresAttr.IntValues, Does.Contain(78));

        // Second object should have 1 tag and 1 score
        var secondObject = result.ImportObjects[1];
        var tagsAttr2 = secondObject.Attributes.Single(a => a.Name == "Tags");
        Assert.That(tagsAttr2.StringValues, Has.Count.EqualTo(1));
        Assert.That(tagsAttr2.StringValues[0], Is.EqualTo("user"));
    }

    [Test]
    public async Task ImportAsync_WithEmptyDateTime_SkipsAttributeAsync()
    {
        // Arrange - file has rows with empty DateTime values
        var filePath = Path.Combine(_testFilesPath, "empty_values.csv");
        var connectedSystem = CreateConnectedSystem(filePath, "User");
        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };

        // Act
        var result = await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ImportObjects, Has.Count.EqualTo(4));

        // Row 1: Empty Age (Number) - should have no IntValues for Age
        var row1 = result.ImportObjects[0];
        Assert.That(row1.ErrorType, Is.Null, "Row with empty Number should not cause error");
        var ageAttr1 = row1.Attributes.SingleOrDefault(a => a.Name == "Age");
        Assert.That(ageAttr1, Is.Not.Null);
        Assert.That(ageAttr1!.IntValues, Is.Empty, "Empty Number should result in no IntValues");

        // Row 2: Empty StartDate (DateTime) - should have no DateTimeValue
        var row2 = result.ImportObjects[1];
        Assert.That(row2.ErrorType, Is.Null, "Row with empty DateTime should not cause error");
        var dateAttr2 = row2.Attributes.SingleOrDefault(a => a.Name == "StartDate");
        Assert.That(dateAttr2, Is.Not.Null);
        Assert.That(dateAttr2!.DateTimeValue, Is.Null, "Empty DateTime should result in null DateTimeValue");

        // Row 3: Empty IsActive (Boolean) - should have no BoolValue
        var row3 = result.ImportObjects[2];
        Assert.That(row3.ErrorType, Is.Null, "Row with empty Boolean should not cause error");
        var boolAttr3 = row3.Attributes.SingleOrDefault(a => a.Name == "IsActive");
        Assert.That(boolAttr3, Is.Not.Null);
        Assert.That(boolAttr3!.BoolValue, Is.Null, "Empty Boolean should result in null BoolValue");

        // Row 4: All empty (except Name) - should have no errors
        var row4 = result.ImportObjects[3];
        Assert.That(row4.ErrorType, Is.Null, "Row with all empty values should not cause error");
        var nameAttr4 = row4.Attributes.SingleOrDefault(a => a.Name == "Name");
        Assert.That(nameAttr4, Is.Not.Null);
        Assert.That(nameAttr4!.StringValues, Has.Count.EqualTo(1));
        Assert.That(nameAttr4.StringValues[0], Is.EqualTo("Empty All"));
    }

    [Test]
    public async Task ImportAsync_WithEmptyGuid_SkipsAttributeAsync()
    {
        // Arrange - file has a row with an empty GUID value
        var filePath = Path.Combine(_testFilesPath, "empty_guids.csv");
        var connectedSystem = CreateConnectedSystemWithGuidAttr(filePath, "User");
        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };

        // Act
        var result = await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ImportObjects, Has.Count.EqualTo(3));

        // Row 1: Has GUID
        var row1 = result.ImportObjects[0];
        Assert.That(row1.ErrorType, Is.Null);
        var guidAttr1 = row1.Attributes.SingleOrDefault(a => a.Name == "UniqueId");
        Assert.That(guidAttr1, Is.Not.Null);
        Assert.That(guidAttr1!.GuidValues, Has.Count.EqualTo(1));

        // Row 2: Empty GUID - should have no GuidValues, no error
        var row2 = result.ImportObjects[1];
        Assert.That(row2.ErrorType, Is.Null, "Row with empty GUID should not cause error");
        var guidAttr2 = row2.Attributes.SingleOrDefault(a => a.Name == "UniqueId");
        Assert.That(guidAttr2, Is.Not.Null);
        Assert.That(guidAttr2!.GuidValues, Is.Empty, "Empty GUID should result in no GuidValues");

        // Row 3: Has GUID
        var row3 = result.ImportObjects[2];
        Assert.That(row3.ErrorType, Is.Null);
        var guidAttr3 = row3.Attributes.SingleOrDefault(a => a.Name == "UniqueId");
        Assert.That(guidAttr3, Is.Not.Null);
        Assert.That(guidAttr3!.GuidValues, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ImportAsync_WithUnselectedExternalIdAttribute_StillImportsItAsync()
    {
        // Arrange - "Id" attribute is NOT selected but IS the ExternalId.
        // The import should still include it because ExternalId attributes are critical for identity.
        var filePath = Path.Combine(_testFilesPath, "valid_users.csv");
        var objectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "User",
            Selected = true,
            Attributes = new List<ConnectedSystemObjectTypeAttribute>
            {
                new() { Id = 1, Name = "Id", Type = AttributeDataType.Number, Selected = false, IsExternalId = true },
                new() { Id = 2, Name = "Name", Type = AttributeDataType.Text, Selected = true },
                new() { Id = 3, Name = "Age", Type = AttributeDataType.Number, Selected = true },
                new() { Id = 4, Name = "StartDate", Type = AttributeDataType.DateTime, Selected = true },
                new() { Id = 5, Name = "IsActive", Type = AttributeDataType.Boolean, Selected = true }
            }
        };

        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test File Connector",
            ObjectTypes = new List<ConnectedSystemObjectType> { objectType },
            SettingValues = CreateSettingValues(filePath, "User")
        };

        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };

        // Act
        var result = await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None);

        // Assert - the unselected ExternalId attribute should still be imported
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ImportObjects, Has.Count.EqualTo(3));

        var firstObject = result.ImportObjects[0];
        var idAttr = firstObject.Attributes.SingleOrDefault(a => a.Name == "Id");
        Assert.That(idAttr, Is.Not.Null, "ExternalId attribute 'Id' should be imported even though it is not selected");
        Assert.That(idAttr!.IntValues, Has.Count.EqualTo(1));
        Assert.That(idAttr.IntValues[0], Is.EqualTo(1));
    }

    [Test]
    public async Task ImportAsync_WithUnselectedSecondaryExternalIdAttribute_StillImportsItAsync()
    {
        // Arrange - "Name" attribute is NOT selected but IS the SecondaryExternalId.
        // The import should still include it because SecondaryExternalId attributes are needed for export confirmation.
        var filePath = Path.Combine(_testFilesPath, "valid_users.csv");
        var objectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "User",
            Selected = true,
            Attributes = new List<ConnectedSystemObjectTypeAttribute>
            {
                new() { Id = 1, Name = "Id", Type = AttributeDataType.Number, Selected = true, IsExternalId = true },
                new() { Id = 2, Name = "Name", Type = AttributeDataType.Text, Selected = false, IsSecondaryExternalId = true },
                new() { Id = 3, Name = "Age", Type = AttributeDataType.Number, Selected = true },
                new() { Id = 4, Name = "StartDate", Type = AttributeDataType.DateTime, Selected = true },
                new() { Id = 5, Name = "IsActive", Type = AttributeDataType.Boolean, Selected = true }
            }
        };

        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test File Connector",
            ObjectTypes = new List<ConnectedSystemObjectType> { objectType },
            SettingValues = CreateSettingValues(filePath, "User")
        };

        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };

        // Act
        var result = await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None);

        // Assert - the unselected SecondaryExternalId attribute should still be imported
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ImportObjects, Has.Count.EqualTo(3));

        var firstObject = result.ImportObjects[0];
        var nameAttr = firstObject.Attributes.SingleOrDefault(a => a.Name == "Name");
        Assert.That(nameAttr, Is.Not.Null, "SecondaryExternalId attribute 'Name' should be imported even though it is not selected");
        Assert.That(nameAttr!.StringValues, Has.Count.EqualTo(1));
        Assert.That(nameAttr.StringValues[0], Is.EqualTo("John Smith"));
    }

    [Test]
    public async Task ImportAsync_WithSelectedExternalIdAttribute_DoesNotDuplicateItAsync()
    {
        // Arrange - "Id" attribute IS selected AND IS the ExternalId.
        // The DistinctBy should prevent duplication.
        var filePath = Path.Combine(_testFilesPath, "valid_users.csv");
        var objectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "User",
            Selected = true,
            Attributes = new List<ConnectedSystemObjectTypeAttribute>
            {
                new() { Id = 1, Name = "Id", Type = AttributeDataType.Number, Selected = true, IsExternalId = true },
                new() { Id = 2, Name = "Name", Type = AttributeDataType.Text, Selected = true },
                new() { Id = 3, Name = "Age", Type = AttributeDataType.Number, Selected = true },
                new() { Id = 4, Name = "StartDate", Type = AttributeDataType.DateTime, Selected = true },
                new() { Id = 5, Name = "IsActive", Type = AttributeDataType.Boolean, Selected = true }
            }
        };

        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test File Connector",
            ObjectTypes = new List<ConnectedSystemObjectType> { objectType },
            SettingValues = CreateSettingValues(filePath, "User")
        };

        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };

        // Act
        var result = await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None);

        // Assert - "Id" should appear exactly once (not duplicated)
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ImportObjects, Has.Count.EqualTo(3));

        var firstObject = result.ImportObjects[0];
        var idAttributes = firstObject.Attributes.Where(a => a.Name == "Id").ToList();
        Assert.That(idAttributes, Has.Count.EqualTo(1), "ExternalId attribute that is also selected should not be duplicated");
    }

    #endregion

    #region ValidateSettingValues Tests

    [Test]
    public void ValidateSettingValues_WithMissingFile_ReturnsError()
    {
        // Arrange
        var filePath = Path.Combine(_testFilesPath, "nonexistent.csv");
        var settingValues = CreateSettingValues(filePath, "User");

        // Act
        var results = _connector.ValidateSettingValues(settingValues, _logger);

        // Assert
        Assert.That(results, Has.Count.GreaterThan(0));
        Assert.That(results.Any(r => !r.IsValid), Is.True);
    }

    [Test]
    public void ValidateSettingValues_WithValidFile_ReturnsNoErrors()
    {
        // Arrange
        var filePath = Path.Combine(_testFilesPath, "valid_users.csv");
        var settingValues = CreateSettingValues(filePath, "User");

        // Act
        var results = _connector.ValidateSettingValues(settingValues, _logger);

        // Assert
        Assert.That(results.All(r => r.IsValid), Is.True);
    }

    #endregion

    #region Helper Methods

    private List<ConnectedSystemSettingValue> CreateSettingValues(string filePath, string objectType, string delimiter = ",", bool stopOnFirstError = false, string multiValueDelimiter = "|")
    {
        return new List<ConnectedSystemSettingValue>
        {
            new()
            {
                Setting = new ConnectorDefinitionSetting
                {
                    Name = "File Path",
                    Required = true,
                    Type = ConnectedSystemSettingType.File
                },
                StringValue = filePath
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting
                {
                    Name = "Mode",
                    Required = true,
                    Type = ConnectedSystemSettingType.DropDown
                },
                StringValue = "Import Only"
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting
                {
                    Name = "Object Type",
                    Required = false,
                    Type = ConnectedSystemSettingType.String
                },
                StringValue = objectType
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting
                {
                    Name = "Object Type Column",
                    Required = false,
                    Type = ConnectedSystemSettingType.String
                },
                StringValue = null
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting
                {
                    Name = "Delimiter",
                    Required = false,
                    Type = ConnectedSystemSettingType.String
                },
                StringValue = delimiter
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting
                {
                    Name = "Culture",
                    Required = false,
                    Type = ConnectedSystemSettingType.String
                },
                StringValue = null
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting
                {
                    Name = "Stop On First Error",
                    Required = false,
                    Type = ConnectedSystemSettingType.CheckBox
                },
                CheckboxValue = stopOnFirstError
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting
                {
                    Name = "Multi-Value Delimiter",
                    Required = false,
                    Type = ConnectedSystemSettingType.String
                },
                StringValue = multiValueDelimiter
            }
        };
    }

    private ConnectedSystem CreateConnectedSystem(string filePath, string objectTypeName, string delimiter = ",", bool stopOnFirstError = false, string multiValueDelimiter = "|")
    {
        var objectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = objectTypeName,
            Selected = true,
            Attributes = new List<ConnectedSystemObjectTypeAttribute>
            {
                new() { Id = 1, Name = "Id", Type = AttributeDataType.Number, Selected = true },
                new() { Id = 2, Name = "Name", Type = AttributeDataType.Text, Selected = true },
                new() { Id = 3, Name = "Age", Type = AttributeDataType.Number, Selected = true },
                new() { Id = 4, Name = "StartDate", Type = AttributeDataType.DateTime, Selected = true },
                new() { Id = 5, Name = "IsActive", Type = AttributeDataType.Boolean, Selected = true }
            }
        };

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Test File Connector",
            ObjectTypes = new List<ConnectedSystemObjectType> { objectType },
            SettingValues = CreateSettingValues(filePath, objectTypeName, delimiter, stopOnFirstError, multiValueDelimiter)
        };
    }

    private List<ConnectedSystemSettingValue> CreateSettingValuesWithObjectTypeColumn(string filePath, string objectTypeColumn, string delimiter = ",")
    {
        return new List<ConnectedSystemSettingValue>
        {
            new()
            {
                Setting = new ConnectorDefinitionSetting
                {
                    Name = "File Path",
                    Required = true,
                    Type = ConnectedSystemSettingType.File
                },
                StringValue = filePath
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting
                {
                    Name = "Mode",
                    Required = true,
                    Type = ConnectedSystemSettingType.DropDown
                },
                StringValue = "Import Only"
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting
                {
                    Name = "Object Type",
                    Required = false,
                    Type = ConnectedSystemSettingType.String
                },
                StringValue = null // Not using predefined object type
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting
                {
                    Name = "Object Type Column",
                    Required = false,
                    Type = ConnectedSystemSettingType.String
                },
                StringValue = objectTypeColumn // Using column-based object type
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting
                {
                    Name = "Delimiter",
                    Required = false,
                    Type = ConnectedSystemSettingType.String
                },
                StringValue = delimiter
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting
                {
                    Name = "Culture",
                    Required = false,
                    Type = ConnectedSystemSettingType.String
                },
                StringValue = null
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting
                {
                    Name = "Stop On First Error",
                    Required = false,
                    Type = ConnectedSystemSettingType.CheckBox
                },
                CheckboxValue = false
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting
                {
                    Name = "Multi-Value Delimiter",
                    Required = false,
                    Type = ConnectedSystemSettingType.String
                },
                StringValue = "|"
            }
        };
    }

    private ConnectedSystem CreateConnectedSystemWithMultiValuedAttrs(string filePath, string objectTypeName, string multiValueDelimiter = "|")
    {
        var objectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = objectTypeName,
            Selected = true,
            Attributes = new List<ConnectedSystemObjectTypeAttribute>
            {
                new() { Id = 1, Name = "Id", Type = AttributeDataType.Number, Selected = true, AttributePlurality = AttributePlurality.SingleValued },
                new() { Id = 2, Name = "Name", Type = AttributeDataType.Text, Selected = true, AttributePlurality = AttributePlurality.SingleValued },
                new() { Id = 3, Name = "Tags", Type = AttributeDataType.Text, Selected = true, AttributePlurality = AttributePlurality.MultiValued },
                new() { Id = 4, Name = "Scores", Type = AttributeDataType.Number, Selected = true, AttributePlurality = AttributePlurality.MultiValued }
            }
        };

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Test File Connector",
            ObjectTypes = new List<ConnectedSystemObjectType> { objectType },
            SettingValues = CreateSettingValues(filePath, objectTypeName, multiValueDelimiter: multiValueDelimiter)
        };
    }

    private ConnectedSystem CreateConnectedSystemWithGuidAttr(string filePath, string objectTypeName)
    {
        var objectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = objectTypeName,
            Selected = true,
            Attributes = new List<ConnectedSystemObjectTypeAttribute>
            {
                new() { Id = 1, Name = "Id", Type = AttributeDataType.Number, Selected = true, AttributePlurality = AttributePlurality.SingleValued },
                new() { Id = 2, Name = "Name", Type = AttributeDataType.Text, Selected = true, AttributePlurality = AttributePlurality.SingleValued },
                new() { Id = 3, Name = "UniqueId", Type = AttributeDataType.Guid, Selected = true, AttributePlurality = AttributePlurality.SingleValued }
            }
        };

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Test File Connector",
            ObjectTypes = new List<ConnectedSystemObjectType> { objectType },
            SettingValues = CreateSettingValues(filePath, objectTypeName)
        };
    }

    #endregion
}
