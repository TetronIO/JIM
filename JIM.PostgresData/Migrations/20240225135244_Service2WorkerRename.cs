using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class Service2WorkerRename : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServiceTasks");

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
                name: "WorkerTasks");

            migrationBuilder.CreateTable(
                name: "ServiceTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitiatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    Discriminator = table.Column<string>(type: "text", nullable: false),
                    ExecutionMode = table.Column<int>(type: "integer", nullable: false),
                    InitiatedByName = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    TemplateId = table.Column<int>(type: "integer", nullable: true),
                    SynchronisationServiceTask_ConnectedSystemId = table.Column<int>(type: "integer", nullable: true),
                    ConnectedSystemRunProfileId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceTasks_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ServiceTasks_MetaverseObjects_InitiatedById",
                        column: x => x.InitiatedById,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceTasks_ActivityId",
                table: "ServiceTasks",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceTasks_InitiatedById",
                table: "ServiceTasks",
                column: "InitiatedById");
        }
    }
}
