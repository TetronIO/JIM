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

        _testExportPath = Path.Combine(testOutputDir, "export.csv");
        _logger = new LoggerConfiguration().CreateLogger();

        // Clean up any existing test file
        if (File.Exists(_testExportPath))
            File.Delete(_testExportPath);
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();

        // Clean up export file
        if (File.Exists(_testExportPath))
            File.Delete(_testExportPath);
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
        Assert.That(File.Exists(_testExportPath), Is.False);
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
        Assert.That(File.Exists(_testExportPath), Is.True);
        Assert.That(_testExportPath, Does.EndWith("export.csv"));
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
        var lines = File.ReadAllLines(_testExportPath);

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
        var content = File.ReadAllText(_testExportPath);

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
        var header = File.ReadLines(_testExportPath).First();

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
                Setting = new ConnectorDefinitionSetting { Name = "File Path" },
                StringValue = null
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "Mode" },
                StringValue = "Export Only"
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
        var lines = File.ReadAllLines(_testExportPath);

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
        var content = File.ReadAllText(_testExportPath);

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
        bool includeFullState = false)
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
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "Include Full State" },
                CheckboxValue = includeFullState
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
