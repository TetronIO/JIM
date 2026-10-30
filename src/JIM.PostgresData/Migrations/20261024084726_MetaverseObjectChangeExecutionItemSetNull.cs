using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class MetaverseObjectChangeExecutionItemSetNull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectChanges_ActivityRunProfileExecutionItems_Act~",
                table: "MetaverseObjectChanges");

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectChanges_ActivityRunProfileExecutionItems_Act~",
                table: "MetaverseObjectChanges",
                column: "ActivityRunProfileExecutionItemId",
                principalTable: "ActivityRunProfileExecutionItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetaverseObjectChanges_ActivityRunProfileExecutionItems_Act~",
                table: "MetaverseObjectChanges");

            migrationBuilder.AddForeignKey(
                name: "FK_MetaverseObjectChanges_ActivityRunProfileExecutionItems_Act~",
                table: "MetaverseObjectChanges",
                column: "ActivityRunProfileExecutionItemId",
                principalTable: "ActivityRunProfileExecutionItems",
                principalColumn: "Id");
        }
    }
}
