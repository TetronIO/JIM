using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ServiceTaskActivity : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "CompletionTime",
                table: "Activities",
                newName: "TotalActivityTime");

            migrationBuilder.AddColumn<Guid>(
                name: "ActivityId",
                table: "ServiceTasks",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTime>(
                name: "Executed",
                table: "Activities",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<TimeSpan>(
                name: "ExecutionTime",
                table: "Activities",
                type: "interval",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceTasks_ActivityId",
                table: "ServiceTasks",
                column: "ActivityId");

            migrationBuilder.AddForeignKey(
                name: "FK_ServiceTasks_Activities_ActivityId",
                table: "ServiceTasks",
                column: "ActivityId",
                principalTable: "Activities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ServiceTasks_Activities_ActivityId",
                table: "ServiceTasks");

            migrationBuilder.DropIndex(
                name: "IX_ServiceTasks_ActivityId",
                table: "ServiceTasks");

            migrationBuilder.DropColumn(
                name: "ActivityId",
                table: "ServiceTasks");

            migrationBuilder.DropColumn(
                name: "Executed",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ExecutionTime",
                table: "Activities");

            migrationBuilder.RenameColumn(
                name: "TotalActivityTime",
                table: "Activities",
                newName: "CompletionTime");
        }
    }
}
