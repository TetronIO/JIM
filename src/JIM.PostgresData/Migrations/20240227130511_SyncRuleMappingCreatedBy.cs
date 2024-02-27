using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class SyncRuleMappingCreatedBy : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "Created",
                table: "SyncRuleMapping",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "SyncRuleMapping",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMapping_CreatedById",
                table: "SyncRuleMapping",
                column: "CreatedById");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMapping_MetaverseObjects_CreatedById",
                table: "SyncRuleMapping",
                column: "CreatedById",
                principalTable: "MetaverseObjects",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMapping_MetaverseObjects_CreatedById",
                table: "SyncRuleMapping");

            migrationBuilder.DropIndex(
                name: "IX_SyncRuleMapping_CreatedById",
                table: "SyncRuleMapping");

            migrationBuilder.DropColumn(
                name: "Created",
                table: "SyncRuleMapping");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "SyncRuleMapping");
        }
    }
}
