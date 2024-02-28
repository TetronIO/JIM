using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class MVOandCSOChanges : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HistoryItems_SynchronisationRunHistoryDetails_Synchronisati~",
                table: "HistoryItems");

            migrationBuilder.DropTable(
                name: "SynchronisationRunHistoryDetailItem");

            migrationBuilder.DropTable(
                name: "SyncRunObjects");

            migrationBuilder.DropTable(
                name: "SynchronisationRunHistoryDetails");

            migrationBuilder.DropTable(
                name: "SyncRuns");

            migrationBuilder.DropIndex(
                name: "IX_HistoryItems_SynchronisationRunHistoryDetailId",
                table: "HistoryItems");

            migrationBuilder.AddColumn<Guid>(
                name: "ReferenceValueId",
                table: "ConnectedSystemObjectAttributeValues",
                type: "uuid",
                nullable: true);

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
                name: "SyncRunHistoryDetails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunHistoryItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemName = table.Column<string>(type: "text", nullable: true),
                    RunProfileId = table.Column<int>(type: "integer", nullable: true),
                    RunProfileName = table.Column<string>(type: "text", nullable: true),
                    RunType = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<int>(type: "integer", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ErrorStackTrace = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRunHistoryDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRunHistoryDetails_ConnectedSystemRunProfiles_RunProfile~",
                        column: x => x.RunProfileId,
                        principalTable: "ConnectedSystemRunProfiles",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRunHistoryDetails_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRunHistoryDetails_HistoryItems_RunHistoryItemId",
                        column: x => x.RunHistoryItemId,
                        principalTable: "HistoryItems",
                        principalColumn: "Id");
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
                name: "SyncRunHistoryDetailItem",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SyncRunHistoryDetailId = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSnapshot = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<int>(type: "integer", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ErrorStackTrace = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRunHistoryDetailItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRunHistoryDetailItem_SyncRunHistoryDetails_SyncRunHisto~",
                        column: x => x.SyncRunHistoryDetailId,
                        principalTable: "SyncRunHistoryDetails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "ConnectedSystemObjectChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SyncRunHistoryDetailItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangeTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangeType = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemObjectChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjectChanges_ConnectedSystemObjects_Connect~",
                        column: x => x.ConnectedSystemObjectId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjectChanges_ConnectedSystems_ConnectedSyst~",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemObjectChanges_SyncRunHistoryDetailItem_SyncR~",
                        column: x => x.SyncRunHistoryDetailItemId,
                        principalTable: "SyncRunHistoryDetailItem",
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
                name: "IX_ConnectedSystemObjectChanges_ConnectedSystemId",
                table: "ConnectedSystemObjectChanges",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectChanges_ConnectedSystemObjectId",
                table: "ConnectedSystemObjectChanges",
                column: "ConnectedSystemObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemObjectChanges_SyncRunHistoryDetailItemId",
                table: "ConnectedSystemObjectChanges",
                column: "SyncRunHistoryDetailItemId",
                unique: true);

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
                name: "IX_SyncRunHistoryDetailItem_SyncRunHistoryDetailId",
                table: "SyncRunHistoryDetailItem",
                column: "SyncRunHistoryDetailId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRunHistoryDetails_ConnectedSystemId",
                table: "SyncRunHistoryDetails",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRunHistoryDetails_RunHistoryItemId",
                table: "SyncRunHistoryDetails",
                column: "RunHistoryItemId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRunHistoryDetails_RunProfileId",
                table: "SyncRunHistoryDetails",
                column: "RunProfileId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectAttributeValues_ConnectedSystemObject~1",
                table: "ConnectedSystemObjectAttributeValues",
                column: "ReferenceValueId",
                principalTable: "ConnectedSystemObjects",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectAttributeValues_ConnectedSystemObject~1",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.DropTable(
                name: "ConnectedSystemObjectChangeAttributeValues");

            migrationBuilder.DropTable(
                name: "MetaverseObjectChangeAttributeValues");

            migrationBuilder.DropTable(
                name: "ConnectedSystemObjectChangeAttributes");

            migrationBuilder.DropTable(
                name: "MetaverseObjectChangeAttributes");

            migrationBuilder.DropTable(
                name: "ConnectedSystemObjectChanges");

            migrationBuilder.DropTable(
                name: "MetaverseObjectChanges");

            migrationBuilder.DropTable(
                name: "SyncRunHistoryDetailItem");

            migrationBuilder.DropTable(
                name: "SyncRunHistoryDetails");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemObjectAttributeValues_ReferenceValueId",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.DropColumn(
                name: "ReferenceValueId",
                table: "ConnectedSystemObjectAttributeValues");

            migrationBuilder.CreateTable(
                name: "SynchronisationRunHistoryDetails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    RunProfileId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemName = table.Column<string>(type: "text", nullable: true),
                    RunProfileName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SynchronisationRunHistoryDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SynchronisationRunHistoryDetails_ConnectedSystemRunProfiles~",
                        column: x => x.RunProfileId,
                        principalTable: "ConnectedSystemRunProfiles",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SynchronisationRunHistoryDetails_ConnectedSystems_Connected~",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SyncRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ConnectedSystemStackTrace = table.Column<string>(type: "text", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RunType = table.Column<int>(type: "integer", nullable: false)
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
                name: "SynchronisationRunHistoryDetailItem",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    SynchronisationRunHistoryDetailId = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSnapshot = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<int>(type: "integer", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ErrorStackTrace = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SynchronisationRunHistoryDetailItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SynchronisationRunHistoryDetailItem_ConnectedSystemObjects_~",
                        column: x => x.ConnectedSystemObjectId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SynchronisationRunHistoryDetailItem_SynchronisationRunHisto~",
                        column: x => x.SynchronisationRunHistoryDetailId,
                        principalTable: "SynchronisationRunHistoryDetails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncRunObjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    PendingExportId = table.Column<Guid>(type: "uuid", nullable: true),
                    SynchronisationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ConnectedSystemStackTrace = table.Column<string>(type: "text", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Result = table.Column<int>(type: "integer", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_SynchronisationRunHistoryDetailId",
                table: "HistoryItems",
                column: "SynchronisationRunHistoryDetailId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SynchronisationRunHistoryDetailItem_ConnectedSystemObjectId",
                table: "SynchronisationRunHistoryDetailItem",
                column: "ConnectedSystemObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_SynchronisationRunHistoryDetailItem_SynchronisationRunHisto~",
                table: "SynchronisationRunHistoryDetailItem",
                column: "SynchronisationRunHistoryDetailId");

            migrationBuilder.CreateIndex(
                name: "IX_SynchronisationRunHistoryDetails_ConnectedSystemId",
                table: "SynchronisationRunHistoryDetails",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_SynchronisationRunHistoryDetails_RunProfileId",
                table: "SynchronisationRunHistoryDetails",
                column: "RunProfileId");

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

            migrationBuilder.AddForeignKey(
                name: "FK_HistoryItems_SynchronisationRunHistoryDetails_Synchronisati~",
                table: "HistoryItems",
                column: "SynchronisationRunHistoryDetailId",
                principalTable: "SynchronisationRunHistoryDetails",
                principalColumn: "Id");
        }
    }
}
