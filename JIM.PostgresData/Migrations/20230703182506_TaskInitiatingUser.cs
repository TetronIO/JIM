using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class TaskInitiatingUser : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "InitiatedById",
                table: "ServiceTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InitiatedByName",
                table: "ServiceTasks",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceTasks_InitiatedById",
                table: "ServiceTasks",
                column: "InitiatedById");

            migrationBuilder.AddForeignKey(
                name: "FK_ServiceTasks_MetaverseObjects_InitiatedById",
                table: "ServiceTasks",
                column: "InitiatedById",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ServiceTasks_MetaverseObjects_InitiatedById",
                table: "ServiceTasks");

            migrationBuilder.DropIndex(
                name: "IX_ServiceTasks_InitiatedById",
                table: "ServiceTasks");

            migrationBuilder.DropColumn(
                name: "InitiatedById",
                table: "ServiceTasks");

            migrationBuilder.DropColumn(
                name: "InitiatedByName",
                table: "ServiceTasks");
        }
    }
}
