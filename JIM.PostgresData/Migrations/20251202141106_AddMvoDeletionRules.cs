using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddMvoDeletionRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeletionGracePeriodDays",
                table: "MetaverseObjectTypes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletionRule",
                table: "MetaverseObjectTypes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledDeletionDate",
                table: "MetaverseObjects",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletionGracePeriodDays",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropColumn(
                name: "DeletionRule",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropColumn(
                name: "ScheduledDeletionDate",
                table: "MetaverseObjects");
        }
    }
}
