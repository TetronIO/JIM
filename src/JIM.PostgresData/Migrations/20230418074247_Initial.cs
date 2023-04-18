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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    SupportsPartitionContainers = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataGenerationTemplates",
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
                    table.PrimaryKey("PK_DataGenerationTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExampleDataSets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                name: "ServiceTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Discriminator = table.Column<string>(type: "text", nullable: false),
                    TemplateId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemRunProfileId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConnectorDefinitionId = table.Column<int>(type: "integer", nullable: false),
                    SettingValuesValid = table.Column<bool>(type: "boolean", nullable: false)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                name: "ExampleDataSetValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                name: "DataGenerationTemplateAttributeDependency",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: false),
                    ComparisonType = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataGenerationTemplateAttributeDependency", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributeDependency_MetaverseAttribut~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SSOAuthority = table.Column<string>(type: "text", nullable: true),
                    SSOClientId = table.Column<string>(type: "text", nullable: true),
                    SSOSecret = table.Column<string>(type: "text", nullable: true),
                    SSOUniqueIdentifierClaimType = table.Column<string>(type: "text", nullable: true),
                    SSOUniqueIdentifierMetaverseAttributeId = table.Column<int>(type: "integer", nullable: true),
                    SSOEnableLogOut = table.Column<bool>(type: "boolean", nullable: false),
                    IsServiceInMaintenanceMode = table.Column<bool>(type: "boolean", nullable: false),
                    RunHistoryRetentionPeriod = table.Column<TimeSpan>(type: "interval", nullable: false)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MetaverseObjectTypeId = table.Column<int>(type: "integer", nullable: false),
                    IsDefaultForMetaverseObjectType = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Uri = table.Column<string>(type: "text", nullable: false),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                name: "SyncRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RunType = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ConnectedSystemStackTrace = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuns_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemSettingValue",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FunctionId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
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
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AttributeId = table.Column<int>(type: "integer", nullable: false),
                    MetaverseObjectId = table.Column<int>(type: "integer", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IntValue = table.Column<int>(type: "integer", nullable: true),
                    ByteValue = table.Column<byte[]>(type: "bytea", nullable: true),
                    ReferenceValueId = table.Column<int>(type: "integer", nullable: true),
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
                name: "MetaverseObjectRole",
                columns: table => new
                {
                    RolesId = table.Column<int>(type: "integer", nullable: false),
                    StaticMembersId = table.Column<int>(type: "integer", nullable: false)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ClassName = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    AttributePlurality = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemObjectTypeId = table.Column<int>(type: "integer", nullable: false),
                    Selected = table.Column<bool>(type: "boolean", nullable: false)
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
                name: "SyncRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemObjectTypeId = table.Column<int>(type: "integer", nullable: false),
                    MetaverseObjectTypeId = table.Column<int>(type: "integer", nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    ProvisionToConnectedSystem = table.Column<bool>(type: "boolean", nullable: true),
                    ProjectToMetaverse = table.Column<bool>(type: "boolean", nullable: true)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    PartitionId = table.Column<int>(type: "integer", nullable: true),
                    RunType = table.Column<int>(type: "integer", nullable: false)
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
                name: "PredefinedSearchCriteria",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                name: "ConnectedSystemObjects",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TypeId = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    UniqueIdentifierAttributeId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    MetaverseObjectId = table.Column<int>(type: "integer", nullable: true),
                    JoinType = table.Column<int>(type: "integer", nullable: false),
                    DateJoined = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemObjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjects_ConnectedSystemAttributes_UniqueIden~",
                        column: x => x.UniqueIdentifierAttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "DataGenerationTemplateAttributes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemAttributeId = table.Column<int>(type: "integer", nullable: true),
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
                        column: x => x.ConnectedSystemAttributeId,
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
                        principalTable: "DataGenerationTemplateAttributeDependency",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DataGenerationTemplateAttributes_MetaverseAttributes_Metave~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SyncRuleMapping",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SynchronisationRuleId = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    TargetMetaverseAttributeId = table.Column<int>(type: "integer", nullable: true),
                    TargetConnectedSystemAttributeId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuleMapping", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuleMapping_ConnectedSystemAttributes_TargetConnectedSy~",
                        column: x => x.TargetConnectedSystemAttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMapping_MetaverseAttributes_TargetMetaverseAttribut~",
                        column: x => x.TargetMetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMapping_SyncRules_SynchronisationRuleId",
                        column: x => x.SynchronisationRuleId,
                        principalTable: "SyncRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemAttributeValue",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AttributeId = table.Column<int>(type: "integer", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IntValue = table.Column<int>(type: "integer", nullable: false),
                    ByteValue = table.Column<byte[]>(type: "bytea", nullable: false),
                    ConnectedSystemObjectId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemAttributeValue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemAttributeValue_ConnectedSystemAttributes_Att~",
                        column: x => x.AttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemAttributeValue_ConnectedSystemObjects_Connec~",
                        column: x => x.ConnectedSystemObjectId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PendingExports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemObjectId = table.Column<long>(type: "bigint", nullable: true),
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                name: "SyncRuleMappingSource",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemAttributeId = table.Column<int>(type: "integer", nullable: true),
                    FunctionId = table.Column<int>(type: "integer", nullable: true),
                    SyncRuleMappingId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuleMappingSource", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSource_ConnectedSystemAttributes_ConnectedSy~",
                        column: x => x.ConnectedSystemAttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSource_Function_FunctionId",
                        column: x => x.FunctionId,
                        principalTable: "Function",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSource_MetaverseAttributes_MetaverseAttribut~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSource_SyncRuleMapping_SyncRuleMappingId",
                        column: x => x.SyncRuleMappingId,
                        principalTable: "SyncRuleMapping",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PendingExportAttributeValueChanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AttributeId = table.Column<int>(type: "integer", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IntValue = table.Column<int>(type: "integer", nullable: true),
                    ByteValue = table.Column<byte[]>(type: "bytea", nullable: true),
                    ChangeType = table.Column<int>(type: "integer", nullable: false),
                    ErrorCount = table.Column<int>(type: "integer", nullable: true),
                    PendingExportId = table.Column<long>(type: "bigint", nullable: true)
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
                name: "SyncRunObjects",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SynchronisationRunId = table.Column<long>(type: "bigint", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConnectedSystemObjectId = table.Column<long>(type: "bigint", nullable: true),
                    PendingExportId = table.Column<long>(type: "bigint", nullable: true),
                    Result = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ConnectedSystemStackTrace = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRunObjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRunObjects_ConnectedSystemObjects_ConnectedSystemObject~",
                        column: x => x.ConnectedSystemObjectId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRunObjects_PendingExports_PendingExportId",
                        column: x => x.PendingExportId,
                        principalTable: "PendingExports",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRunObjects_SyncRuns_SynchronisationRunId",
                        column: x => x.SynchronisationRunId,
                        principalTable: "SyncRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncRuleMappingSourceParamValue",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FunctionParameterId = table.Column<int>(type: "integer", nullable: false),
                    MetaverseAttributeId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemAttributeId = table.Column<int>(type: "integer", nullable: true),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DoubleValue = table.Column<double>(type: "double precision", nullable: false),
                    SyncRuleMappingSourceId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuleMappingSourceParamValue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSourceParamValue_ConnectedSystemAttributes_C~",
                        column: x => x.ConnectedSystemAttributeId,
                        principalTable: "ConnectedSystemAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSourceParamValue_FunctionParameter_FunctionP~",
                        column: x => x.FunctionParameterId,
                        principalTable: "FunctionParameter",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSourceParamValue_MetaverseAttributes_Metaver~",
                        column: x => x.MetaverseAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRuleMappingSourceParamValue_SyncRuleMappingSource_SyncR~",
                        column: x => x.SyncRuleMappingSourceId,
                        principalTable: "SyncRuleMappingSource",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemAttributes_ConnectedSystemObjectTypeId",
                table: "ConnectedSystemAttributes",
                column: "ConnectedSystemObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemAttributeValue_AttributeId",
                table: "ConnectedSystemAttributeValue",
                column: "AttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemAttributeValue_ConnectedSystemObjectId",
                table: "ConnectedSystemAttributeValue",
                column: "ConnectedSystemObjectId");

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
                name: "IX_ConnectedSystemObjects_UniqueIdentifierAttributeId",
                table: "ConnectedSystemObjects",
                column: "UniqueIdentifierAttributeId");

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
                name: "IX_DataGenerationTemplateAttributeDependency_MetaverseAttribut~",
                table: "DataGenerationTemplateAttributeDependency",
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
                name: "IX_DataGenerationTemplateAttributes_ConnectedSystemAttributeId",
                table: "DataGenerationTemplateAttributes",
                column: "ConnectedSystemAttributeId");

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
                name: "IX_SyncRuleMapping_SynchronisationRuleId",
                table: "SyncRuleMapping",
                column: "SynchronisationRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMapping_TargetConnectedSystemAttributeId",
                table: "SyncRuleMapping",
                column: "TargetConnectedSystemAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMapping_TargetMetaverseAttributeId",
                table: "SyncRuleMapping",
                column: "TargetMetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSource_ConnectedSystemAttributeId",
                table: "SyncRuleMappingSource",
                column: "ConnectedSystemAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSource_FunctionId",
                table: "SyncRuleMappingSource",
                column: "FunctionId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSource_MetaverseAttributeId",
                table: "SyncRuleMappingSource",
                column: "MetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSource_SyncRuleMappingId",
                table: "SyncRuleMappingSource",
                column: "SyncRuleMappingId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSourceParamValue_ConnectedSystemAttributeId",
                table: "SyncRuleMappingSourceParamValue",
                column: "ConnectedSystemAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSourceParamValue_FunctionParameterId",
                table: "SyncRuleMappingSourceParamValue",
                column: "FunctionParameterId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSourceParamValue_MetaverseAttributeId",
                table: "SyncRuleMappingSourceParamValue",
                column: "MetaverseAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSourceParamValue_SyncRuleMappingSourceId",
                table: "SyncRuleMappingSourceParamValue",
                column: "SyncRuleMappingSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRules_ConnectedSystemId",
                table: "SyncRules",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRules_ConnectedSystemObjectTypeId",
                table: "SyncRules",
                column: "ConnectedSystemObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRules_MetaverseObjectTypeId",
                table: "SyncRules",
                column: "MetaverseObjectTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRunObjects_ConnectedSystemObjectId",
                table: "SyncRunObjects",
                column: "ConnectedSystemObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRunObjects_PendingExportId",
                table: "SyncRunObjects",
                column: "PendingExportId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRunObjects_SynchronisationRunId",
                table: "SyncRunObjects",
                column: "SynchronisationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuns_ConnectedSystemId",
                table: "SyncRuns",
                column: "ConnectedSystemId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectedSystemAttributeValue");

            migrationBuilder.DropTable(
                name: "ConnectedSystemContainers");

            migrationBuilder.DropTable(
                name: "ConnectedSystemRunProfiles");

            migrationBuilder.DropTable(
                name: "ConnectedSystemSettingValue");

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
                name: "ServiceTasks");

            migrationBuilder.DropTable(
                name: "SyncRuleMappingSourceParamValue");

            migrationBuilder.DropTable(
                name: "SyncRunObjects");

            migrationBuilder.DropTable(
                name: "ConnectedSystemPartitions");

            migrationBuilder.DropTable(
                name: "ConnectorDefinitionSetting");

            migrationBuilder.DropTable(
                name: "DataGenerationTemplateAttributes");

            migrationBuilder.DropTable(
                name: "ExampleDataSets");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "PredefinedSearchCriteriaGroups");

            migrationBuilder.DropTable(
                name: "FunctionParameter");

            migrationBuilder.DropTable(
                name: "SyncRuleMappingSource");

            migrationBuilder.DropTable(
                name: "PendingExports");

            migrationBuilder.DropTable(
                name: "SyncRuns");

            migrationBuilder.DropTable(
                name: "DataGenerationObjectTypes");

            migrationBuilder.DropTable(
                name: "DataGenerationTemplateAttributeDependency");

            migrationBuilder.DropTable(
                name: "PredefinedSearches");

            migrationBuilder.DropTable(
                name: "Function");

            migrationBuilder.DropTable(
                name: "SyncRuleMapping");

            migrationBuilder.DropTable(
                name: "ConnectedSystemObjects");

            migrationBuilder.DropTable(
                name: "DataGenerationTemplates");

            migrationBuilder.DropTable(
                name: "FunctionLibrary");

            migrationBuilder.DropTable(
                name: "MetaverseAttributes");

            migrationBuilder.DropTable(
                name: "SyncRules");

            migrationBuilder.DropTable(
                name: "ConnectedSystemAttributes");

            migrationBuilder.DropTable(
                name: "MetaverseObjects");

            migrationBuilder.DropTable(
                name: "ConnectedSystemObjectTypes");

            migrationBuilder.DropTable(
                name: "MetaverseObjectTypes");

            migrationBuilder.DropTable(
                name: "ConnectedSystems");

            migrationBuilder.DropTable(
                name: "ConnectorDefinitions");
        }
    }
}
