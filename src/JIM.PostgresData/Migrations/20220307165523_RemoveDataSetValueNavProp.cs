using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    public partial class RemoveDataSetValueNavProp : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExampleDataSetValues_ExampleDataSets_ExampleDataSetId",
                table: "ExampleDataSetValues");

            migrationBuilder.AlterColumn<int>(
                name: "ExampleDataSetId",
                table: "ExampleDataSetValues",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddForeignKey(
                name: "FK_ExampleDataSetValues_ExampleDataSets_ExampleDataSetId",
                table: "ExampleDataSetValues",
                column: "ExampleDataSetId",
                principalTable: "ExampleDataSets",
                principalColumn: "Id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExampleDataSetValues_ExampleDataSets_ExampleDataSetId",
                table: "ExampleDataSetValues");

            migrationBuilder.AlterColumn<int>(
                name: "ExampleDataSetId",
                table: "ExampleDataSetValues",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ExampleDataSetValues_ExampleDataSets_ExampleDataSetId",
                table: "ExampleDataSetValues",
                column: "ExampleDataSetId",
                principalTable: "ExampleDataSets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
