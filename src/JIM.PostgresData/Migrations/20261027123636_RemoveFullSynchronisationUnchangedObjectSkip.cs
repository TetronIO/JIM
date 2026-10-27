using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class RemoveFullSynchronisationUnchangedObjectSkip : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncRuleLastDeletedAt",
                table: "ServiceSettings");

            migrationBuilder.DropColumn(
                name: "ConfigurationLastFullyAppliedAt",
                table: "ConnectedSystems");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SyncRuleLastDeletedAt",
                table: "ServiceSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConfigurationLastFullyAppliedAt",
                table: "ConnectedSystems",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
