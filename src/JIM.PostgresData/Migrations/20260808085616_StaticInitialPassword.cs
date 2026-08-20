using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class StaticInitialPassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StaticPasswordEncryptedValue",
                table: "SyncRuleInitialPasswords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StaticPasswordSetAt",
                table: "SyncRuleInitialPasswords",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StaticPasswordEncryptedValue",
                table: "SyncRuleInitialPasswords");

            migrationBuilder.DropColumn(
                name: "StaticPasswordSetAt",
                table: "SyncRuleInitialPasswords");
        }
    }
}
