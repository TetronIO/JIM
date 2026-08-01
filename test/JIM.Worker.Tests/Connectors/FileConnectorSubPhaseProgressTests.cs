// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Connectors.File;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Verifies the File connector narrates its internal sub-phases through the optional progress callback,
/// so that the Activity message keeps moving during long-running file work instead of appearing stuck.
/// </summary>
[TestFixture]
public class FileConnectorSubPhaseProgressTests
{
    private FileConnector _connector = null!;
    private string _testDirectory = null!;
    private string _testExportPath = null!;
    private ILogger _logger = null!;
    private readonly List<string> _importFilePaths = [];

    [SetUp]
    public void SetUp()
    {
        _connector = new FileConnector();
        _testDirectory = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestOutput");
        if (!Directory.Exists(_testDirectory))
            Directory.CreateDirectory(_testDirectory);

        _testExportPath = Path.Combine(_testDirectory, $"export_{Guid.NewGuid():N}.csv");
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();

        if (File.Exists(_testExportPath))
            File.Delete(_testExportPath);

        foreach (var importFilePath in _importFilePaths.Where(File.Exists))
            File.Delete(importFilePath);

        _importFilePaths.Clear();
    }

    #region Export

    [Test]
    public async Task ExportAsync_WithProgressCallback_ReportsLoadMergeAndWriteInOrderAsync()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateCreatePendingExports("emp001", "emp002");
        var messages = new List<string>();

        // Act
        await _connector.ExportAsync(settingValues, pendingExports, CancellationToken.None,
            message =>
            {
                messages.Add(message);
                return Task.CompletedTask;
            });

        // Assert
        Assert.That(messages, Is.EqualTo(new[]
        {
            "Loading existing export file...",
            "Merging 2 changes into file...",
            "Writing 2 rows to output file..."
        }));
    }

    [Test]
    public async Task ExportAsync_WithNoPendingExports_ReportsNoProgressAsync()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var messages = new List<string>();

        // Act
        await _connector.ExportAsync(settingValues, new List<PendingExport>(), CancellationToken.None,
            message =>
            {
                messages.Add(message);
                return Task.CompletedTask;
            });

        // Assert
        Assert.That(messages, Is.Empty, "Nothing to export means no file work to narrate");
    }

    [Test]
    public async Task ExportAsync_WithoutProgressCallback_StillExportsAsync()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateCreatePendingExports("emp001");

        // Act
        var results = await _connector.ExportAsync(settingValues, pendingExports, CancellationToken.None);

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True);
        Assert.That(File.Exists(_testExportPath), Is.True);
    }

    [Test]
    public async Task ExportAsync_WhenProgressCallbackThrows_StillExportsAsync()
    {
        // Arrange - the connector reports progress directly; the caller is responsible for guarding
        // its own delegate (ConnectorSubPhaseProgress does this), so a throwing delegate here proves
        // the connector does not swallow export results on a progress failure.
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateCreatePendingExports("emp001");
        using var progress = new ConnectorSubPhaseProgress(
            _ => throw new InvalidOperationException("activity update failed"));

        // Act
        var results = await _connector.ExportAsync(settingValues, pendingExports, CancellationToken.None, progress.Callback);

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True);
        Assert.That(File.Exists(_testExportPath), Is.True);
    }

    #endregion

    #region Import

    [Test]
    public async Task ImportAsync_WithProgressCallback_ReportsReadingThenParsedRowsAsync()
    {
        // Arrange - enough rows to cross the periodic parse-progress interval more than once
        var filePath = CreateCsvFile(rowCount: 25_000);
        var connectedSystem = CreateConnectedSystem(filePath);
        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };
        var messages = new List<string>();

        // Act
        var result = await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None,
            message =>
            {
                messages.Add(message);
                return Task.CompletedTask;
            });

        // Assert
        Assert.That(result.ImportObjects, Has.Count.EqualTo(25_000));
        Assert.That(messages[0], Is.EqualTo("Reading CSV file..."));
        Assert.That(messages.Skip(1), Is.EqualTo(new[]
        {
            "Parsed 10,000 rows...",
            "Parsed 20,000 rows...",
            "Parsed 25,000 rows..."
        }), "Row progress must advance during the read, and finish on the true total");
    }

    [Test]
    public async Task ImportAsync_WithFewerRowsThanTheProgressInterval_ReportsTheFinalCountAsync()
    {
        // Arrange
        var filePath = CreateCsvFile(rowCount: 3);
        var connectedSystem = CreateConnectedSystem(filePath);
        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };
        var messages = new List<string>();

        // Act
        await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None,
            message =>
            {
                messages.Add(message);
                return Task.CompletedTask;
            });

        // Assert
        Assert.That(messages, Is.EqualTo(new[] { "Reading CSV file...", "Parsed 3 rows..." }));
    }

    [Test]
    public async Task ImportAsync_WithoutProgressCallback_StillImportsAsync()
    {
        // Arrange
        var filePath = CreateCsvFile(rowCount: 5);
        var connectedSystem = CreateConnectedSystem(filePath);
        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };

        // Act
        var result = await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None);

        // Assert
        Assert.That(result.ImportObjects, Has.Count.EqualTo(5));
    }

    #endregion

    #region Helper Methods

    private string CreateCsvFile(int rowCount)
    {
        var filePath = Path.Combine(_testDirectory, $"import_{Guid.NewGuid():N}.csv");
        var builder = new StringBuilder();
        builder.AppendLine("Id,Name");
        for (var i = 1; i <= rowCount; i++)
            builder.AppendLine($"{i},User {i}");

        File.WriteAllText(filePath, builder.ToString());
        _importFilePaths.Add(filePath);
        return filePath;
    }

    private static ConnectedSystem CreateConnectedSystem(string filePath)
    {
        var objectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "User",
            Selected = true,
            Attributes = new List<ConnectedSystemObjectTypeAttribute>
            {
                new() { Id = 1, Name = "Id", Type = AttributeDataType.Number, Selected = true, IsExternalId = true },
                new() { Id = 2, Name = "Name", Type = AttributeDataType.Text, Selected = true }
            }
        };

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Test File System",
            ObjectTypes = new List<ConnectedSystemObjectType> { objectType },
            SettingValues = new List<ConnectedSystemSettingValue>
            {
                NewSetting("File Path", filePath),
                NewSetting("Mode", "Import Only"),
                NewSetting("Object Type", "User"),
                NewSetting("Delimiter", ","),
                NewSetting("Multi-Value Delimiter", "|")
            }
        };
    }

    private static List<ConnectedSystemSettingValue> CreateExportSettingValues(string exportPath)
    {
        return new List<ConnectedSystemSettingValue>
        {
            NewSetting("File Path", exportPath),
            NewSetting("Mode", "Export Only"),
            NewSetting("Delimiter", ","),
            NewSetting("Multi-Value Delimiter", "|")
        };
    }

    private static ConnectedSystemSettingValue NewSetting(string name, string? value)
    {
        return new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = name },
            StringValue = value
        };
    }

    private static List<PendingExport> CreateCreatePendingExports(params string[] externalIds)
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "User" };
        var externalIdAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 100,
            Name = "employeeId",
            Type = AttributeDataType.Text,
            IsExternalId = true,
            ConnectedSystemObjectType = objectType
        };
        var displayNameAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "displayName",
            Type = AttributeDataType.Text,
            ConnectedSystemObjectType = objectType
        };

        return externalIds.Select(externalId => new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Create,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Attribute = externalIdAttribute,
                    AttributeId = externalIdAttribute.Id,
                    StringValue = externalId
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    Attribute = displayNameAttribute,
                    AttributeId = displayNameAttribute.Id,
                    StringValue = $"User {externalId}"
                }
            }
        }).ToList();
    }

    #endregion
}
