// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.File;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A multi-valued attribute is held in one column, its values separated by the Multi-Value Delimiter (#2186). A
/// repeated column header is refused on every path rather than half-read, and export writes the whole set, not the
/// last value it was handed.
/// </summary>
[TestFixture]
public class FileConnectorMultiValuedTests
{
    private const string ExternalId = "emp001";

    private readonly List<string> _files = [];
    private FileConnector _connector = null!;
    private ILogger _logger = null!;
    private string _filePath = null!;

    [SetUp]
    public void SetUp()
    {
        _connector = new FileConnector();
        _logger = new LoggerConfiguration().CreateLogger();
        _filePath = NewFilePath();
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();
        foreach (var file in _files.Where(System.IO.File.Exists))
            System.IO.File.Delete(file);
        _files.Clear();
    }

    #region Repeated headers

    [Test]
    public async Task GetSchemaAsync_RepeatedHeader_IsRefusedNamingTheColumnAsync()
    {
        await System.IO.File.WriteAllTextAsync(_filePath, "employeeId,member,member\nemp001,a,b\n");

        var exception = Assert.ThrowsAsync<CsvParsingException>(async () => await _connector.GetSchemaAsync(SchemaSettingValues(_filePath), _logger));

        Assert.That(exception!.Message, Does.Contain("'member'").And.Contain("Multi-Value Delimiter"));
    }

    [Test]
    public async Task GetSchemaAsync_DelimitedColumn_IsDiscoveredSingleValuedAsync()
    {
        // Plurality is the administrator's to set: a delimiter in a cell could as easily be part of a single value.
        await System.IO.File.WriteAllTextAsync(_filePath, "employeeId,member\nemp001,a|b\n");

        var schema = await _connector.GetSchemaAsync(SchemaSettingValues(_filePath), _logger);

        var member = schema.ObjectTypes.Single().Attributes.Single(a => a.Name == "member");
        Assert.That(member.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
    }

    [Test]
    public async Task ImportAsync_RepeatedHeader_FailsNamingTheColumnAsync()
    {
        await System.IO.File.WriteAllTextAsync(_filePath, "employeeId,member,member\nemp001,a,b\n");

        Assert.That(async () => await ImportAsync(AttributeDataType.Text),
            Throws.InstanceOf<InvalidDataException>().With.Message.Contains("'member'").And.Message.Contains("Multi-Value Delimiter"));
    }

    [Test]
    public async Task ExportAsync_FileWithRepeatedHeader_FailsEveryExportAndLeavesTheFileUntouchedAsync()
    {
        const string original = "employeeId,displayName,member,member\nemp001,Jo,a,b\nemp002,Al,x,y\n";
        await System.IO.File.WriteAllTextAsync(_filePath, original);
        var export = UpdateExport(AttributeDataType.Text);
        export.AttributeValueChanges.Add(Change(TextAttribute("displayName", id: 3), PendingExportAttributeChangeType.Update, "Joanne"));

        var results = await _connector.ExportAsync(ExportSettingValues(_filePath), [export], CancellationToken.None, ConnectorProgress.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.False);
            Assert.That(results.Single().ErrorMessage, Does.Contain("'member'"));
            Assert.That(await System.IO.File.ReadAllTextAsync(_filePath), Is.EqualTo(original));
        }
    }

    private static IEnumerable<TestCaseData> DelimitedCellsOfEachType()
    {
        yield return new TestCaseData(AttributeDataType.LongNumber, "3000000000|4000000000",
            (Func<ConnectedSystemImportObjectAttribute, IEnumerable<object>>)(a => a.LongValues.Cast<object>()),
            new object[] { 3_000_000_000L, 4_000_000_000L }).SetArgDisplayNames("LongNumber");
        yield return new TestCaseData(AttributeDataType.Guid, "11111111-1111-1111-1111-111111111111|22222222-2222-2222-2222-222222222222",
            (Func<ConnectedSystemImportObjectAttribute, IEnumerable<object>>)(a => a.GuidValues.Cast<object>()),
            new object[] { Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222") }).SetArgDisplayNames("Guid");
        yield return new TestCaseData(AttributeDataType.Reference, "CN=Alice|CN=Bob",
            (Func<ConnectedSystemImportObjectAttribute, IEnumerable<object>>)(a => a.ReferenceValues.Cast<object>()),
            new object[] { "CN=Alice", "CN=Bob" }).SetArgDisplayNames("Reference");
    }

    [TestCaseSource(nameof(DelimitedCellsOfEachType))]
    public async Task ImportAsync_OneMultiValuedColumn_ReadsEveryDelimitedValueAsync(
        AttributeDataType type, string cell, Func<ConnectedSystemImportObjectAttribute, IEnumerable<object>> valuesOf, object[] expected)
    {
        await System.IO.File.WriteAllTextAsync(_filePath, $"employeeId,member\nemp001,{cell}\n");

        var result = await ImportAsync(type);

        var member = result.ImportObjects.Single().Attributes.Single(a => a.Name == "member");
        Assert.That(valuesOf(member), Is.EqualTo(expected));
    }

    #endregion

    #region Export: whole sets

    private static IEnumerable<TestCaseData> EveryTypeOfValue()
    {
        yield return new TestCaseData(AttributeDataType.Text, new object[] { "a", "b", "c" }, "a|b|c").SetArgDisplayNames("Text");
        yield return new TestCaseData(AttributeDataType.Number, new object[] { 1, 2, 3 }, "1|2|3").SetArgDisplayNames("Number");
        yield return new TestCaseData(AttributeDataType.LongNumber, new object[] { 3_000_000_000L, 4_000_000_000L }, "3000000000|4000000000").SetArgDisplayNames("LongNumber");
        yield return new TestCaseData(AttributeDataType.Decimal, new object[] { 1.5m, 2.25m }, "1.5|2.25").SetArgDisplayNames("Decimal");
        yield return new TestCaseData(AttributeDataType.DateTime,
            new object[] { new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 2, 10, 30, 0, DateTimeKind.Utc) },
            "2026-03-01T09:00:00.0000000Z|2026-03-02T10:30:00.0000000Z").SetArgDisplayNames("DateTime");
        yield return new TestCaseData(AttributeDataType.Guid,
            new object[] { Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222") },
            "11111111-1111-1111-1111-111111111111|22222222-2222-2222-2222-222222222222").SetArgDisplayNames("Guid");
        yield return new TestCaseData(AttributeDataType.Reference, new object[] { "CN=Alice", "CN=Bob" }, "CN=Alice|CN=Bob").SetArgDisplayNames("Reference");
        yield return new TestCaseData(AttributeDataType.Binary, new object[] { new byte[] { 1, 2 }, new byte[] { 3, 4 } }, "AQI=|AwQ=").SetArgDisplayNames("Binary");
    }

    [TestCaseSource(nameof(EveryTypeOfValue))]
    public async Task ExportAsync_CreateWithSeveralAdds_WritesEveryValueAsync(AttributeDataType type, object[] values, string expectedCell)
    {
        var member = Attribute("member", type, AttributePlurality.MultiValued);
        var export = CreateExport();
        foreach (var value in values)
            export.AttributeValueChanges.Add(Change(member, PendingExportAttributeChangeType.Add, value));

        var results = await _connector.ExportAsync(ExportSettingValues(_filePath), [export], CancellationToken.None, ConnectorProgress.None);

        Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
        Assert.That(await ReadCellAsync("member"), Is.EqualTo(expectedCell));
    }

    [Test]
    public async Task ExportAsync_UpdateAddingAValue_KeepsTheExistingValuesAsync()
    {
        await System.IO.File.WriteAllTextAsync(_filePath, "employeeId,member\nemp001,a|b\n");

        await ExportUpdateAsync((PendingExportAttributeChangeType.Add, "c"));

        Assert.That(await ReadCellAsync("member"), Is.EqualTo("a|b|c"));
    }

    [Test]
    public async Task ExportAsync_UpdateRemovingAValue_RemovesExactlyThatValueAsync()
    {
        await System.IO.File.WriteAllTextAsync(_filePath, "employeeId,member\nemp001,a|b|c\n");

        await ExportUpdateAsync((PendingExportAttributeChangeType.Remove, "b"));

        Assert.That(await ReadCellAsync("member"), Is.EqualTo("a|c"));
    }

    [Test]
    public async Task ExportAsync_UpdateRemovingAValueInAnotherCase_RemovesItFromACaseInsensitiveAttributeAsync()
    {
        await System.IO.File.WriteAllTextAsync(_filePath, "employeeId,member\nemp001,CN=Alice|CN=Bob\n");

        await ExportUpdateAsync((PendingExportAttributeChangeType.Remove, "cn=alice"));

        Assert.That(await ReadCellAsync("member"), Is.EqualTo("CN=Bob"));
    }

    [Test]
    public async Task ExportAsync_UpdateRemovingEveryValue_ClearsTheCellAsync()
    {
        await System.IO.File.WriteAllTextAsync(_filePath, "employeeId,member\nemp001,a|b\n");

        await ExportUpdateAsync((PendingExportAttributeChangeType.RemoveAll, null));

        Assert.That(await ReadCellAsync("member"), Is.Empty);
    }

    [Test]
    public async Task ExportAsync_UpdateAddingAValueAlreadyHeld_LeavesTheSetAsItWasAsync()
    {
        await System.IO.File.WriteAllTextAsync(_filePath, "employeeId,member\nemp001,a|b\n");

        await ExportUpdateAsync((PendingExportAttributeChangeType.Add, "a"));

        Assert.That(await ReadCellAsync("member"), Is.EqualTo("a|b"));
    }

    [Test]
    public async Task ExportAsync_UpdateWithRemoveAndAddTogether_AppliesBothAsync()
    {
        await System.IO.File.WriteAllTextAsync(_filePath, "employeeId,member\nemp001,a|b\n");

        await ExportUpdateAsync((PendingExportAttributeChangeType.Remove, "a"), (PendingExportAttributeChangeType.Add, "c"));

        Assert.That(await ReadCellAsync("member"), Is.EqualTo("b|c"));
    }

    [Test]
    public async Task ExportAsync_UpdateForARowNotInTheFile_StartsFromTheConnectedSystemObjectsValuesAsync()
    {
        await System.IO.File.WriteAllTextAsync(_filePath, "employeeId,member\nemp002,x\n");
        var export = UpdateExport(AttributeDataType.Text, csoValues: ["a", "b"]);
        export.AttributeValueChanges.Add(Change(MemberOf(export), PendingExportAttributeChangeType.Add, "c"));

        await _connector.ExportAsync(ExportSettingValues(_filePath), [export], CancellationToken.None, ConnectorProgress.None);

        Assert.That(await ReadCellAsync("member"), Is.EqualTo("a|b|c"));
    }

    [Test]
    public async Task ExportAsync_RowNotInTheFile_WritesEveryConnectedSystemObjectValueOfAnUnchangedAttributeAsync()
    {
        await System.IO.File.WriteAllTextAsync(_filePath, "displayName,employeeId,member\nAl,emp002,x\n");
        var export = UpdateExport(AttributeDataType.Text, csoValues: ["a", "b"]);
        export.AttributeValueChanges.Add(Change(TextAttribute("displayName", id: 3), PendingExportAttributeChangeType.Update, "Jo"));

        await _connector.ExportAsync(ExportSettingValues(_filePath), [export], CancellationToken.None, ConnectorProgress.None);

        Assert.That(await ReadCellAsync("member"), Is.EqualTo("a|b"));
    }

    [Test]
    public async Task ExportAsync_ValueHoldingTheDelimiter_FailsThatObjectAndLeavesItsRowUntouchedAsync()
    {
        await System.IO.File.WriteAllTextAsync(_filePath, "displayName,employeeId,member\nJo,emp001,a\n");
        var export = UpdateExport(AttributeDataType.Text);
        export.AttributeValueChanges.Add(Change(TextAttribute("displayName", id: 3), PendingExportAttributeChangeType.Update, "Joanne"));
        export.AttributeValueChanges.Add(Change(MemberOf(export), PendingExportAttributeChangeType.Add, "b|c"));

        var results = await _connector.ExportAsync(ExportSettingValues(_filePath), [export], CancellationToken.None, ConnectorProgress.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Single().Success, Is.False);
            Assert.That(results.Single().ErrorMessage, Does.Contain("member").And.Contain("Multi-Value Delimiter"));
            Assert.That(await ReadCellAsync("member"), Is.EqualTo("a"));
            Assert.That(await ReadCellAsync("displayName"), Is.EqualTo("Jo"));
        }
    }

    [Test]
    public async Task ExportAsync_SingleValuedValueHoldingTheDelimiter_IsWrittenAsItIsAsync()
    {
        // Only a multi-valued cell is split on the delimiter, so a single value may carry it.
        var export = CreateExport();
        export.AttributeValueChanges.Add(Change(TextAttribute("displayName", id: 3), PendingExportAttributeChangeType.Update, "Jo|Joanne"));

        var results = await _connector.ExportAsync(ExportSettingValues(_filePath), [export], CancellationToken.None, ConnectorProgress.None);

        Assert.That(results.Single().Success, Is.True);
        Assert.That(await ReadCellAsync("displayName"), Is.EqualTo("Jo|Joanne"));
    }

    #endregion

    #region Round trip

    [Test]
    public async Task ExportThenImport_MultiValuedSet_ReadsBackTheSetExportedAsync()
    {
        var export = CreateExport();
        foreach (var value in new[] { "CN=Alice", "CN=Bob", "CN=Carol" })
            export.AttributeValueChanges.Add(Change(MemberOf(export), PendingExportAttributeChangeType.Add, value));
        await _connector.ExportAsync(ExportSettingValues(_filePath), [export], CancellationToken.None, ConnectorProgress.None);

        var imported = await ImportAsync(AttributeDataType.Text);

        var member = imported.ImportObjects.Single().Attributes.Single(a => a.Name == "member");
        Assert.That(member.StringValues, Is.EquivalentTo(new[] { "CN=Alice", "CN=Bob", "CN=Carol" }));
    }

    #endregion

    #region Helpers

    private string NewFilePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jim-multi-valued-{Guid.NewGuid():N}.csv");
        _files.Add(path);
        return path;
    }

    private async Task ExportUpdateAsync(params (PendingExportAttributeChangeType ChangeType, string? Value)[] changes)
    {
        var export = UpdateExport(AttributeDataType.Text);
        foreach (var (changeType, value) in changes)
            export.AttributeValueChanges.Add(Change(MemberOf(export), changeType, value));

        var results = await _connector.ExportAsync(ExportSettingValues(_filePath), [export], CancellationToken.None, ConnectorProgress.None);
        Assert.That(results.Single().Success, Is.True, results.Single().ErrorMessage);
    }

    private async Task<string> ReadCellAsync(string column)
    {
        var lines = await System.IO.File.ReadAllLinesAsync(_filePath);
        var header = lines[0].Split(',');
        var row = lines.Skip(1).Select(line => line.Split(',')).Single(fields => fields[Array.IndexOf(header, "employeeId")] == ExternalId);
        return row[Array.IndexOf(header, column)];
    }

    private async Task<ConnectedSystemImportResult> ImportAsync(AttributeDataType memberType)
    {
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "HR",
            ObjectTypes =
            [
                new ConnectedSystemObjectType
                {
                    Id = 1,
                    Name = "User",
                    Selected = true,
                    Attributes =
                    [
                        new() { Id = 100, Name = "employeeId", Type = AttributeDataType.Text, Selected = true, IsExternalId = true },
                        new() { Id = 2, Name = "member", Type = memberType, Selected = true, AttributePlurality = AttributePlurality.MultiValued }
                    ]
                }
            ],
            SettingValues =
            [
                new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = "Object Type" }, StringValue = "User" },
                new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = "Delimiter" }, StringValue = "," },
                new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = "Multi-Value Delimiter" }, StringValue = "|" }
            ]
        };
        var runProfile = new ConnectedSystemRunProfile { FilePath = _filePath, RunType = ConnectedSystemRunType.FullImport };

        return await _connector.ImportAsync(connectedSystem, runProfile, _logger, CancellationToken.None, ConnectorProgress.None);
    }

    private static List<ConnectedSystemSettingValue> SchemaSettingValues(string filePath) =>
    [
        new() { Setting = new ConnectorDefinitionSetting { Name = "File Path" }, StringValue = filePath },
        new() { Setting = new ConnectorDefinitionSetting { Name = "Mode" }, StringValue = "Import Only" },
        new() { Setting = new ConnectorDefinitionSetting { Name = "Object Type" }, StringValue = "User" },
        new() { Setting = new ConnectorDefinitionSetting { Name = "Delimiter" }, StringValue = "," },
        new() { Setting = new ConnectorDefinitionSetting { Name = "Multi-Value Delimiter" }, StringValue = "|" }
    ];

    private static List<ConnectedSystemSettingValue> ExportSettingValues(string filePath) =>
    [
        new() { Setting = new ConnectorDefinitionSetting { Name = "File Path" }, StringValue = filePath },
        new() { Setting = new ConnectorDefinitionSetting { Name = "Delimiter" }, StringValue = "," },
        new() { Setting = new ConnectorDefinitionSetting { Name = "Multi-Value Delimiter" }, StringValue = "|" }
    ];

    private static ConnectedSystemObjectType UserType() => new() { Id = 1, Name = "User" };

    private static ConnectedSystemObjectTypeAttribute Attribute(string name, AttributeDataType type, AttributePlurality plurality, int id = 2) =>
        new() { Id = id, Name = name, Type = type, AttributePlurality = plurality };

    private static ConnectedSystemObjectTypeAttribute TextAttribute(string name, int id) =>
        Attribute(name, AttributeDataType.Text, AttributePlurality.SingleValued, id);

    private static ConnectedSystemObjectTypeAttribute ExternalIdAttribute() =>
        new() { Id = 100, Name = "employeeId", Type = AttributeDataType.Text, IsExternalId = true };

    /// <summary>
    /// The multi-valued <c>member</c> attribute a test export carries, so every change to it shares one attribute.
    /// </summary>
    private static ConnectedSystemObjectTypeAttribute MemberOf(PendingExport export) =>
        export.ConnectedSystemObject?.Type.Attributes.SingleOrDefault(a => a.Name == "member")
        ?? Attribute("member", AttributeDataType.Text, AttributePlurality.MultiValued);

    private static PendingExport CreateExport()
    {
        var externalId = ExternalIdAttribute();
        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Create,
            AttributeValueChanges = [Change(externalId, PendingExportAttributeChangeType.Update, ExternalId)]
        };
    }

    private static PendingExport UpdateExport(AttributeDataType memberType, IEnumerable<string>? csoValues = null)
    {
        var objectType = UserType();
        var externalId = ExternalIdAttribute();
        var member = Attribute("member", memberType, AttributePlurality.MultiValued);
        objectType.Attributes.Add(externalId);
        objectType.Attributes.Add(member);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = objectType,
            ExternalIdAttributeId = externalId.Id,
            AttributeValues = [new ConnectedSystemObjectAttributeValue { Attribute = externalId, AttributeId = externalId.Id, StringValue = ExternalId }]
        };
        foreach (var value in csoValues ?? [])
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = member, AttributeId = member.Id, StringValue = value });

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = cso,
            AttributeValueChanges = []
        };
    }

    private static PendingExportAttributeValueChange Change(ConnectedSystemObjectTypeAttribute attribute, PendingExportAttributeChangeType changeType, object? value)
    {
        var change = new PendingExportAttributeValueChange { Id = Guid.NewGuid(), Attribute = attribute, AttributeId = attribute.Id, ChangeType = changeType };
        switch (value)
        {
            case null:
                break;
            case string text:
                change.StringValue = text;
                break;
            case int number:
                change.IntValue = number;
                break;
            case long longNumber:
                change.LongValue = longNumber;
                break;
            case decimal decimalValue:
                change.DecimalValue = decimalValue;
                break;
            case DateTime dateTime:
                change.DateTimeValue = dateTime;
                break;
            case Guid guid:
                change.GuidValue = guid;
                break;
            case byte[] bytes:
                change.ByteValue = bytes;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value, "No value field for this type.");
        }

        return change;
    }

    #endregion
}
