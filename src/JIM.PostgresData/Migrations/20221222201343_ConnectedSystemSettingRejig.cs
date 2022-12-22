using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ConnectedSystemSettingRejig : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConnectedSystemSettings_ConnectedSystemSettingValues_ValueId",
                table: "ConnectedSystemSettings");

            migrationBuilder.DropTable(
                name: "ConnectedSystemSettingValues");

            migrationBuilder.DropIndex(
                name: "IX_ConnectedSystemSettings_ValueId",
                table: "ConnectedSystemSettings");

            migrationBuilder.DropColumn(
                name: "ValueId",
                table: "ConnectedSystemSettings");

            migrationBuilder.AddColumn<bool>(
                name: "CheckboxValue",
                table: "ConnectedSystemSettings",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StringEncryptedValue",
                table: "ConnectedSystemSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StringValue",
                table: "ConnectedSystemSettings",
                type: "text",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckboxValue",
                table: "ConnectedSystemSettings");

            migrationBuilder.DropColumn(
                name: "StringEncryptedValue",
                table: "ConnectedSystemSettings");

            migrationBuilder.DropColumn(
                name: "StringValue",
                table: "ConnectedSystemSettings");

            migrationBuilder.AddColumn<int>(
                name: "ValueId",
                table: "ConnectedSystemSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConnectedSystemSettingValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CheckboxValue = table.Column<bool>(type: "boolean", nullable: true),
                    StringEncryptedValue = table.Column<string>(type: "text", nullable: true),
                    StringValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemSettingValues", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemSettings_ValueId",
                table: "ConnectedSystemSettings",
                column: "ValueId");

            migrationBuilder.AddForeignKey(
                name: "FK_ConnectedSystemSettings_ConnectedSystemSettingValues_ValueId",
                table: "ConnectedSystemSettings",
                column: "ValueId",
                principalTable: "ConnectedSystemSettingValues",
                principalColumn: "Id");
        }
    }
}
