using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConfigurationChangePreviewWorkerTask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProposedConfigurationPayload",
                table: "WorkerTasks",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Surface",
                table: "WorkerTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetGuidId",
                table: "WorkerTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetId",
                table: "WorkerTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetName",
                table: "WorkerTasks",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProposedConfigurationPayload",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "Surface",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "TargetGuidId",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "TargetId",
                table: "WorkerTasks");

            migrationBuilder.DropColumn(
                name: "TargetName",
                table: "WorkerTasks");
        }
    }
}
