using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class HistoryToActivityRefactor : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectChanges_SyncRunHistoryDetailItem_SyncR~",
                table: "ConnectedSystemObjectChanges");

            migrationBuilder.DropTable(
                name: "SyncRunHistoryDetailItem");

            migrationBuilder.DropTable(
                name: "SyncRunHistoryDetails");

            migrationBuilder.DropTable(
                name: "HistoryItems");

            migrationBuilder.CreateTable(
                name: "Activities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentActivityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InitiatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    InitiatedByName = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ErrorStackTrace = table.Column<string>(type: "text", nullable: true),
                    CompletionTime = table.Column<TimeSpan>(type: "interval", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TargetType = table.Column<int>(type: "integer", nullable: false),
                    TargetOperationType = table.Column<int>(type: "integer", nullable: false),
                    TargetName = table.Column<string>(type: "text", nullable: true),
                    DataGenerationTemplateId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    RunProfileId = table.Column<int>(type: "integer", nullable: true),
                    RunType = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Activities_ConnectedSystemRunProfiles_RunProfileId",
                        column: x => x.RunProfileId,
                        principalTable: "ConnectedSystemRunProfiles",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Activities_ConnectedSystems_ConnectedSystemId",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Activities_MetaverseObjects_InitiatedById",
                        column: x => x.InitiatedById,
                        principalTable: "MetaverseObjects",
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
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ConnectedSystemId",
                table: "Activities",
                column: "ConnectedSystemId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_InitiatedById",
                table: "Activities",
                column: "InitiatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_RunProfileId",
                table: "Activities",
                column: "RunProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityRunProfileExecutionItems_ActivityId",
                table: "ActivityRunProfileExecutionItems",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityRunProfileExecutionItems_ConnectedSystemObjectId",
                table: "ActivityRunProfileExecutionItems",
                column: "ConnectedSystemObjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemObjectChanges_ActivityRunProfileExecutionIte~",
                table: "ConnectedSystemObjectChanges",
                column: "SyncRunHistoryDetailItemId",
                principalTable: "ActivityRunProfileExecutionItems",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemObjectChanges_ActivityRunProfileExecutionIte~",
                table: "ConnectedSystemObjectChanges");

            migrationBuilder.DropTable(
                name: "ActivityRunProfileExecutionItems");

            migrationBuilder.DropTable(
                name: "Activities");

            migrationBuilder.CreateTable(
                name: "HistoryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InitiatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletionTime = table.Column<TimeSpan>(type: "interval", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Discriminator = table.Column<string>(type: "text", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ErrorStackTrace = table.Column<string>(type: "text", nullable: true),
                    InitiatedByName = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    DataGenerationTemplateId = table.Column<int>(type: "integer", nullable: true),
                    SynchronisationRunHistoryDetailId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HistoryItems_MetaverseObjects_InitiatedById",
                        column: x => x.InitiatedById,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SyncRunHistoryDetails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    RunHistoryItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    RunProfileId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemName = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<int>(type: "integer", nullable: true),
                    RunProfileName = table.Column<string>(type: "text", nullable: true),
                    RunType = table.Column<int>(type: "integer", nullable: false)
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
                name: "SyncRunHistoryDetailItem",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectedSystemObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncRunHistoryDetailId = table.Column<Guid>(type: "uuid", nullable: false),
                    DataSnapshot = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<int>(type: "integer", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ErrorStackTrace = table.Column<string>(type: "text", nullable: true),
                    ObjectChangeType = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRunHistoryDetailItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRunHistoryDetailItem_ConnectedSystemObjects_ConnectedSy~",
                        column: x => x.ConnectedSystemObjectId,
                        principalTable: "ConnectedSystemObjects",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SyncRunHistoryDetailItem_SyncRunHistoryDetails_SyncRunHisto~",
                        column: x => x.SyncRunHistoryDetailId,
                        principalTable: "SyncRunHistoryDetails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_InitiatedById",
                table: "HistoryItems",
                column: "InitiatedById");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRunHistoryDetailItem_ConnectedSystemObjectId",
                table: "SyncRunHistoryDetailItem",
                column: "ConnectedSystemObjectId");

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
                name: "FK_ConnectedSystemObjectChanges_SyncRunHistoryDetailItem_SyncR~",
                table: "ConnectedSystemObjectChanges",
                column: "SyncRunHistoryDetailItemId",
                principalTable: "SyncRunHistoryDetailItem",
                principalColumn: "Id");
        }
    }
}
