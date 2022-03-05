using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class FixObjectRoleRelationship : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjects_Roles_RoleId",
                table: "MetaverseObjects");

            migrationBuilder.DropForeignKey(
                name: "FK_Roles_MetaverseObjects_CreatedById",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_Roles_CreatedById",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_MetaverseObjects_RoleId",
                table: "MetaverseObjects");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "RoleId",
                table: "MetaverseObjects");

            migrationBuilder.CreateTable(
                name: "MetaverseObjectRole",
                columns: table => new
                {
                    RolesId = table.Column<int>(type: "integer", nullable: false),
                    StaticMembersId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseObjectRole", x => new { x.RolesId, x.StaticMembersId });
                    table.ForeignKey(
                        name: "FK_MetaverseObjectRole_MetaverseObjects_StaticMembersId",
                        column: x => x.StaticMembersId,
                        principalTable: "MetaverseObjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MetaverseObjectRole_Roles_RolesId",
                        column: x => x.RolesId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjectRole_StaticMembersId",
                table: "MetaverseObjectRole",
                column: "StaticMembersId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetaverseObjectRole");

            migrationBuilder.AddColumn<int>(
                name: "CreatedById",
                table: "Roles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RoleId",
                table: "MetaverseObjects",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_CreatedById",
                table: "Roles",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseObjects_RoleId",
                table: "MetaverseObjects",
                column: "RoleId");

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjects_Roles_RoleId",
                table: "MetaverseObjects",
                column: "RoleId",
                principalTable: "Roles",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Roles_MetaverseObjects_CreatedById",
                table: "Roles",
                column: "CreatedById",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");
        }
    }
}
