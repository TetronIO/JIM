using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class ObjectClassGroup : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "MetaverseObjectTypes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Order",
                table: "MetaverseObjectTypes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MetaverseObjectTypeGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    BuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseObjectTypeGroups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectTypes_GroupId",
                table: "MetaverseObjectTypes",
                column: "GroupId");

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectTypes_MetaverseObjectTypeGroups_GroupId",
                table: "MetaverseObjectTypes",
                column: "GroupId",
                principalTable: "MetaverseObjectTypeGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectTypes_MetaverseObjectTypeGroups_GroupId",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropTable(
                name: "MetaverseObjectTypeGroups");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjectTypes_GroupId",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "MetaverseObjectTypes");

            migrationBuilder.DropColumn(
                name: "Order",
                table: "MetaverseObjectTypes");
        }
    }
}
