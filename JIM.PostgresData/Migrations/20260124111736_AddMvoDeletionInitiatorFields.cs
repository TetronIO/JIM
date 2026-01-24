using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddMvoDeletionInitiatorFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DeletionInitiatedById",
                table: "MetaverseObjects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionInitiatedByName",
                table: "MetaverseObjects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletionInitiatedByType",
                table: "MetaverseObjects",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletionInitiatedById",
                table: "MetaverseObjects");

            migrationBuilder.DropColumn(
                name: "DeletionInitiatedByName",
                table: "MetaverseObjects");

            migrationBuilder.DropColumn(
                name: "DeletionInitiatedByType",
                table: "MetaverseObjects");
        }
    }
}
