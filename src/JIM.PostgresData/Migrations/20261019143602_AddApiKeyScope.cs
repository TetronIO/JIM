using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "ApiKeys",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ScopeConnectedSystemId",
                table: "ApiKeys",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_ScopeConnectedSystemId",
                table: "ApiKeys",
                column: "ScopeConnectedSystemId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ApiKeys_ScopeNamesItsConnectedSystem",
                table: "ApiKeys",
                sql: "(\"Scope\" = 0) = (\"ScopeConnectedSystemId\" IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_ApiKeys_ConnectedSystems_ScopeConnectedSystemId",
                table: "ApiKeys",
                column: "ScopeConnectedSystemId",
                principalTable: "ConnectedSystems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ApiKeys_ConnectedSystems_ScopeConnectedSystemId",
                table: "ApiKeys");

            migrationBuilder.DropIndex(
                name: "IX_ApiKeys_ScopeConnectedSystemId",
                table: "ApiKeys");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ApiKeys_ScopeNamesItsConnectedSystem",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "ScopeConnectedSystemId",
                table: "ApiKeys");
        }
    }
}
