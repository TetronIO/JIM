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
                    Name = "Import File Path",
                    Required = true,
                    Type = ConnectedSystemSettingType.String
                },
                StringValue = filePath
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
                    Name = "Import File Path",
                    Required = true,
                    Type = ConnectedSystemSettingType.String
                },
                StringValue = filePath
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

    #endregion
}
