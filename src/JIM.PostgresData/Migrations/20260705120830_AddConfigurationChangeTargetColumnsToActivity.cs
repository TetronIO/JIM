using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConfigurationChangeTargetColumnsToActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ApiKeyId",
                table: "Activities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConnectorDefinitionId",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExampleDataSetId",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetaverseAttributeId",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetaverseObjectTypeId",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PredefinedSearchId",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RoleId",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceSettingKey",
                table: "Activities",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TrustedCertificateId",
                table: "Activities",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApiKeyId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ConnectorDefinitionId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ExampleDataSetId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "MetaverseAttributeId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "MetaverseObjectTypeId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "PredefinedSearchId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "RoleId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ServiceSettingKey",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "TrustedCertificateId",
                table: "Activities");
        }
    }
}
