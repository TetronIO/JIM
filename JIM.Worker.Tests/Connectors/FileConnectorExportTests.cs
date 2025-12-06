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
        _testExportPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestOutput", "Export");
        _logger = new LoggerConfiguration().CreateLogger();

        // Ensure export directory exists and is clean
        if (Directory.Exists(_testExportPath))
            Directory.Delete(_testExportPath, true);
        Directory.CreateDirectory(_testExportPath);
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();

        // Clean up export directory
        if (Directory.Exists(_testExportPath))
            Directory.Delete(_testExportPath, true);
    }

    #region Export Tests

    [Test]
    public void Export_WithNoPendingExports_CreatesNoFile()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = new List<PendingExport>();

        // Act
        _connector.Export(settingValues, pendingExports);

        // Assert
        var files = Directory.GetFiles(_testExportPath);
        Assert.That(files, Is.Empty);
    }

    [Test]
    public void Export_WithPendingExports_CreatesCsvFile()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateSamplePendingExports();

        // Act
        _connector.Export(settingValues, pendingExports);

        // Assert
        var files = Directory.GetFiles(_testExportPath, "*.csv");
        Assert.That(files, Has.Length.EqualTo(1));
        Assert.That(files[0], Does.EndWith("export.csv"));
    }

    [Test]
    public void Export_WithTimestampedFiles_IncludesTimestampInFilename()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath, timestampedFiles: true);
        var pendingExports = CreateSamplePendingExports();

        // Act
        _connector.Export(settingValues, pendingExports);

        // Assert
        var files = Directory.GetFiles(_testExportPath, "*.csv");
        Assert.That(files, Has.Length.EqualTo(1));

        var filename = Path.GetFileName(files[0]);
        Assert.That(filename, Does.StartWith("export_"));
        Assert.That(filename, Does.Match(@"export_\d{8}_\d{6}\.csv"));
    }

    [Test]
    public void Export_WithSeparateFilesByObjectType_CreatesMultipleFiles()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath, separateByObjectType: true);
        var pendingExports = CreateMixedTypePendingExports();

        // Act
        _connector.Export(settingValues, pendingExports);

        // Assert
        var files = Directory.GetFiles(_testExportPath, "*.csv");
        Assert.That(files, Has.Length.EqualTo(2)); // User.csv and Group.csv

        var filenames = files.Select(Path.GetFileName).ToList();
        Assert.That(filenames, Does.Contain("User.csv"));
        Assert.That(filenames, Does.Contain("Group.csv"));
    }

    [Test]
    public void Export_WritesCsvWithCorrectHeaders()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateSamplePendingExports();

        // Act
        _connector.Export(settingValues, pendingExports);

        // Assert
        var files = Directory.GetFiles(_testExportPath, "*.csv");
        var lines = File.ReadAllLines(files[0]);

        Assert.That(lines, Has.Length.GreaterThan(0));
        var header = lines[0];
        Assert.That(header, Does.Contain("_objectType"));
        Assert.That(header, Does.Contain("_externalId"));
        Assert.That(header, Does.Contain("_changeType"));
    }

    [Test]
    public void Export_WritesCsvWithCorrectData()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateSamplePendingExports();

        // Act
        _connector.Export(settingValues, pendingExports);

        // Assert
        var files = Directory.GetFiles(_testExportPath, "*.csv");
        var content = File.ReadAllText(files[0]);

        Assert.That(content, Does.Contain("User"));
        Assert.That(content, Does.Contain("Create"));
        Assert.That(content, Does.Contain("John Smith"));
        Assert.That(content, Does.Contain("jsmith@example.com"));
    }

    [Test]
    public void Export_WithCustomDelimiter_UsesCorrectDelimiter()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath, delimiter: ";");
        var pendingExports = CreateSamplePendingExports();

        // Act
        _connector.Export(settingValues, pendingExports);

        // Assert
        var files = Directory.GetFiles(_testExportPath, "*.csv");
        var header = File.ReadLines(files[0]).First();

        // Header should use semicolon delimiter
        Assert.That(header, Does.Contain(";"));
        Assert.That(header.Split(';'), Has.Length.GreaterThan(3)); // At least _objectType;_externalId;_changeType;...
    }

    [Test]
    public void Export_WithMissingExportPath_ThrowsException()
    {
        // Arrange
        var settingValues = new List<ConnectedSystemSettingValue>
        {
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "Export File Path" },
                StringValue = null
            }
        };
        var pendingExports = CreateSamplePendingExports();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _connector.Export(settingValues, pendingExports));
    }

    [Test]
    public void Export_WithUpdateChangeType_IncludesExternalId()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateUpdatePendingExports();

        // Act
        _connector.Export(settingValues, pendingExports);

        // Assert
        var files = Directory.GetFiles(_testExportPath, "*.csv");
        var lines = File.ReadAllLines(files[0]);

        Assert.That(lines, Has.Length.GreaterThan(1));
        var dataLine = lines[1];
        Assert.That(dataLine, Does.Contain("Update"));
        Assert.That(dataLine, Does.Contain("EXT-001")); // External ID from CSO
    }

    [Test]
    public void Export_WithDeleteChangeType_WritesDeleteRecord()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateDeletePendingExports();

        // Act
        _connector.Export(settingValues, pendingExports);

        // Assert
        var files = Directory.GetFiles(_testExportPath, "*.csv");
        var content = File.ReadAllText(files[0]);

        Assert.That(content, Does.Contain("Delete"));
    }

    #endregion

    #region Capabilities Tests

    [Test]
    public void SupportsExport_ReturnsTrue()
    {
        // Assert
        Assert.That(_connector.SupportsExport, Is.True);
    }

    [Test]
    public void SupportsAutoConfirmExport_ReturnsTrue()
    {
        // Assert
        Assert.That(_connector.SupportsAutoConfirmExport, Is.True);
    }

    #endregion

    #region Helper Methods

    private List<ConnectedSystemSettingValue> CreateExportSettingValues(
        string exportPath,
        string delimiter = ",",
        string multiValueDelimiter = "|",
        bool timestampedFiles = false,
        bool separateByObjectType = false,
        bool includeFullState = false,
        bool autoConfirmExports = true)
    {
        return new List<ConnectedSystemSettingValue>
        {
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "Export File Path" },
                StringValue = exportPath
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
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "Timestamped Files" },
                CheckboxValue = timestampedFiles
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "Separate Files Per Object Type" },
                CheckboxValue = separateByObjectType
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "Include Full State" },
                CheckboxValue = includeFullState
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "Auto-Confirm Exports" },
                CheckboxValue = autoConfirmExports
            }
        };
    }

    private List<PendingExport> CreateSamplePendingExports()
    {
        var objectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "User"
        };

        var displayNameAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "displayName",
            Type = AttributeDataType.Text,
            ConnectedSystemObjectType = objectType
        };

        var emailAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 2,
            Name = "email",
            Type = AttributeDataType.Text,
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
                        Attribute = displayNameAttr,
                        StringValue = "John Smith"
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Attribute = emailAttr,
                        StringValue = "jsmith@example.com"
                    }
                }
            }
        };
    }

    private List<PendingExport> CreateMixedTypePendingExports()
    {
        var userType = new ConnectedSystemObjectType { Id = 1, Name = "User" };
        var groupType = new ConnectedSystemObjectType { Id = 2, Name = "Group" };

        var displayNameAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "displayName",
            Type = AttributeDataType.Text,
            ConnectedSystemObjectType = userType
        };

        var groupNameAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 2,
            Name = "groupName",
            Type = AttributeDataType.Text,
            ConnectedSystemObjectType = groupType
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
                        Attribute = displayNameAttr,
                        StringValue = "John Smith"
                    }
                }
            },
            new()
            {
                Id = Guid.NewGuid(),
                ChangeType = PendingExportChangeType.Create,
                AttributeValueChanges = new List<PendingExportAttributeValueChange>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Attribute = groupNameAttr,
                        StringValue = "Admins"
                    }
                }
            }
        };
    }

    private List<PendingExport> CreateUpdatePendingExports()
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "User" };

        var displayNameAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "displayName",
            Type = AttributeDataType.Text,
            ConnectedSystemObjectType = objectType
        };

        var externalIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 100,
            Name = "externalId",
            Type = AttributeDataType.Text,
            ConnectedSystemObjectType = objectType
        };

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = objectType,
            ExternalIdAttributeId = 100, // Point to the externalId attribute
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new()
                {
                    Attribute = externalIdAttr,
                    StringValue = "EXT-001"
                }
            }
        };

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
                        Attribute = displayNameAttr,
                        StringValue = "John Updated"
                    }
                }
            }
        };
    }

    private List<PendingExport> CreateDeletePendingExports()
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "User" };

        var externalIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 100,
            Name = "externalId",
            Type = AttributeDataType.Text,
            ConnectedSystemObjectType = objectType
        };

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = objectType,
            ExternalIdAttributeId = 100,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new()
                {
                    Attribute = externalIdAttr,
                    StringValue = "EXT-DEL-001"
                }
            }
        };

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
