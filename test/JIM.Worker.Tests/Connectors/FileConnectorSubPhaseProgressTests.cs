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

        _testExportPath = Path.Join(_testDirectory, $"export_{Guid.NewGuid():N}.csv");
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
    public async Task ExportAsync_WithProgressReporter_EntersLoadMergeAndWriteInOrderAsync()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateCreatePendingExports("emp001", "emp002");
        var progress = new RecordingConnectorProgress();

        // Act
        await _connector.ExportAsync(settingValues, pendingExports, CancellationToken.None, progress);

        // Assert
        Assert.That(progress.PhaseKeys, Is.EqualTo(new[]
        {
            FileConnectorPhases.LoadExistingFile,
            FileConnectorPhases.Merge,
            FileConnectorPhases.Write
        }), "The steps an administrator sees must be the declared ones, entered in the order the work happens");
    }

    [Test]
    public async Task ExportAsync_WithProgressReporter_NarratesTheScaleOfTheWorkAsync()
    {
        // Arrange - a step's name says what is happening; the message says how much of it there is.
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateCreatePendingExports("emp001", "emp002");
        var progress = new RecordingConnectorProgress();

        // Act
        await _connector.ExportAsync(settingValues, pendingExports, CancellationToken.None, progress);

        // Assert
        Assert.That(progress.Messages, Is.EqualTo(new[]
        {
            "Merging 2 changes into file...",
            "Writing 2 rows to output file..."
        }));
    }

    [Test]
    public async Task ExportAsync_EveryPhaseEntered_WasDeclaredUpFrontAsync()
    {
        // A phase entered but never declared still shows, appended to the end of the stepper, but it
        // cannot be shown as still to come. Every phase this Connector enters must be declared.
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateCreatePendingExports("emp001");
        var progress = new RecordingConnectorProgress();
        var runProfile = new ConnectedSystemRunProfile { RunType = ConnectedSystemRunType.Export };
        var declared = _connector.GetPhases(new ConnectedSystem { Name = "Target" }, runProfile).Select(p => p.Key).ToList();

        await _connector.ExportAsync(settingValues, pendingExports, CancellationToken.None, progress);

        Assert.That(declared, Is.SupersetOf(progress.PhaseKeys));
    }

    [Test]
    public async Task ExportAsync_WithNoPendingExports_ReportsNoProgressAsync()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var progress = new RecordingConnectorProgress();

        // Act
        await _connector.ExportAsync(settingValues, new List<PendingExport>(), CancellationToken.None, progress);

        // Assert
        Assert.That(progress.Messages, Is.Empty, "Nothing to export means no file work to narrate");
    }

    [Test]
    public async Task ExportAsync_WithTheNoOpProgressReporter_StillExportsAsync()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateCreatePendingExports("emp001");

        // Act
        var results = await _connector.ExportAsync(settingValues, pendingExports, CancellationToken.None, ConnectorProgress.None);

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True);
        Assert.That(File.Exists(_testExportPath), Is.True);
    }

    [Test]
    public async Task ExportAsync_WhenProgressCallbackThrows_StillExportsAsync()
    {
        // Arrange - guarding the reporter is JIM's job, not the Connector's, so this uses the real
        // reporter with a delegate that throws: the export must still complete.
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = CreateCreatePendingExports("emp001");
        using var progress = new ConnectorProgress(
            _ => throw new InvalidOperationException("activity update failed"));

        // Act
        var results = await _connector.ExportAsync(settingValues, pendingExports, CancellationToken.None, progress);

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
        var progress = new RecordingConnectorProgress();

        // Act
        var result = await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None, progress);

        // Assert
        Assert.That(result.ImportObjects, Has.Count.EqualTo(25_000));
        Assert.That(progress.PhaseKeys, Is.EqualTo(new[] { FileConnectorPhases.Read }),
            "Reading and parsing a file is one pass, so it is one step");
        Assert.That(progress.ObjectsRead, Is.EqualTo(new[] { 10_000, 20_000, 25_000 }),
            "Row progress must advance during the read, and finish on the true total");
        Assert.That(progress.Messages, Is.Empty,
            "The counters carry the figures now, so narrating them as prose as well would say the same thing twice");
    }

    [Test]
    public async Task ImportAsync_WithProgressCallback_StatesHowManyRowsTheFileHoldsBeforeParsingThemAsync()
    {
        // Arrange - a file import returns everything in one call, so without a stated total the
        // Activity can only show a bar with no end to it for the whole read.
        var filePath = CreateCsvFile(rowCount: 25_000);
        var connectedSystem = CreateConnectedSystem(filePath);
        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };
        var progress = new RecordingConnectorProgress();

        // Act
        await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None, progress);

        // Assert
        Assert.That(progress.ExpectedObjectCounts, Is.EqualTo(new[] { 25_000 }),
            "The rows are counted once, before the read, and the figure does not need correcting afterwards");
    }

    [Test]
    public async Task ImportAsync_WithARowSpanningLines_CountsItOnceAsync()
    {
        // Arrange - a quoted field may hold line breaks, so counting lines rather than records
        // would overstate the file and leave the bar stuck short of complete.
        var filePath = CreateCsvFileWithAMultiLineField();
        var connectedSystem = CreateConnectedSystem(filePath);
        var runProfile = new ConnectedSystemRunProfile
        {
            FilePath = filePath,
            RunType = ConnectedSystemRunType.FullImport
        };
        var progress = new RecordingConnectorProgress();

        // Act
        var result = await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None, progress);

        // Assert
        Assert.That(result.ImportObjects, Has.Count.EqualTo(2));
        Assert.That(progress.ExpectedObjectCounts, Is.EqualTo(new[] { 2 }),
            "The stated total has to match what the Connector actually hands over, or the bar never reaches the end");
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
        var progress = new RecordingConnectorProgress();

        // Act
        await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None, progress);

        // Assert
        Assert.That(progress.ObjectsRead, Is.EqualTo(new[] { 3 }));
    }

    [Test]
    public async Task ImportAsync_WithTheNoOpProgressReporter_StillImportsAsync()
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
        var result = await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None, ConnectorProgress.None);

        // Assert
        Assert.That(result.ImportObjects, Has.Count.EqualTo(5));
    }

    #endregion

    #region Helper Methods

    private string CreateCsvFile(int rowCount)
    {
        var filePath = Path.Join(_testDirectory, $"import_{Guid.NewGuid():N}.csv");
        var builder = new StringBuilder();
        builder.AppendLine("Id,Name");
        for (var i = 1; i <= rowCount; i++)
            builder.AppendLine($"{i},User {i}");

        File.WriteAllText(filePath, builder.ToString());
        _importFilePaths.Add(filePath);
        return filePath;
    }

    /// <summary>
    /// Two records, the first of which carries a line break inside a quoted field, so the file has
    /// three lines but holds two objects.
    /// </summary>
    private string CreateCsvFileWithAMultiLineField()
    {
        var filePath = Path.Join(_testDirectory, $"import_{Guid.NewGuid():N}.csv");
        File.WriteAllText(filePath, "Id,Name\r\n1,\"User\r\nOne\"\r\n2,User Two\r\n");
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
