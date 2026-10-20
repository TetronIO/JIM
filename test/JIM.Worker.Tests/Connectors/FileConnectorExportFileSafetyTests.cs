// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.File;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// An export rewrites the whole file, so every row it was not asked to change must come back exactly as it was. When
/// JIM cannot be sure of that (it cannot read the existing file, the file has no External ID column, or two rows claim
/// the same External ID), the export fails and the file is left untouched, rather than being rewritten with only the
/// rows this run touched.
/// </summary>
[TestFixture]
public class FileConnectorExportFileSafetyTests
{
    private FileConnector _connector = null!;
    private string _filePath = null!;

    [SetUp]
    public void SetUp()
    {
        _connector = new FileConnector();
        _filePath = Path.Combine(Path.GetTempPath(), $"jim-export-safety-{Guid.NewGuid():N}.csv");
    }

    [TearDown]
    public void TearDown()
    {
        if (System.IO.File.Exists(_filePath))
            System.IO.File.Delete(_filePath);
    }

    [Test]
    public async Task ExportAsync_ExistingFileJimCannotParse_FailsEveryExportAndLeavesTheFileUntouchedAsync()
    {
        const string original = "displayName,employeeId\nJo,emp001\n\"Al\"x,emp002\nSam,emp003\n";
        await System.IO.File.WriteAllTextAsync(_filePath, original);

        var results = await ExportAsync(UpdateDisplayName("emp001", "Joanne"));

        await AssertRefusedAndUntouchedAsync(results, original, "could not be read");
    }

    [Test]
    public async Task ExportAsync_ExistingFileWithRowsButNoExternalIdColumn_FailsEveryExportAndLeavesTheFileUntouchedAsync()
    {
        const string original = "displayName,mail\nJo,jo@panoply.org\nAl,al@panoply.org\n";
        await System.IO.File.WriteAllTextAsync(_filePath, original);

        var results = await ExportAsync(UpdateDisplayName("emp001", "Joanne"));

        await AssertRefusedAndUntouchedAsync(results, original, "'employeeId'");
    }

    [Test]
    public async Task ExportAsync_ExistingHeaderOnlyFileWithoutTheExternalIdColumn_IsWrittenAsync()
    {
        // Nothing to lose: a header with no rows, as an administrator might prepare for an Export Only system.
        await System.IO.File.WriteAllTextAsync(_filePath, "displayName\n");

        var results = await ExportAsync(UpdateDisplayName("emp001", "Joanne"));

        Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
        Assert.That(await System.IO.File.ReadAllTextAsync(_filePath), Does.Contain("emp001"));
    }

    [Test]
    public async Task ExportAsync_ExistingFileWithTwoRowsForOneExternalId_FailsEveryExportAndLeavesTheFileUntouchedAsync()
    {
        const string original = "displayName,employeeId\nJo,emp001\nJoanne,emp001\nAl,emp002\n";
        await System.IO.File.WriteAllTextAsync(_filePath, original);

        var results = await ExportAsync(UpdateDisplayName("emp002", "Alan"));

        await AssertRefusedAndUntouchedAsync(results, original, "'emp001'");
    }

    [Test]
    public async Task ExportAsync_ExistingRowWithNoExternalId_IsKeptAsItWasAsync()
    {
        await System.IO.File.WriteAllTextAsync(_filePath, "displayName,employeeId\nJo,emp001\nUnassigned,\n");

        var results = await ExportAsync(UpdateDisplayName("emp001", "Joanne"));

        var lines = await System.IO.File.ReadAllLinesAsync(_filePath);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
            Assert.That(lines, Does.Contain("Joanne,emp001"));
            Assert.That(lines, Does.Contain("Unassigned,"));
        }
    }

    [Test]
    public async Task ExportAsync_SuccessfulRewrite_LeavesNoTemporaryFileBehindAsync()
    {
        await System.IO.File.WriteAllTextAsync(_filePath, "displayName,employeeId\nJo,emp001\n");

        await ExportAsync(UpdateDisplayName("emp001", "Joanne"));

        var directory = Path.GetDirectoryName(_filePath)!;
        var leftovers = Directory.GetFiles(directory, Path.GetFileName(_filePath) + "*").Where(f => f != _filePath);
        Assert.That(leftovers, Is.Empty);
    }

    private async Task AssertRefusedAndUntouchedAsync(List<ConnectedSystemExportResult> results, string original, string messageFragment)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Is.Not.Empty);
            Assert.That(results.All(r => !r.Success), Is.True, "Every Pending Export must fail, so none is confirmed against a file JIM did not write.");
            Assert.That(results.First().ErrorMessage, Does.Contain(messageFragment));
            Assert.That(await System.IO.File.ReadAllTextAsync(_filePath), Is.EqualTo(original));
        }
    }

    private async Task<List<ConnectedSystemExportResult>> ExportAsync(PendingExport export) =>
        await _connector.ExportAsync(
        [
            new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = "File Path" }, StringValue = _filePath },
            new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = "Delimiter" }, StringValue = "," },
            new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = "Multi-Value Delimiter" }, StringValue = "|" }
        ], [export], CancellationToken.None, ConnectorProgress.None);

    private static PendingExport UpdateDisplayName(string externalId, string displayName)
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "User" };
        var externalIdAttribute = new ConnectedSystemObjectTypeAttribute { Id = 100, Name = "employeeId", Type = AttributeDataType.Text, IsExternalId = true };
        var displayNameAttribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "displayName", Type = AttributeDataType.Text };
        objectType.Attributes.Add(externalIdAttribute);
        objectType.Attributes.Add(displayNameAttribute);

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = objectType,
                ExternalIdAttributeId = externalIdAttribute.Id,
                AttributeValues = [new ConnectedSystemObjectAttributeValue { Attribute = externalIdAttribute, AttributeId = externalIdAttribute.Id, StringValue = externalId }]
            },
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(),
                    Attribute = displayNameAttribute,
                    AttributeId = displayNameAttribute.Id,
                    ChangeType = PendingExportAttributeChangeType.Update,
                    StringValue = displayName
                }
            ]
        };
    }
}
