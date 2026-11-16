using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectedSystemImportIncompleteSince : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ImportIncompleteSince",
                table: "ConnectedSystems",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImportIncompleteSince",
                table: "ConnectedSystems");
        }
    }
}
