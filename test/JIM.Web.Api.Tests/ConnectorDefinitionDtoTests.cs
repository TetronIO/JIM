// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST surface for a Connector Definition (#1447). The raw entity dragged its Files (including each
/// connector binary as base64) and a ConnectedSystems back-navigation onto the wire; the DTO carries the
/// metadata an API client actually binds to and nothing EF happened to have loaded.
/// </summary>
[TestFixture]
public class ConnectorDefinitionDtoTests
{
    private static ConnectorDefinition BuildDefinition()
    {
        var definition = new ConnectorDefinition
        {
            Id = 3,
            Name = "JIM SQL Connector",
            Description = "Synchronises with relational databases.",
            Url = "https://tetron.io",
            BuiltIn = true,
            Created = new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc),
            LastUpdated = new DateTime(2026, 2, 1, 9, 30, 0, DateTimeKind.Utc),
            CreatedByName = "System",
            LastUpdatedByName = "Jay",
            SupportsFullImport = true,
            SupportsDeltaImport = true,
            SupportsExport = true,
            SupportsPartitions = false,
            SupportsPartitionContainers = false,
            SupportsSecondaryExternalId = false,
            SupportsUserSelectedExternalId = true,
            SupportsUserSelectedAttributeTypes = true,
            SupportsAutoConfirmExport = true,
            SupportsParallelExport = true,
            SupportsPaging = true,
            SupportsFilePaths = false,
            SupportsPasswordSet = false,
            SupportsPasswordPolicyDiscovery = false,
            SchemaStandard = AttributeStandard.Scim,
            Settings =
            [
                new ConnectorDefinitionSetting
                {
                    Id = 11,
                    Name = "Connection String",
                    Description = "How to reach the database.",
                    Category = ConnectedSystemSettingCategory.Connectivity,
                    Type = ConnectedSystemSettingType.StringEncrypted,
                    Required = true
                }
            ]
        };
        definition.Files.Add(new ConnectorDefinitionFile
        {
            Id = 21,
            Filename = "JIM.Connectors.dll",
            Version = "1.0.0",
            FileSizeBytes = 4096,
            File = [1, 2, 3],
            ImplementsIConnector = true,
            ImplementsISchema = true
        });
        return definition;
    }

    [Test]
    public void FromEntity_MapsScalarsAndCapabilities()
    {
        var dto = ConnectorDefinitionDto.FromEntity(BuildDefinition());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Id, Is.EqualTo(3));
            Assert.That(dto.Name, Is.EqualTo("JIM SQL Connector"));
            Assert.That(dto.Description, Is.EqualTo("Synchronises with relational databases."));
            Assert.That(dto.Url, Is.EqualTo("https://tetron.io"));
            Assert.That(dto.BuiltIn, Is.True);
            Assert.That(dto.Created, Is.EqualTo(new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc)));
            Assert.That(dto.LastUpdated, Is.EqualTo(new DateTime(2026, 2, 1, 9, 30, 0, DateTimeKind.Utc)));
            Assert.That(dto.CreatedByName, Is.EqualTo("System"));
            Assert.That(dto.LastUpdatedByName, Is.EqualTo("Jay"));
            Assert.That(dto.SupportsFullImport, Is.True);
            Assert.That(dto.SupportsDeltaImport, Is.True);
            Assert.That(dto.SupportsExport, Is.True);
            Assert.That(dto.SupportsPartitions, Is.False);
            Assert.That(dto.SupportsPartitionContainers, Is.False);
            Assert.That(dto.SupportsSecondaryExternalId, Is.False);
            Assert.That(dto.SupportsUserSelectedExternalId, Is.True);
            Assert.That(dto.SupportsUserSelectedAttributeTypes, Is.True);
            Assert.That(dto.SupportsAutoConfirmExport, Is.True);
            Assert.That(dto.SupportsParallelExport, Is.True);
            Assert.That(dto.SupportsPaging, Is.True);
            Assert.That(dto.SupportsFilePaths, Is.False);
            Assert.That(dto.SupportsPasswordSet, Is.False);
            Assert.That(dto.SupportsPasswordPolicyDiscovery, Is.False);
            Assert.That(dto.SchemaStandard, Is.EqualTo("Scim"));
        }
    }

    [Test]
    public void FromEntity_MapsSettingsWithEnumNames()
    {
        var dto = ConnectorDefinitionDto.FromEntity(BuildDefinition());

        Assert.That(dto.Settings, Has.Count.EqualTo(1));
        var setting = dto.Settings.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(setting.Id, Is.EqualTo(11));
            Assert.That(setting.Name, Is.EqualTo("Connection String"));
            Assert.That(setting.Description, Is.EqualTo("How to reach the database."));
            Assert.That(setting.Category, Is.EqualTo("Connectivity"));
            Assert.That(setting.Type, Is.EqualTo("StringEncrypted"));
            Assert.That(setting.Required, Is.True);
        }
    }

    [Test]
    public void FromEntity_MapsFileMetadataWithoutThePayload()
    {
        var dto = ConnectorDefinitionDto.FromEntity(BuildDefinition());

        Assert.That(dto.Files, Has.Count.EqualTo(1));
        var file = dto.Files.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(file.Id, Is.EqualTo(21));
            Assert.That(file.Filename, Is.EqualTo("JIM.Connectors.dll"));
            Assert.That(file.Version, Is.EqualTo("1.0.0"));
            Assert.That(file.FileSizeBytes, Is.EqualTo(4096));
            Assert.That(file.ImplementsIConnector, Is.True);
            Assert.That(file.ImplementsISchema, Is.True);
        }

        // the connector binary must never be serialised onto the wire; the DTO has no property that could carry it.
        Assert.That(typeof(ConnectorDefinitionFileDto).GetProperties().Where(p => p.PropertyType == typeof(byte[])), Is.Empty);
    }
}
