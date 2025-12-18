using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddExpressionSupportRemoveFunctions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ObjectMatchingRuleSourceParamValues_FunctionParameter_Funct~",
                table: "ObjectMatchingRuleSourceParamValues");

            migrationBuilder.DropForeignKey(
                name: "FK_ObjectMatchingRuleSources_Function_FunctionId",
                table: "ObjectMatchingRuleSources");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSourceParamValues_FunctionParameter_Function~",
                table: "SyncRuleMappingSourceParamValues");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSourceParamValues_SyncRuleMappingSources_Syn~",
                table: "SyncRuleMappingSourceParamValues");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuleMappingSources_Function_FunctionId",
                table: "SyncRuleMappingSources");

            migrationBuilder.DropTable(
                name: "FunctionParameter");

            migrationBuilder.DropTable(
                name: "Function");

            migrationBuilder.DropTable(
                name: "FunctionLibrary");

            migrationBuilder.DropIndex(
                name: "IX_SyncRuleMappingSources_FunctionId",
                table: "SyncRuleMappingSources");

            migrationBuilder.DropIndex(
                name: "IX_SyncRuleMappingSourceParamValues_FunctionParameterId",
                table: "SyncRuleMappingSourceParamValues");

            migrationBuilder.DropIndex(
                name: "IX_SyncRuleMappingSourceParamValues_SyncRuleMappingSourceId",
                table: "SyncRuleMappingSourceParamValues");

            migrationBuilder.DropIndex(
                name: "IX_ObjectMatchingRuleSources_FunctionId",
                table: "ObjectMatchingRuleSources");

            migrationBuilder.DropIndex(
                name: "IX_ObjectMatchingRuleSourceParamValues_FunctionParameterId",
                table: "ObjectMatchingRuleSourceParamValues");

            migrationBuilder.DropColumn(
                name: "FunctionId",
                table: "SyncRuleMappingSources");

            migrationBuilder.DropColumn(
                name: "FunctionParameterId",
                table: "SyncRuleMappingSourceParamValues");

            migrationBuilder.DropColumn(
                name: "SyncRuleMappingSourceId",
                table: "SyncRuleMappingSourceParamValues");

            migrationBuilder.DropColumn(
                name: "FunctionId",
                table: "ObjectMatchingRuleSources");

            migrationBuilder.DropColumn(
                name: "FunctionParameterId",
                table: "ObjectMatchingRuleSourceParamValues");

            migrationBuilder.AddColumn<string>(
                name: "Expression",
                table: "SyncRuleMappingSources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Expression",
                table: "ObjectMatchingRuleSources",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Expression",
                table: "SyncRuleMappingSources");

            migrationBuilder.DropColumn(
                name: "Expression",
                table: "ObjectMatchingRuleSources");

            migrationBuilder.AddColumn<int>(
                name: "FunctionId",
                table: "SyncRuleMappingSources",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FunctionParameterId",
                table: "SyncRuleMappingSourceParamValues",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SyncRuleMappingSourceId",
                table: "SyncRuleMappingSourceParamValues",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FunctionId",
                table: "ObjectMatchingRuleSources",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FunctionParameterId",
                table: "ObjectMatchingRuleSourceParamValues",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FunctionLibrary",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Filename = table.Column<string>(type: "text", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FunctionLibrary", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Function",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FunctionLibraryId = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    OutputType = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Function", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Function_FunctionLibrary_FunctionLibraryId",
                        column: x => x.FunctionLibraryId,
                        principalTable: "FunctionLibrary",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FunctionParameter",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FunctionId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FunctionParameter", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FunctionParameter_Function_FunctionId",
                        column: x => x.FunctionId,
                        principalTable: "Function",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSources_FunctionId",
                table: "SyncRuleMappingSources",
                column: "FunctionId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSourceParamValues_FunctionParameterId",
                table: "SyncRuleMappingSourceParamValues",
                column: "FunctionParameterId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleMappingSourceParamValues_SyncRuleMappingSourceId",
                table: "SyncRuleMappingSourceParamValues",
                column: "SyncRuleMappingSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRuleSources_FunctionId",
                table: "ObjectMatchingRuleSources",
                column: "FunctionId");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectMatchingRuleSourceParamValues_FunctionParameterId",
                table: "ObjectMatchingRuleSourceParamValues",
                column: "FunctionParameterId");

            migrationBuilder.CreateIndex(
                name: "IX_Function_FunctionLibraryId",
                table: "Function",
                column: "FunctionLibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_FunctionParameter_FunctionId",
                table: "FunctionParameter",
                column: "FunctionId");

            migrationBuilder.AddForeignKey(
                name: "FK_ObjectMatchingRuleSourceParamValues_FunctionParameter_Funct~",
                table: "ObjectMatchingRuleSourceParamValues",
                column: "FunctionParameterId",
                principalTable: "FunctionParameter",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ObjectMatchingRuleSources_Function_FunctionId",
                table: "ObjectMatchingRuleSources",
                column: "FunctionId",
                principalTable: "Function",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSourceParamValues_FunctionParameter_Function~",
                table: "SyncRuleMappingSourceParamValues",
                column: "FunctionParameterId",
                principalTable: "FunctionParameter",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSourceParamValues_SyncRuleMappingSources_Syn~",
                table: "SyncRuleMappingSourceParamValues",
                column: "SyncRuleMappingSourceId",
                principalTable: "SyncRuleMappingSources",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuleMappingSources_Function_FunctionId",
                table: "SyncRuleMappingSources",
                column: "FunctionId",
                principalTable: "Function",
                principalColumn: "Id");
        }
    }
}
