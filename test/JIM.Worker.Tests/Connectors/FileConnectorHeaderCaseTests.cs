// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.File;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A file's column headers name the schema's attributes ignoring case (#2051): <c>employeeid</c> is the column for
/// <c>EmployeeId</c>. A column the schema expects that the file does not have fails the import with a message naming
/// it, rather than being read as an attribute with no value, which would clear it on every object.
/// </summary>
[TestFixture]
public class FileConnectorHeaderCaseTests
{
    private readonly List<string> _files = [];
    private ILogger _logger = null!;

    [SetUp]
    public void SetUp() => _logger = new LoggerConfiguration().CreateLogger();

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();
        foreach (var file in _files.Where(System.IO.File.Exists))
            System.IO.File.Delete(file);
        _files.Clear();
    }

    [Test]
    public async Task ImportAsync_HeadersInAnotherCase_ReadTheirAttributesAsync()
    {
        var result = await ImportAsync("id,NAME,employeeid\n1,Joe Bloggs,E001\n");

        var row = result.ImportObjects.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.ErrorType, Is.Null);
            Assert.That(row.Attributes.Single(a => a.Name == "Name").StringValues, Is.EqualTo(new[] { "Joe Bloggs" }));
            Assert.That(row.Attributes.Single(a => a.Name == "EmployeeId").StringValues, Is.EqualTo(new[] { "E001" }));
        }
    }

    [Test]
    public void ImportAsync_SelectedAttributesColumnMissing_FailsNamingTheColumn()
    {
        Assert.That(async () => await ImportAsync("Id,Name\n1,Joe Bloggs\n"),
            Throws.InstanceOf<InvalidDataException>().With.Message.Contains("EmployeeId"));
    }

    [Test]
    public async Task ImportAsync_UnselectedAttributesColumnMissing_ImportsAsync()
    {
        var result = await ImportAsync("Id,Name,EmployeeId\n1,Joe Bloggs,E001\n", includeUnselectedNotes: true);

        Assert.That(result.ImportObjects.Single().ErrorType, Is.Null);
    }

    [Test]
    public void ImportAsync_TwoColumnsDifferingOnlyInCase_FailsNamingThem()
    {
        Assert.That(async () => await ImportAsync("Id,Name,name,EmployeeId\n1,Joe,joe,E001\n"),
            Throws.InstanceOf<InvalidDataException>().With.Message.Contains("Name").And.Message.Contains("name"));
    }

    private async Task<ConnectedSystemImportResult> ImportAsync(string csv, bool includeUnselectedNotes = false)
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"jim-header-case-{Guid.NewGuid():N}.csv");
        await System.IO.File.WriteAllTextAsync(filePath, csv);
        _files.Add(filePath);

        var attributes = new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Id = 1, Name = "Id", Type = AttributeDataType.Number, Selected = true, IsExternalId = true },
            new() { Id = 2, Name = "Name", Type = AttributeDataType.Text, Selected = true },
            new() { Id = 3, Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true }
        };
        if (includeUnselectedNotes)
            attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 4, Name = "Notes", Type = AttributeDataType.Text, Selected = false });

        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "HR",
            ObjectTypes = [new ConnectedSystemObjectType { Id = 1, Name = "User", Selected = true, Attributes = attributes }],
            SettingValues =
            [
                new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = "Object Type" }, StringValue = "User" },
                new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = "Delimiter" }, StringValue = "," }
            ]
        };
        var runProfile = new ConnectedSystemRunProfile { FilePath = filePath, RunType = ConnectedSystemRunType.FullImport };

        return await new FileConnector().ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None, ConnectorProgress.None);
    }
}
