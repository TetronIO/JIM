using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoryRetentionCleanupToActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeletedActivityCount",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedCsoChangeCount",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedMvoChangeCount",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedRecordsFromDate",
                table: "Activities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedRecordsToDate",
                table: "Activities",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedActivityCount",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "DeletedCsoChangeCount",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "DeletedMvoChangeCount",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "DeletedRecordsFromDate",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "DeletedRecordsToDate",
                table: "Activities");
        }
    }
}
