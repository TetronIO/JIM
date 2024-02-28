using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class SyncRuleActivityLink : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Activities_SyncRuleId",
                table: "Activities",
                column: "SyncRuleId");

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_SyncRules_SyncRuleId",
                table: "Activities",
                column: "SyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Activities_SyncRules_SyncRuleId",
                table: "Activities");

            migrationBuilder.DropIndex(
                name: "IX_Activities_SyncRuleId",
                table: "Activities");
        }
    }
}
