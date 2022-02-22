using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class InitialCreate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConnectedSystems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FunctionLibrary",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
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
                name: "MetaverseObjectTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseObjectTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemAttributes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    AttributePlurality = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemAttributes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemAttributes_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemObjectTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConnectedSystemId = table.Column<Guid>(type: "uuid", nullable: false)
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
                name: "ConnectedSystemRunProfile",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunType = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemRunProfile", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemRunProfile_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SynchronisationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RunType = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ConnectedSystemStackTrace = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SynchronisationRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SynchronisationRuns_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Function",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FunctionLibraryId = table.Column<Guid>(type: "uuid", nullable: false),
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
                name: "MetaverseAttributes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    AttributePlurality = table.Column<int>(type: "integer", nullable: false),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    MetaverseObjectTypeId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseAttributes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetaverseAttributes_MetaverseObjectTypes_MetaverseObjectTyp~",
                        column: x => x.MetaverseObjectTypeId,
                        principalTable: "MetaverseObjectTypes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemObjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    UniqueIdentifierAttributeId = table.Column<Guid>(type: "uuid", nullable: false)
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
                });

            migrationBuilder.CreateTable(
                name: "SyncRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConnectedSystemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemObjectTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    MetaverseObjectTypeId = table.Column<Guid>(type: "uuid", nullable: false),
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
                name: "FunctionParameter",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FunctionId = table.Column<Guid>(type: "uuid", nullable: false),
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
                name: "ServiceSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SSOAuthority = table.Column<string>(type: "text", nullable: true),
                    SSOClientId = table.Column<string>(type: "text", nullable: true),
                    SSOSecret = table.Column<string>(type: "text", nullable: true),
                    SSONameIDAttributeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SSOEnableLogOut = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceSettings_MetaverseAttributes_SSONameIDAttributeId",
                        column: x => x.SSONameIDAttributeId,
                        principalTable: "MetaverseAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConnectedSystemAttributeValue",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttributeId = table.Column<Guid>(type: "uuid", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IntValue = table.Column<int>(type: "integer", nullable: false),
                    ByteValue = table.Column<byte[]>(type: "bytea", nullable: false),
                    ConnectedSystemObjectId = table.Column<Guid>(type: "uuid", nullable: true)
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
                name: "SyncRunObject",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SynchronisationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConnectedSystemObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Result = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ConnectedSystemStackTrace = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRunObject", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRunObject_ConnectedSystemObjects_ConnectedSystemObjectId",
                        column: x => x.ConnectedSystemObjectId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRunObject_SynchronisationRuns_SynchronisationRunId",
                        column: x => x.SynchronisationRunId,
                        principalTable: "SynchronisationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncRuleMapping",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SynchronisationRuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    TargetMetaverseAttributeId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetConnectedSystemAttributeId = table.Column<Guid>(type: "uuid", nullable: true)
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
                name: "SyncRuleMappingSource",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    MetaverseAttributeId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectedSystemAttributeId = table.Column<Guid>(type: "uuid", nullable: true),
                    FunctionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncRuleMappingId = table.Column<Guid>(type: "uuid", nullable: true)
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
                name: "SyncRuleMappingSourceParamValue",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FunctionParameterId = table.Column<Guid>(type: "uuid", nullable: false),
                    MetaverseAttributeId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectedSystemAttributeId = table.Column<Guid>(type: "uuid", nullable: true),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DoubleValue = table.Column<double>(type: "double precision", nullable: false),
                    SyncRuleMappingSourceId = table.Column<Guid>(type: "uuid", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "MetaverseObjectAttributeValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AttributeId = table.Column<Guid>(type: "uuid", nullable: false),
                    StringValue = table.Column<string>(type: "text", nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IntValue = table.Column<int>(type: "integer", nullable: false),
                    ByteValue = table.Column<byte[]>(type: "bytea", nullable: false),
                    ContributedBySystemId = table.Column<Guid>(type: "uuid", nullable: true),
                    MetaverseObjectId = table.Column<Guid>(type: "uuid", nullable: false)
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
                });

            migrationBuilder.CreateTable(
                name: "MetaverseObjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: true)
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
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Roles_MetaverseObjects_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemAttributes_ConnectedSystemId",
                table: "ConnectedSystemAttributes",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemAttributeValue_AttributeId",
                table: "ConnectedSystemAttributeValue",
                column: "AttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemAttributeValue_ConnectedSystemObjectId",
                table: "ConnectedSystemAttributeValue",
                column: "ConnectedSystemObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjects_ConnectedSystemId",
                table: "ConnectedSystemObjects",
                column: "ConnectedSystemId");

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
                name: "IX_ConnectedSystemRunProfile_ConnectedSystemId",
                table: "ConnectedSystemRunProfile",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_Function_FunctionLibraryId",
                table: "Function",
                column: "FunctionLibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_FunctionParameter_FunctionId",
                table: "FunctionParameter",
                column: "FunctionId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseAttributes_MetaverseObjectTypeId",
                table: "MetaverseAttributes",
                column: "MetaverseObjectTypeId");

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
                name: "IX_MetaverseObjectAttributeValues_MetaverseObjectId",
                table: "MetaverseObjectAttributeValues",
                column: "MetaverseObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectAttributeValues_StringValue",
                table: "MetaverseObjectAttributeValues",
                column: "StringValue");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjects_RoleId",
                table: "MetaverseObjects",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjects_TypeId",
                table: "MetaverseObjects",
                column: "TypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_CreatedById",
                table: "Roles",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceSettings_SSONameIDAttributeId",
                table: "ServiceSettings",
                column: "SSONameIDAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_SynchronisationRuns_ConnectedSystemId",
                table: "SynchronisationRuns",
                column: "ConnectedSystemId");

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
                name: "IX_SyncRunObject_ConnectedSystemObjectId",
                table: "SyncRunObject",
                column: "ConnectedSystemObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRunObject_SynchronisationRunId",
                table: "SyncRunObject",
                column: "SynchronisationRunId");

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectAttributeValues_MetaverseObjects_MetaverseOb~",
                table: "MetaverseObjectAttributeValues",
                column: "MetaverseObjectId",
                principalTable: "MetaverseObjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjects_Roles_RoleId",
                table: "MetaverseObjects",
                column: "RoleId",
                principalTable: "Roles",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjects_MetaverseObjectTypes_TypeId",
                table: "MetaverseObjects");

            migrationBuilder.DropForeignKey(
                name: "FK_Roles_MetaverseObjects_CreatedById",
                table: "Roles");

            migrationBuilder.DropTable(
                name: "ConnectedSystemAttributeValue");

            migrationBuilder.DropTable(
                name: "ConnectedSystemRunProfile");

            migrationBuilder.DropTable(
                name: "MetaverseObjectAttributeValues");

            migrationBuilder.DropTable(
                name: "ServiceSettings");

            migrationBuilder.DropTable(
                name: "SyncRuleMappingSourceParamValue");

            migrationBuilder.DropTable(
                name: "SyncRunObject");

            migrationBuilder.DropTable(
                name: "FunctionParameter");

            migrationBuilder.DropTable(
                name: "SyncRuleMappingSource");

            migrationBuilder.DropTable(
                name: "ConnectedSystemObjects");

            migrationBuilder.DropTable(
                name: "SynchronisationRuns");

            migrationBuilder.DropTable(
                name: "Function");

            migrationBuilder.DropTable(
                name: "SyncRuleMapping");

            migrationBuilder.DropTable(
                name: "FunctionLibrary");

            migrationBuilder.DropTable(
                name: "ConnectedSystemAttributes");

            migrationBuilder.DropTable(
                name: "MetaverseAttributes");

            migrationBuilder.DropTable(
                name: "SyncRules");

            migrationBuilder.DropTable(
                name: "ConnectedSystemObjectTypes");

            migrationBuilder.DropTable(
                name: "ConnectedSystems");

            migrationBuilder.DropTable(
                name: "MetaverseObjectTypes");

            migrationBuilder.DropTable(
                name: "MetaverseObjects");

            migrationBuilder.DropTable(
                name: "Roles");
        }
    }
}
