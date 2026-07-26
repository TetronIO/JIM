using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddCsoImportStateHashAndRunProfileVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "VerifyImportContentHashes",
                table: "ConnectedSystemRunProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ImportStateFingerprint",
                table: "ConnectedSystemObjects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ImportStateHash",
                table: "ConnectedSystemObjects",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VerifyImportContentHashes",
                table: "ConnectedSystemRunProfiles");

            migrationBuilder.DropColumn(
                name: "ImportStateFingerprint",
                table: "ConnectedSystemObjects");

            migrationBuilder.DropColumn(
                name: "ImportStateHash",
                table: "ConnectedSystemObjects");
        }
    }
}
