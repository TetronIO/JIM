using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddCausalEdgeWordingSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CauseObjectTypeName",
                table: "CausalEdges",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CauseObjectTypePluralName",
                table: "CausalEdges",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EffectAttributeName",
                table: "CausalEdges",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CauseObjectTypeName",
                table: "CausalEdges");

            migrationBuilder.DropColumn(
                name: "CauseObjectTypePluralName",
                table: "CausalEdges");

            migrationBuilder.DropColumn(
                name: "EffectAttributeName",
                table: "CausalEdges");
        }
    }
}
