using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class InitialPasswordProvisioningConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProvisioningSyncRuleId",
                table: "PendingExports",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SyncRuleInitialPasswords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SyncRuleId = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    CustomPolicy_Style = table.Column<int>(type: "integer", nullable: false),
                    CustomPolicy_Length = table.Column<int>(type: "integer", nullable: false),
                    CustomPolicy_MinimumUppercase = table.Column<int>(type: "integer", nullable: false),
                    CustomPolicy_MinimumLowercase = table.Column<int>(type: "integer", nullable: false),
                    CustomPolicy_MinimumDigits = table.Column<int>(type: "integer", nullable: false),
                    CustomPolicy_MinimumSymbols = table.Column<int>(type: "integer", nullable: false),
                    CustomPolicy_PermittedSymbols = table.Column<string>(type: "text", nullable: false),
                    CustomPolicy_WordCount = table.Column<int>(type: "integer", nullable: false),
                    CustomPolicy_WordSeparator = table.Column<int>(type: "integer", nullable: false),
                    CustomPolicy_WordCapitalisation = table.Column<int>(type: "integer", nullable: false),
                    CustomPolicy_AppendedDigitCount = table.Column<int>(type: "integer", nullable: false),
                    CustomPolicy_AppendSymbol = table.Column<bool>(type: "boolean", nullable: false),
                    CustomPolicy_ExcludeAmbiguousCharacters = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiryBehaviour = table.Column<int>(type: "integer", nullable: false),
                    EnableAccount = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuleInitialPasswords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuleInitialPasswords_SyncRules_SyncRuleId",
                        column: x => x.SyncRuleId,
                        principalTable: "SyncRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingExports_ProvisioningSyncRuleId",
                table: "PendingExports",
                column: "ProvisioningSyncRuleId",
                filter: "\"ProvisioningSyncRuleId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleInitialPasswords_SyncRuleId",
                table: "SyncRuleInitialPasswords",
                column: "SyncRuleId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PendingExports_SyncRules_ProvisioningSyncRuleId",
                table: "PendingExports",
                column: "ProvisioningSyncRuleId",
                principalTable: "SyncRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PendingExports_SyncRules_ProvisioningSyncRuleId",
                table: "PendingExports");

            migrationBuilder.DropTable(
                name: "SyncRuleInitialPasswords");

            migrationBuilder.DropIndex(
                name: "IX_PendingExports_ProvisioningSyncRuleId",
                table: "PendingExports");

            migrationBuilder.DropColumn(
                name: "ProvisioningSyncRuleId",
                table: "PendingExports");
        }
    }
}
