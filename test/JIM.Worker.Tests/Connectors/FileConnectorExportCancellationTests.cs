using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Cancellation token tests for the File connector's export operations.
/// Note: The File connector currently does NOT check the cancellation token during export —
/// it delegates to a synchronous FileConnectorExport.Execute() method. This is acceptable
/// for small file-based exports but means cancellation is only effective at the batch boundary
/// in ExportExecutionServer, not within the connector itself.
/// </summary>
[TestFixture]
public class FileConnectorExportCancellationTests
{
    private FileConnector _connector = null!;
    private string _testExportPath = null!;
    private ILogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _connector = new FileConnector();
        var testOutputDir = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestOutput");
        if (!Directory.Exists(testOutputDir))
            Directory.CreateDirectory(testOutputDir);
        _testExportPath = Path.Combine(testOutputDir, $"export_{Guid.NewGuid():N}.csv");
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();
        if (File.Exists(_testExportPath))
            File.Delete(_testExportPath);
    }

    /// <summary>
    /// Verifies that the File connector completes export even when the cancellation token
    /// is already cancelled. This documents the current behaviour: the File connector does
    /// NOT check the cancellation token (unlike the LDAP connector which throws
    /// OperationCanceledException). Cancellation for file exports is handled at the batch
    /// boundary in ExportExecutionServer instead.
    /// </summary>
    [Test]
    public async Task ExportAsync_CancellationRequested_CompletesWithoutThrowingAsync()
    {
        // Arrange
        var settingValues = CreateExportSettingValues(_testExportPath);
        var pendingExports = new List<PendingExport>
        {
            CreatePendingExport("User1", PendingExportChangeType.Create)
        };

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act — does NOT throw despite cancellation (file connector ignores token)
        var results = await _connector.ExportAsync(settingValues, pendingExports, cts.Token);

        // Assert: Export completes successfully
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Success, Is.True);
        Assert.That(File.Exists(_testExportPath), Is.True);
    }

    #region Helpers

    private static List<ConnectedSystemSettingValue> CreateExportSettingValues(string filePath)
    {
        return new List<ConnectedSystemSettingValue>
        {
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "File Path" },
                StringValue = filePath
            },
            new()
            {
                Setting = new ConnectorDefinitionSetting { Name = "Mode" },
                StringValue = "Export Only"
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
    }

    private static PendingExport CreatePendingExport(string displayName, PendingExportChangeType changeType)
    {
        var objectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "User",
            Attributes = new List<ConnectedSystemObjectTypeAttribute>
            {
                new() { Id = 1, Name = "DisplayName", Type = AttributeDataType.Text, IsExternalId = true, Selected = true }
            }
        };

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            TypeId = objectType.Id,
            Type = objectType
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = 1,
            Attribute = objectType.Attributes[0],
            StringValue = displayName
        });

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = changeType,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    AttributeId = 1,
                    Attribute = objectType.Attributes[0],
                    StringValue = displayName
                }
            }
        };
    }

    #endregion
}
