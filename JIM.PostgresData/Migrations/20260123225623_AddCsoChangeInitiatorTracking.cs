using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddCsoChangeInitiatorTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "InitiatedById",
                table: "ConnectedSystemObjectChanges",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InitiatedByName",
                table: "ConnectedSystemObjectChanges",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InitiatedByType",
                table: "ConnectedSystemObjectChanges",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InitiatedById",
                table: "ConnectedSystemObjectChanges");

            migrationBuilder.DropColumn(
                name: "InitiatedByName",
                table: "ConnectedSystemObjectChanges");

            migrationBuilder.DropColumn(
                name: "InitiatedByType",
                table: "ConnectedSystemObjectChanges");
        }
    }
}
