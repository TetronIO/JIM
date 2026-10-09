using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class ActivityPreviewOvertaken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PreviewOvertakenAt",
                table: "Activities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviewOvertakenBy",
                table: "Activities",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviewOvertakenAt",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "PreviewOvertakenBy",
                table: "Activities");
        }
    }
}
