using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class Initial : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConnectorDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Url = table.Column<string>(type: "text", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsFullImport = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsDeltaImport = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsExport = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsPartitions = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsPartitionContainers = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsSecondaryExternalId = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsUserSelectedExternalId = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsUserSelectedAttributeTypes = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConnectorPartitions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorPartitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataGenerationTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataGenerationTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExampleDataSets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    Culture = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExampleDataSets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FunctionLibrary",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Filename = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FunctionLibrary", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MetaverseAttributes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    AttributePlurality = table.Column<int>(type: "integer", nullable: false),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseAttributes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MetaverseObjectTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseObjectTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConnectorDefinitionId = table.Column<int>(type: "integer", nullable: false),
                    SettingValuesValid = table.Column<bool>(type: "boolean", nullable: false),
                    PersistedConnectorData = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystems_ConnectorDefinitions_ConnectorDefinitionId",
                        column: x => x.ConnectorDefinitionId,
                        principalTable: "ConnectorDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectorDefinitionFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectorDefinitionId = table.Column<int>(type: "integer", nullable: false),
                    Filename = table.Column<string>(type: "text", nullable: false),
                    ImplementsIConnector = table.Column<bool>(type: "boolean", nullable: false),
                    ImplementsICapabilities = table.Column<bool>(type: "boolean", nullable: false),
                    ImplementsISchema = table.Column<bool>(type: "boolean", nullable: false),
                    ImplementsISettings = table.Column<bool>(type: "boolean", nullable: false),
                    ImplementsIContainers = table.Column<bool>(type: "boolean", nullable: false),
                    ImplementsIExportUsingCalls = table.Column<bool>(type: "boolean", nullable: false),
                    ImplementsIExportUsingFiles = table.Column<bool>(type: "boolean", nullable: false),
                    ImplementsIImportUsingCalls = table.Column<bool>(type: "boolean", nullable: false),
                    ImplementsIImportUsingFiles = table.Column<bool>(type: "boolean", nullable: false),
                    FileSizeBytes = table.Column<int>(type: "integer", nullable: false),
                    File = table.Column<byte[]>(type: "bytea", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorDefinitionFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectorDefinitionFiles_ConnectorDefinitions_ConnectorDefi~",
                        column: x => x.ConnectorDefinitionId,
                        principalTable: "ConnectorDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectorDefinitionSetting",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectorDefinitionId = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    DefaultCheckboxValue = table.Column<bool>(type: "boolean", nullable: true),
                    DefaultStringValue = table.Column<string>(type: "text", nullable: true),
                    DefaultIntValue = table.Column<int>(type: "integer", nullable: true),
                    DropDownValues = table.Column<List<string>>(type: "text[]", nullable: true),
                    Required = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorDefinitionSetting", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectorDefinitionSetting_ConnectorDefinitions_ConnectorDe~",
                        column: x => x.ConnectorDefinitionId,
                        principalTable: "ConnectorDefinitions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ConnectorContainers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false),
                    ConnectorPartitionId = table.Column<string>(type: "text", nullable: true),
                    ConnectorContainerId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorContainers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectorContainers_ConnectorContainers_ConnectorContainerId",
                        column: x => x.ConnectorContainerId,
                        principalTable: "ConnectorContainers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConnectorContainers_ConnectorPartitions_ConnectorPartitionId",
                        column: x => x.ConnectorPartitionId,
                        principalTable: "ConnectorPartitions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ExampleDataSetValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StringValue = table.Column<string>(type: "text", nullable: false),
                    ExampleDataSetId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExampleDataSetValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExampleDataSetValues_ExampleDataSets_ExampleDataSetId",
                        column: x => x.ExampleDataSetId,
                        principalTable: "ExampleDataSets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Function",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FunctionLibraryId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    OutputType = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Function", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Function_FunctionLibrary_FunctionLibraryId",
                        column: x => x.FunctionLibraryId,
                        principalTable: "FunctionLibrary",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DataGenerationTemplateAttributeDependencies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: false),
                    ComparisonType = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataGenerationTemplateAttributeDependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributeDependencies_MetaverseAttrib~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SSOAuthority = table.Column<string>(type: "text", nullable: true),
                    SSOClientId = table.Column<string>(type: "text", nullable: true),
                    SSOSecret = table.Column<string>(type: "text", nullable: true),
                    SSOUniqueIdentifierClaimType = table.Column<string>(type: "text", nullable: true),
                    SSOUniqueIdentifierMetaverseAttributeId = table.Column<int>(type: "integer", nullable: true),
                    SSOEnableLogOut = table.Column<bool>(type: "boolean", nullable: false),
                    IsServiceInMaintenanceMode = table.Column<bool>(type: "boolean", nullable: false),
                    HistoryRetentionPeriod = table.Column<TimeSpan>(type: "interval", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceSettings_MetaverseAttributes_SSOUniqueIdentifierMeta~",
                        column: x => x.SSOUniqueIdentifierMetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "DataGenerationObjectTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MetaverseObjectTypeId = table.Column<int>(type: "integer", nullable: false),
                    ObjectsToCreate = table.Column<int>(type: "integer", nullable: false),
                    DataGenerationTemplateId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataGenerationObjectTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataGenerationObjectTypes_DataGenerationTemplates_DataGener~",
                        column: x => x.DataGenerationTemplateId,
                        principalTable: "DataGenerationTemplates",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DataGenerationObjectTypes_MetaverseObjectTypes_MetaverseObj~",
                        column: x => x.MetaverseObjectTypeId,
                        principalTable: "MetaverseObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetaverseAttributeMetaverseObjectType",
                columns: table => new
                {
                    AttributesId = table.Column<int>(type: "integer", nullable: false),
                    MetaverseObjectTypesId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseAttributeMetaverseObjectType", x => new { x.AttributesId, x.MetaverseObjectTypesId });
                    table.ForeignKey(
                        name: "FK_MetaverseAttributeMetaverseObjectType_MetaverseAttributes_A~",
                        column: x => x.AttributesId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MetaverseAttributeMetaverseObjectType_MetaverseObjectTypes_~",
                        column: x => x.MetaverseObjectTypesId,
                        principalTable: "MetaverseObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetaverseObjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TypeId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseObjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetaverseObjects_MetaverseObjectTypes_TypeId",
                        column: x => x.TypeId,
                        principalTable: "MetaverseObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PredefinedSearches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MetaverseObjectTypeId = table.Column<int>(type: "integer", nullable: false),
                    IsDefaultForMetaverseObjectType = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Uri = table.Column<string>(type: "text", nullable: false),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredefinedSearches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PredefinedSearches_MetaverseObjectTypes_MetaverseObjectType~",
                        column: x => x.MetaverseObjectTypeId,
                        principalTable: "MetaverseObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemObjectTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    Selected = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemObjectTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjectTypes_ConnectedSystems_ConnectedSystem~",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemPartitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Selected = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemPartitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemPartitions_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemSettingValue",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    SettingId = table.Column<int>(type: "integer", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    StringEncryptedValue = table.Column<string>(type: "text", nullable: true),
                    IntValue = table.Column<int>(type: "integer", nullable: true),
                    CheckboxValue = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemSettingValue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemSettingValue_ConnectedSystems_ConnectedSyste~",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemSettingValue_ConnectorDefinitionSetting_Sett~",
                        column: x => x.SettingId,
                        principalTable: "ConnectorDefinitionSetting",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FunctionParameter",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FunctionId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FunctionParameter", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FunctionParameter_Function_FunctionId",
                        column: x => x.FunctionId,
                        principalTable: "Function",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetaverseObjectAttributeValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttributeId = table.Column<int>(type: "integer", nullable: false),
                    MetaverseObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IntValue = table.Column<int>(type: "integer", nullable: true),
                    ByteValue = table.Column<byte[]>(type: "bytea", nullable: true),
                    ReferenceValueId = table.Column<Guid>(type: "uuid", nullable: true),
                    GuidValue = table.Column<Guid>(type: "uuid", nullable: true),
                    BoolValue = table.Column<bool>(type: "boolean", nullable: true),
                    ContributedBySystemId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseObjectAttributeValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetaverseObjectAttributeValues_ConnectedSystems_Contributed~",
                        column: x => x.ContributedBySystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MetaverseObjectAttributeValues_MetaverseAttributes_Attribut~",
                        column: x => x.AttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MetaverseObjectAttributeValues_MetaverseObjects_MetaverseOb~",
                        column: x => x.MetaverseObjectId,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MetaverseObjectAttributeValues_MetaverseObjects_ReferenceVa~",
                        column: x => x.ReferenceValueId,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "MetaverseObjectChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MetaverseObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangeTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangeInitiatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangeInitiatorType = table.Column<int>(type: "integer", nullable: false),
                    ChangeType = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseObjectChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetaverseObjectChanges_MetaverseObjects_ChangeInitiatorId",
                        column: x => x.ChangeInitiatorId,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MetaverseObjectChanges_MetaverseObjects_MetaverseObjectId",
                        column: x => x.MetaverseObjectId,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "MetaverseObjectRole",
                columns: table => new
                {
                    RolesId = table.Column<int>(type: "integer", nullable: false),
                    StaticMembersId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseObjectRole", x => new { x.RolesId, x.StaticMembersId });
                    table.ForeignKey(
                        name: "FK_MetaverseObjectRole_MetaverseObjects_StaticMembersId",
                        column: x => x.StaticMembersId,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MetaverseObjectRole_Roles_RolesId",
                        column: x => x.RolesId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PredefinedSearchAttribute",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PredefinedSearchId = table.Column<int>(type: "integer", nullable: false),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredefinedSearchAttribute", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PredefinedSearchAttribute_MetaverseAttributes_MetaverseAttr~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PredefinedSearchAttribute_PredefinedSearches_PredefinedSear~",
                        column: x => x.PredefinedSearchId,
                        principalTable: "PredefinedSearches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PredefinedSearchCriteriaGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    ParentGroupId = table.Column<int>(type: "integer", nullable: true),
                    PredefinedSearchId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredefinedSearchCriteriaGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PredefinedSearchCriteriaGroups_PredefinedSearchCriteriaGrou~",
                        column: x => x.ParentGroupId,
                        principalTable: "PredefinedSearchCriteriaGroups",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PredefinedSearchCriteriaGroups_PredefinedSearches_Predefine~",
                        column: x => x.PredefinedSearchId,
                        principalTable: "PredefinedSearches",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemAttributes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ClassName = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    AttributePlurality = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemObjectTypeId = table.Column<int>(type: "integer", nullable: false),
                    Selected = table.Column<bool>(type: "boolean", nullable: false),
                    IsExternalId = table.Column<bool>(type: "boolean", nullable: false),
                    IsSecondaryExternalId = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemAttributes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemAttributes_ConnectedSystemObjectTypes_Connec~",
                        column: x => x.ConnectedSystemObjectTypeId,
                        principalTable: "ConnectedSystemObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemObjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TypeId = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ExternalIdAttributeId = table.Column<int>(type: "integer", nullable: false),
                    SecondaryExternalIdAttributeId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    MetaverseObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    JoinType = table.Column<int>(type: "integer", nullable: false),
                    DateJoined = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemObjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjects_ConnectedSystemObjectTypes_TypeId",
                        column: x => x.TypeId,
                        principalTable: "ConnectedSystemObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjects_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjects_MetaverseObjects_MetaverseObjectId",
                        column: x => x.MetaverseObjectId,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SyncRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemObjectTypeId = table.Column<int>(type: "integer", nullable: false),
                    MetaverseObjectTypeId = table.Column<int>(type: "integer", nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    ProvisionToConnectedSystem = table.Column<bool>(type: "boolean", nullable: true),
                    ProjectToMetaverse = table.Column<bool>(type: "boolean", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRules_ConnectedSystemObjectTypes_ConnectedSystemObjectT~",
                        column: x => x.ConnectedSystemObjectTypeId,
                        principalTable: "ConnectedSystemObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncRules_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncRules_MetaverseObjects_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRules_MetaverseObjectTypes_MetaverseObjectTypeId",
                        column: x => x.MetaverseObjectTypeId,
                        principalTable: "MetaverseObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemContainers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PartitionId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    ExternalId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Hidden = table.Column<bool>(type: "boolean", nullable: false),
                    Selected = table.Column<bool>(type: "boolean", nullable: false),
                    ParentContainerId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemContainers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemContainers_ConnectedSystemContainers_ParentC~",
                        column: x => x.ParentContainerId,
                        principalTable: "ConnectedSystemContainers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConnectedSystemContainers_ConnectedSystemPartitions_Partiti~",
                        column: x => x.PartitionId,
                        principalTable: "ConnectedSystemPartitions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConnectedSystemContainers_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemRunProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    PartitionId = table.Column<int>(type: "integer", nullable: true),
                    RunType = table.Column<int>(type: "integer", nullable: false),
                    PageSize = table.Column<int>(type: "integer", nullable: false),
                    FilePath = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemRunProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemRunProfiles_ConnectedSystemPartitions_Partit~",
                        column: x => x.PartitionId,
                        principalTable: "ConnectedSystemPartitions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConnectedSystemRunProfiles_ConnectedSystems_ConnectedSystem~",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetaverseObjectChangeAttributes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MetaverseObjectChangeId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttributeId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseObjectChangeAttributes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetaverseObjectChangeAttributes_MetaverseAttributes_Attribu~",
                        column: x => x.AttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MetaverseObjectChangeAttributes_MetaverseObjectChanges_Meta~",
                        column: x => x.MetaverseObjectChangeId,
                        principalTable: "MetaverseObjectChanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PredefinedSearchCriteria",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComparisonType = table.Column<int>(type: "integer", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: false),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: false),
                    PredefinedSearchCriteriaGroupId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PredefinedSearchCriteria", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PredefinedSearchCriteria_MetaverseAttributes_MetaverseAttri~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PredefinedSearchCriteria_PredefinedSearchCriteriaGroups_Pre~",
                        column: x => x.PredefinedSearchCriteriaGroupId,
                        principalTable: "PredefinedSearchCriteriaGroups",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "DataGenerationTemplateAttributes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemObjectTypeAttributeId = table.Column<int>(type: "integer", nullable: true),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: true),
                    PopulatedValuesPercentage = table.Column<int>(type: "integer", nullable: true),
                    BoolTrueDistribution = table.Column<int>(type: "integer", nullable: true),
                    BoolShouldBeRandom = table.Column<bool>(type: "boolean", nullable: true),
                    MinDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MaxDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MinNumber = table.Column<int>(type: "integer", nullable: true),
                    MaxNumber = table.Column<int>(type: "integer", nullable: true),
                    SequentialNumbers = table.Column<bool>(type: "boolean", nullable: true),
                    RandomNumbers = table.Column<bool>(type: "boolean", nullable: true),
                    Pattern = table.Column<string>(type: "text", nullable: true),
                    ManagerDepthPercentage = table.Column<int>(type: "integer", nullable: true),
                    MvaRefMinAssignments = table.Column<int>(type: "integer", nullable: true),
                    MvaRefMaxAssignments = table.Column<int>(type: "integer", nullable: true),
                    AttributeDependencyId = table.Column<int>(type: "integer", nullable: true),
                    DataGenerationObjectTypeId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataGenerationTemplateAttributes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributes_ConnectedSystemAttributes_~",
                        column: x => x.ConnectedSystemObjectTypeAttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributes_DataGenerationObjectTypes_~",
                        column: x => x.DataGenerationObjectTypeId,
                        principalTable: "DataGenerationObjectTypes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributes_DataGenerationTemplateAttr~",
                        column: x => x.AttributeDependencyId,
                        principalTable: "DataGenerationTemplateAttributeDependencies",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributes_MetaverseAttributes_Metave~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemObjectAttributeValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttributeId = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IntValue = table.Column<int>(type: "integer", nullable: true),
                    ByteValue = table.Column<byte[]>(type: "bytea", nullable: true),
                    GuidValue = table.Column<Guid>(type: "uuid", nullable: true),
                    BoolValue = table.Column<bool>(type: "boolean", nullable: true),
                    ReferenceValueId = table.Column<Guid>(type: "uuid", nullable: true),
                    UnresolvedReferenceValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemObjectAttributeValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjectAttributeValues_ConnectedSystemAttribu~",
                        column: x => x.AttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjectAttributeValues_ConnectedSystemObject~1",
                        column: x => x.ReferenceValueId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjectAttributeValues_ConnectedSystemObjects~",
                        column: x => x.ConnectedSystemObjectId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingExports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangeType = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorCount = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingExports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingExports_ConnectedSystemObjects_ConnectedSystemObject~",
                        column: x => x.ConnectedSystemObjectId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PendingExports_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncRuleMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: true),
                    AttributeFlowSynchronisationRuleId = table.Column<int>(type: "integer", nullable: true),
                    ObjectMatchingSynchronisationRuleId = table.Column<int>(type: "integer", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    TargetMetaverseAttributeId = table.Column<int>(type: "integer", nullable: true),
                    TargetConnectedSystemAttributeId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuleMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuleMappings_ConnectedSystemAttributes_TargetConnectedS~",
                        column: x => x.TargetConnectedSystemAttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMappings_MetaverseAttributes_TargetMetaverseAttribu~",
                        column: x => x.TargetMetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMappings_MetaverseObjects_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMappings_SyncRules_AttributeFlowSynchronisationRule~",
                        column: x => x.AttributeFlowSynchronisationRuleId,
                        principalTable: "SyncRules",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMappings_SyncRules_ObjectMatchingSynchronisationRul~",
                        column: x => x.ObjectMatchingSynchronisationRuleId,
                        principalTable: "SyncRules",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SyncRuleScopingCriteriaGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    ParentGroupId = table.Column<int>(type: "integer", nullable: true),
                    SyncRuleId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuleScopingCriteriaGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuleScopingCriteriaGroups_SyncRules_SyncRuleId",
                        column: x => x.SyncRuleId,
                        principalTable: "SyncRules",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleScopingCriteriaGroups_SyncRuleScopingCriteriaGroups~",
                        column: x => x.ParentGroupId,
                        principalTable: "SyncRuleScopingCriteriaGroups",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Activities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentActivityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Executed = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InitiatedByName = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ErrorStackTrace = table.Column<string>(type: "text", nullable: true),
                    ExecutionTime = table.Column<TimeSpan>(type: "interval", nullable: true),
                    TotalActivityTime = table.Column<TimeSpan>(type: "interval", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TargetType = table.Column<int>(type: "integer", nullable: false),
                    TargetOperationType = table.Column<int>(type: "integer", nullable: false),
                    TargetName = table.Column<string>(type: "text", nullable: true),
                    DataGenerationTemplateId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    SyncRuleId = table.Column<int>(type: "integer", nullable: true),
                    MetaverseObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectedSystemRunProfileId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemRunType = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Activities_ConnectedSystemRunProfiles_ConnectedSystemRunPro~",
                        column: x => x.ConnectedSystemRunProfileId,
                        principalTable: "ConnectedSystemRunProfiles",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Activities_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Activities_MetaverseObjects_MetaverseObjectId",
                        column: x => x.MetaverseObjectId,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Activities_SyncRules_SyncRuleId",
                        column: x => x.SyncRuleId,
                        principalTable: "SyncRules",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "MetaverseObjectChangeAttributeValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MetaverseObjectChangeAttributeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ValueChangeType = table.Column<int>(type: "integer", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IntValue = table.Column<int>(type: "integer", nullable: true),
                    ByteValueLength = table.Column<int>(type: "integer", nullable: true),
                    GuidValue = table.Column<Guid>(type: "uuid", nullable: true),
                    BoolValue = table.Column<bool>(type: "boolean", nullable: true),
                    ReferenceValueId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseObjectChangeAttributeValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetaverseObjectChangeAttributeValues_MetaverseObjectChangeA~",
                        column: x => x.MetaverseObjectChangeAttributeId,
                        principalTable: "MetaverseObjectChangeAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MetaverseObjectChangeAttributeValues_MetaverseObjects_Refer~",
                        column: x => x.ReferenceValueId,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "DataGenerationTemplateAttributeMetaverseObjectType",
                columns: table => new
                {
                    DataGenerationTemplateAttributesId = table.Column<int>(type: "integer", nullable: false),
                    ReferenceMetaverseObjectTypesId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataGenerationTemplateAttributeMetaverseObjectType", x => new { x.DataGenerationTemplateAttributesId, x.ReferenceMetaverseObjectTypesId });
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributeMetaverseObjectType_DataGene~",
                        column: x => x.DataGenerationTemplateAttributesId,
                        principalTable: "DataGenerationTemplateAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributeMetaverseObjectType_Metavers~",
                        column: x => x.ReferenceMetaverseObjectTypesId,
                        principalTable: "MetaverseObjectTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DataGenerationTemplateAttributeWeightedValue",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Value = table.Column<string>(type: "text", nullable: false),
                    Weight = table.Column<float>(type: "real", nullable: false),
                    DataGenerationTemplateAttributeId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataGenerationTemplateAttributeWeightedValue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributeWeightedValue_DataGeneration~",
                        column: x => x.DataGenerationTemplateAttributeId,
                        principalTable: "DataGenerationTemplateAttributes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ExampleDataSetInstances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DataGenerationTemplateAttributeId = table.Column<int>(type: "integer", nullable: false),
                    ExampleDataSetId = table.Column<int>(type: "integer", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExampleDataSetInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExampleDataSetInstances_DataGenerationTemplateAttributes_Da~",
                        column: x => x.DataGenerationTemplateAttributeId,
                        principalTable: "DataGenerationTemplateAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExampleDataSetInstances_ExampleDataSets_ExampleDataSetId",
                        column: x => x.ExampleDataSetId,
                        principalTable: "ExampleDataSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingExportAttributeValueChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttributeId = table.Column<int>(type: "integer", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IntValue = table.Column<int>(type: "integer", nullable: true),
                    ByteValue = table.Column<byte[]>(type: "bytea", nullable: true),
                    ChangeType = table.Column<int>(type: "integer", nullable: false),
                    ErrorCount = table.Column<int>(type: "integer", nullable: true),
                    PendingExportId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingExportAttributeValueChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingExportAttributeValueChanges_ConnectedSystemAttribute~",
                        column: x => x.AttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PendingExportAttributeValueChanges_PendingExports_PendingEx~",
                        column: x => x.PendingExportId,
                        principalTable: "PendingExports",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SyncRuleMappingSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemAttributeId = table.Column<int>(type: "integer", nullable: true),
                    FunctionId = table.Column<int>(type: "integer", nullable: true),
                    SyncRuleMappingId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuleMappingSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSources_ConnectedSystemAttributes_ConnectedS~",
                        column: x => x.ConnectedSystemAttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSources_Function_FunctionId",
                        column: x => x.FunctionId,
                        principalTable: "Function",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSources_MetaverseAttributes_MetaverseAttribu~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSources_SyncRuleMappings_SyncRuleMappingId",
                        column: x => x.SyncRuleMappingId,
                        principalTable: "SyncRuleMappings",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SyncRuleScopingCriteria",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: false),
                    ComparisonType = table.Column<int>(type: "integer", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    IntValue = table.Column<int>(type: "integer", nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BoolValue = table.Column<bool>(type: "boolean", nullable: true),
                    GuidValue = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncRuleScopingCriteriaGroupId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuleScopingCriteria", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuleScopingCriteria_MetaverseAttributes_MetaverseAttrib~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncRuleScopingCriteria_SyncRuleScopingCriteriaGroups_SyncR~",
                        column: x => x.SyncRuleScopingCriteriaGroupId,
                        principalTable: "SyncRuleScopingCriteriaGroups",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ActivityRunProfileExecutionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObjectChangeType = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    DataSnapshot = table.Column<string>(type: "text", nullable: true),
                    ErrorType = table.Column<int>(type: "integer", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ErrorStackTrace = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityRunProfileExecutionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityRunProfileExecutionItems_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ActivityRunProfileExecutionItems_ConnectedSystemObjects_Con~",
                        column: x => x.ConnectedSystemObjectId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkerTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExecutionMode = table.Column<int>(type: "integer", nullable: false),
                    InitiatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    InitiatedByName = table.Column<string>(type: "text", nullable: true),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Discriminator = table.Column<string>(type: "text", nullable: false),
                    ClearConnectedSystemObjectsWorkerTask_ConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    TemplateId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemRunProfileId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkerTasks_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkerTasks_MetaverseObjects_InitiatedById",
                        column: x => x.InitiatedById,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SyncRuleMappingSourceParamValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    FunctionParameterId = table.Column<int>(type: "integer", nullable: true),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemAttributeId = table.Column<int>(type: "integer", nullable: true),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DoubleValue = table.Column<double>(type: "double precision", nullable: false),
                    IntValue = table.Column<int>(type: "integer", nullable: false),
                    BoolValue = table.Column<bool>(type: "boolean", nullable: false),
                    SyncRuleMappingSourceId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuleMappingSourceParamValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSourceParamValues_ConnectedSystemAttributes_~",
                        column: x => x.ConnectedSystemAttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSourceParamValues_FunctionParameter_Function~",
                        column: x => x.FunctionParameterId,
                        principalTable: "FunctionParameter",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSourceParamValues_MetaverseAttributes_Metave~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSourceParamValues_SyncRuleMappingSources_Syn~",
                        column: x => x.SyncRuleMappingSourceId,
                        principalTable: "SyncRuleMappingSources",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemObjectChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityRunProfileExecutionItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangeTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangeType = table.Column<int>(type: "integer", nullable: false),
                    DeletedObjectTypeId = table.Column<int>(type: "integer", nullable: true),
                    DeletedObjectExternalIdAttributeValueId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemObjectChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjectChanges_ActivityRunProfileExecutionIte~",
                        column: x => x.ActivityRunProfileExecutionItemId,
                        principalTable: "ActivityRunProfileExecutionItems",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjectChanges_ConnectedSystemObjectAttribute~",
                        column: x => x.DeletedObjectExternalIdAttributeValueId,
                        principalTable: "ConnectedSystemObjectAttributeValues",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjectChanges_ConnectedSystemObjects_Connect~",
                        column: x => x.ConnectedSystemObjectId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjectChanges_ConnectedSystemObjectTypes_Del~",
                        column: x => x.DeletedObjectTypeId,
                        principalTable: "ConnectedSystemObjectTypes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemObjectChangeAttributes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemChangeId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttributeId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemObjectChangeAttributes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjectChangeAttributes_ConnectedSystemAttrib~",
                        column: x => x.AttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjectChangeAttributes_ConnectedSystemObject~",
                        column: x => x.ConnectedSystemChangeId,
                        principalTable: "ConnectedSystemObjectChanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemObjectChangeAttributeValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemObjectChangeAttributeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ValueChangeType = table.Column<int>(type: "integer", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IntValue = table.Column<int>(type: "integer", nullable: true),
                    ByteValueLength = table.Column<int>(type: "integer", nullable: true),
                    GuidValue = table.Column<Guid>(type: "uuid", nullable: true),
                    BoolValue = table.Column<bool>(type: "boolean", nullable: true),
                    ReferenceValueId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemObjectChangeAttributeValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjectChangeAttributeValues_ConnectedSystem~1",
                        column: x => x.ReferenceValueId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjectChangeAttributeValues_ConnectedSystemO~",
                        column: x => x.ConnectedSystemObjectChangeAttributeId,
                        principalTable: "ConnectedSystemObjectChangeAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ConnectedSystemId",
                table: "Activities",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ConnectedSystemRunProfileId",
                table: "Activities",
                column: "ConnectedSystemRunProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_MetaverseObjectId",
                table: "Activities",
                column: "MetaverseObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_SyncRuleId",
                table: "Activities",
                column: "SyncRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityRunProfileExecutionItems_ActivityId",
                table: "ActivityRunProfileExecutionItems",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityRunProfileExecutionItems_ConnectedSystemObjectId",
                table: "ActivityRunProfileExecutionItems",
                column: "ConnectedSystemObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemAttributes_ConnectedSystemObjectTypeId",
                table: "ConnectedSystemAttributes",
                column: "ConnectedSystemObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemContainers_ConnectedSystemId",
                table: "ConnectedSystemContainers",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemContainers_ParentContainerId",
                table: "ConnectedSystemContainers",
                column: "ParentContainerId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemContainers_PartitionId",
                table: "ConnectedSystemContainers",
                column: "PartitionId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_AttributeId",
                table: "ConnectedSystemObjectAttributeValues",
                column: "AttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_ConnectedSystemObjectId",
                table: "ConnectedSystemObjectAttributeValues",
                column: "ConnectedSystemObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_ReferenceValueId",
                table: "ConnectedSystemObjectAttributeValues",
                column: "ReferenceValueId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectChangeAttributes_AttributeId",
                table: "ConnectedSystemObjectChangeAttributes",
                column: "AttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectChangeAttributes_ConnectedSystemChange~",
                table: "ConnectedSystemObjectChangeAttributes",
                column: "ConnectedSystemChangeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectChangeAttributeValues_ConnectedSystemO~",
                table: "ConnectedSystemObjectChangeAttributeValues",
                column: "ConnectedSystemObjectChangeAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectChangeAttributeValues_ReferenceValueId",
                table: "ConnectedSystemObjectChangeAttributeValues",
                column: "ReferenceValueId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectChanges_ActivityRunProfileExecutionIte~",
                table: "ConnectedSystemObjectChanges",
                column: "ActivityRunProfileExecutionItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectChanges_ConnectedSystemObjectId",
                table: "ConnectedSystemObjectChanges",
                column: "ConnectedSystemObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectChanges_DeletedObjectExternalIdAttribu~",
                table: "ConnectedSystemObjectChanges",
                column: "DeletedObjectExternalIdAttributeValueId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectChanges_DeletedObjectTypeId",
                table: "ConnectedSystemObjectChanges",
                column: "DeletedObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjects_ConnectedSystemId",
                table: "ConnectedSystemObjects",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjects_MetaverseObjectId",
                table: "ConnectedSystemObjects",
                column: "MetaverseObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjects_TypeId",
                table: "ConnectedSystemObjects",
                column: "TypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectTypes_ConnectedSystemId",
                table: "ConnectedSystemObjectTypes",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemPartitions_ConnectedSystemId",
                table: "ConnectedSystemPartitions",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemRunProfiles_ConnectedSystemId",
                table: "ConnectedSystemRunProfiles",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemRunProfiles_PartitionId",
                table: "ConnectedSystemRunProfiles",
                column: "PartitionId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystems_ConnectorDefinitionId",
                table: "ConnectedSystems",
                column: "ConnectorDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemSettingValue_ConnectedSystemId",
                table: "ConnectedSystemSettingValue",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemSettingValue_SettingId",
                table: "ConnectedSystemSettingValue",
                column: "SettingId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorContainers_ConnectorContainerId",
                table: "ConnectorContainers",
                column: "ConnectorContainerId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorContainers_ConnectorPartitionId",
                table: "ConnectorContainers",
                column: "ConnectorPartitionId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorDefinitionFiles_ConnectorDefinitionId",
                table: "ConnectorDefinitionFiles",
                column: "ConnectorDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorDefinitionSetting_ConnectorDefinitionId",
                table: "ConnectorDefinitionSetting",
                column: "ConnectorDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationObjectTypes_DataGenerationTemplateId",
                table: "DataGenerationObjectTypes",
                column: "DataGenerationTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationObjectTypes_MetaverseObjectTypeId",
                table: "DataGenerationObjectTypes",
                column: "MetaverseObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationTemplateAttributeDependencies_MetaverseAttrib~",
                table: "DataGenerationTemplateAttributeDependencies",
                column: "MetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationTemplateAttributeMetaverseObjectType_Referenc~",
                table: "DataGenerationTemplateAttributeMetaverseObjectType",
                column: "ReferenceMetaverseObjectTypesId");

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationTemplateAttributes_AttributeDependencyId",
                table: "DataGenerationTemplateAttributes",
                column: "AttributeDependencyId");

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationTemplateAttributes_ConnectedSystemObjectTypeA~",
                table: "DataGenerationTemplateAttributes",
                column: "ConnectedSystemObjectTypeAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationTemplateAttributes_DataGenerationObjectTypeId",
                table: "DataGenerationTemplateAttributes",
                column: "DataGenerationObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationTemplateAttributes_MetaverseAttributeId",
                table: "DataGenerationTemplateAttributes",
                column: "MetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationTemplateAttributeWeightedValue_DataGeneration~",
                table: "DataGenerationTemplateAttributeWeightedValue",
                column: "DataGenerationTemplateAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_DataGenerationTemplates_Name",
                table: "DataGenerationTemplates",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ExampleDataSetInstances_DataGenerationTemplateAttributeId",
                table: "ExampleDataSetInstances",
                column: "DataGenerationTemplateAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ExampleDataSetInstances_ExampleDataSetId",
                table: "ExampleDataSetInstances",
                column: "ExampleDataSetId");

            migrationBuilder.CreateIndex(
                name: "IX_ExampleDataSetValues_ExampleDataSetId",
                table: "ExampleDataSetValues",
                column: "ExampleDataSetId");

            migrationBuilder.CreateIndex(
                name: "IX_Function_FunctionLibraryId",
                table: "Function",
                column: "FunctionLibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_FunctionParameter_FunctionId",
                table: "FunctionParameter",
                column: "FunctionId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseAttributeMetaverseObjectType_MetaverseObjectTypesId",
                table: "MetaverseAttributeMetaverseObjectType",
                column: "MetaverseObjectTypesId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseAttributes_Name",
                table: "MetaverseAttributes",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_AttributeId",
                table: "MetaverseObjectAttributeValues",
                column: "AttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_ContributedBySystemId",
                table: "MetaverseObjectAttributeValues",
                column: "ContributedBySystemId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_DateTimeValue",
                table: "MetaverseObjectAttributeValues",
                column: "DateTimeValue");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_GuidValue",
                table: "MetaverseObjectAttributeValues",
                column: "GuidValue");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_IntValue",
                table: "MetaverseObjectAttributeValues",
                column: "IntValue");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_MetaverseObjectId",
                table: "MetaverseObjectAttributeValues",
                column: "MetaverseObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_ReferenceValueId",
                table: "MetaverseObjectAttributeValues",
                column: "ReferenceValueId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_StringValue",
                table: "MetaverseObjectAttributeValues",
                column: "StringValue");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectChangeAttributes_AttributeId",
                table: "MetaverseObjectChangeAttributes",
                column: "AttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectChangeAttributes_MetaverseObjectChangeId",
                table: "MetaverseObjectChangeAttributes",
                column: "MetaverseObjectChangeId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectChangeAttributeValues_MetaverseObjectChangeA~",
                table: "MetaverseObjectChangeAttributeValues",
                column: "MetaverseObjectChangeAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectChangeAttributeValues_ReferenceValueId",
                table: "MetaverseObjectChangeAttributeValues",
                column: "ReferenceValueId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectChanges_ChangeInitiatorId",
                table: "MetaverseObjectChanges",
                column: "ChangeInitiatorId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectChanges_MetaverseObjectId",
                table: "MetaverseObjectChanges",
                column: "MetaverseObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectRole_StaticMembersId",
                table: "MetaverseObjectRole",
                column: "StaticMembersId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjects_TypeId",
                table: "MetaverseObjects",
                column: "TypeId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectTypes_Name",
                table: "MetaverseObjectTypes",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_PendingExportAttributeValueChanges_AttributeId",
                table: "PendingExportAttributeValueChanges",
                column: "AttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingExportAttributeValueChanges_PendingExportId",
                table: "PendingExportAttributeValueChanges",
                column: "PendingExportId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingExports_ConnectedSystemId",
                table: "PendingExports",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingExports_ConnectedSystemObjectId",
                table: "PendingExports",
                column: "ConnectedSystemObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_PredefinedSearchAttribute_MetaverseAttributeId",
                table: "PredefinedSearchAttribute",
                column: "MetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_PredefinedSearchAttribute_PredefinedSearchId",
                table: "PredefinedSearchAttribute",
                column: "PredefinedSearchId");

            migrationBuilder.CreateIndex(
                name: "IX_PredefinedSearchCriteria_MetaverseAttributeId",
                table: "PredefinedSearchCriteria",
                column: "MetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_PredefinedSearchCriteria_PredefinedSearchCriteriaGroupId",
                table: "PredefinedSearchCriteria",
                column: "PredefinedSearchCriteriaGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_PredefinedSearchCriteriaGroups_ParentGroupId",
                table: "PredefinedSearchCriteriaGroups",
                column: "ParentGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_PredefinedSearchCriteriaGroups_PredefinedSearchId",
                table: "PredefinedSearchCriteriaGroups",
                column: "PredefinedSearchId");

            migrationBuilder.CreateIndex(
                name: "IX_PredefinedSearches_MetaverseObjectTypeId",
                table: "PredefinedSearches",
                column: "MetaverseObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_PredefinedSearches_Uri",
                table: "PredefinedSearches",
                column: "Uri");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceSettings_SSOUniqueIdentifierMetaverseAttributeId",
                table: "ServiceSettings",
                column: "SSOUniqueIdentifierMetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappings_AttributeFlowSynchronisationRuleId",
                table: "SyncRuleMappings",
                column: "AttributeFlowSynchronisationRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappings_CreatedById",
                table: "SyncRuleMappings",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappings_ObjectMatchingSynchronisationRuleId",
                table: "SyncRuleMappings",
                column: "ObjectMatchingSynchronisationRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappings_TargetConnectedSystemAttributeId",
                table: "SyncRuleMappings",
                column: "TargetConnectedSystemAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappings_TargetMetaverseAttributeId",
                table: "SyncRuleMappings",
                column: "TargetMetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSourceParamValues_ConnectedSystemAttributeId",
                table: "SyncRuleMappingSourceParamValues",
                column: "ConnectedSystemAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSourceParamValues_FunctionParameterId",
                table: "SyncRuleMappingSourceParamValues",
                column: "FunctionParameterId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSourceParamValues_MetaverseAttributeId",
                table: "SyncRuleMappingSourceParamValues",
                column: "MetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSourceParamValues_SyncRuleMappingSourceId",
                table: "SyncRuleMappingSourceParamValues",
                column: "SyncRuleMappingSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSources_ConnectedSystemAttributeId",
                table: "SyncRuleMappingSources",
                column: "ConnectedSystemAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSources_FunctionId",
                table: "SyncRuleMappingSources",
                column: "FunctionId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSources_MetaverseAttributeId",
                table: "SyncRuleMappingSources",
                column: "MetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSources_SyncRuleMappingId",
                table: "SyncRuleMappingSources",
                column: "SyncRuleMappingId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRules_ConnectedSystemId",
                table: "SyncRules",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRules_ConnectedSystemObjectTypeId",
                table: "SyncRules",
                column: "ConnectedSystemObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRules_CreatedById",
                table: "SyncRules",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRules_MetaverseObjectTypeId",
                table: "SyncRules",
                column: "MetaverseObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleScopingCriteria_MetaverseAttributeId",
                table: "SyncRuleScopingCriteria",
                column: "MetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleScopingCriteria_SyncRuleScopingCriteriaGroupId",
                table: "SyncRuleScopingCriteria",
                column: "SyncRuleScopingCriteriaGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleScopingCriteriaGroups_ParentGroupId",
                table: "SyncRuleScopingCriteriaGroups",
                column: "ParentGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleScopingCriteriaGroups_SyncRuleId",
                table: "SyncRuleScopingCriteriaGroups",
                column: "SyncRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerTasks_ActivityId",
                table: "WorkerTasks",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerTasks_InitiatedById",
                table: "WorkerTasks",
                column: "InitiatedById");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectedSystemContainers");

            migrationBuilder.DropTable(
                name: "ConnectedSystemObjectChangeAttributeValues");

            migrationBuilder.DropTable(
                name: "ConnectedSystemSettingValue");

            migrationBuilder.DropTable(
                name: "ConnectorContainers");

            migrationBuilder.DropTable(
                name: "ConnectorDefinitionFiles");

            migrationBuilder.DropTable(
                name: "DataGenerationTemplateAttributeMetaverseObjectType");

            migrationBuilder.DropTable(
                name: "DataGenerationTemplateAttributeWeightedValue");

            migrationBuilder.DropTable(
                name: "ExampleDataSetInstances");

            migrationBuilder.DropTable(
                name: "ExampleDataSetValues");

            migrationBuilder.DropTable(
                name: "MetaverseAttributeMetaverseObjectType");

            migrationBuilder.DropTable(
                name: "MetaverseObjectAttributeValues");

            migrationBuilder.DropTable(
                name: "MetaverseObjectChangeAttributeValues");

            migrationBuilder.DropTable(
                name: "MetaverseObjectRole");

            migrationBuilder.DropTable(
                name: "PendingExportAttributeValueChanges");

            migrationBuilder.DropTable(
                name: "PredefinedSearchAttribute");

            migrationBuilder.DropTable(
                name: "PredefinedSearchCriteria");

            migrationBuilder.DropTable(
                name: "ServiceSettings");

            migrationBuilder.DropTable(
                name: "SyncRuleMappingSourceParamValues");

            migrationBuilder.DropTable(
                name: "SyncRuleScopingCriteria");

            migrationBuilder.DropTable(
                name: "WorkerTasks");

            migrationBuilder.DropTable(
                name: "ConnectedSystemObjectChangeAttributes");

            migrationBuilder.DropTable(
                name: "ConnectorDefinitionSetting");

            migrationBuilder.DropTable(
                name: "ConnectorPartitions");

            migrationBuilder.DropTable(
                name: "DataGenerationTemplateAttributes");

            migrationBuilder.DropTable(
                name: "ExampleDataSets");

            migrationBuilder.DropTable(
                name: "MetaverseObjectChangeAttributes");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "PendingExports");

            migrationBuilder.DropTable(
                name: "PredefinedSearchCriteriaGroups");

            migrationBuilder.DropTable(
                name: "FunctionParameter");

            migrationBuilder.DropTable(
                name: "SyncRuleMappingSources");

            migrationBuilder.DropTable(
                name: "SyncRuleScopingCriteriaGroups");

            migrationBuilder.DropTable(
                name: "ConnectedSystemObjectChanges");

            migrationBuilder.DropTable(
                name: "DataGenerationObjectTypes");

            migrationBuilder.DropTable(
                name: "DataGenerationTemplateAttributeDependencies");

            migrationBuilder.DropTable(
                name: "MetaverseObjectChanges");

            migrationBuilder.DropTable(
                name: "PredefinedSearches");

            migrationBuilder.DropTable(
                name: "Function");

            migrationBuilder.DropTable(
                name: "SyncRuleMappings");

            migrationBuilder.DropTable(
                name: "ActivityRunProfileExecutionItems");

            migrationBuilder.DropTable(
                name: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.DropTable(
                name: "DataGenerationTemplates");

            migrationBuilder.DropTable(
                name: "FunctionLibrary");

            migrationBuilder.DropTable(
                name: "MetaverseAttributes");

            migrationBuilder.DropTable(
                name: "Activities");

            migrationBuilder.DropTable(
                name: "ConnectedSystemAttributes");

            migrationBuilder.DropTable(
                name: "ConnectedSystemObjects");

            migrationBuilder.DropTable(
                name: "ConnectedSystemRunProfiles");

            migrationBuilder.DropTable(
                name: "SyncRules");

            migrationBuilder.DropTable(
                name: "ConnectedSystemPartitions");

            migrationBuilder.DropTable(
                name: "ConnectedSystemObjectTypes");

            migrationBuilder.DropTable(
                name: "MetaverseObjects");

            migrationBuilder.DropTable(
                name: "ConnectedSystems");

            migrationBuilder.DropTable(
                name: "MetaverseObjectTypes");

            migrationBuilder.DropTable(
                name: "ConnectorDefinitions");
        }
    }
}
