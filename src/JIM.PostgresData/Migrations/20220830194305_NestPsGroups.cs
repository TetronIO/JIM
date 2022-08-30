using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class NestPsGroups : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ParentGroupId",
                table: "PredefinedSearchCriteriaGroups",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PredefinedSearchCriteriaGroups_ParentGroupId",
                table: "PredefinedSearchCriteriaGroups",
                column: "ParentGroupId");

            migrationBuilder.AddForeignKey(
                name: "FK_PredefinedSearchCriteriaGroups_PredefinedSearchCriteriaGrou~",
                table: "PredefinedSearchCriteriaGroups",
                column: "ParentGroupId",
                principalTable: "PredefinedSearchCriteriaGroups",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PredefinedSearchCriteriaGroups_PredefinedSearchCriteriaGrou~",
                table: "PredefinedSearchCriteriaGroups");

            migrationBuilder.DropIndex(
                name: "IX_PredefinedSearchCriteriaGroups_ParentGroupId",
                table: "PredefinedSearchCriteriaGroups");

            migrationBuilder.DropColumn(
                name: "ParentGroupId",
                table: "PredefinedSearchCriteriaGroups");
        }
    }
}
