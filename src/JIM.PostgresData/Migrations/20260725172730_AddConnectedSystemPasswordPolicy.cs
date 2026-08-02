using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JIM.PostgresData.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectedSystemPasswordPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConnectedSystemPasswordPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectedSystemId = table.Column<int>(type: "integer", nullable: false),
                    Discovered = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MinimumLength = table.Column<int>(type: "integer", nullable: true),
                    ComplexityRequired = table.Column<bool>(type: "boolean", nullable: true),
                    RequiredCharacterClassCount = table.Column<int>(type: "integer", nullable: true),
                    RecognisedCharacterClasses = table.Column<int>(type: "integer", nullable: false),
                    PasswordHistoryLength = table.Column<int>(type: "integer", nullable: true),
                    MaximumPasswordAge = table.Column<TimeSpan>(type: "interval", nullable: true),
                    MinimumPasswordAge = table.Column<TimeSpan>(type: "interval", nullable: true),
                    FineGrainedPolicySignal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedSystemPasswordPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConnectedSystemPasswordPolicies_ConnectedSystems_ConnectedS~",
                        column: x => x.ConnectedSystemId,
                        principalTable: "ConnectedSystems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedSystemPasswordPolicies_ConnectedSystemId",
                table: "ConnectedSystemPasswordPolicies",
                column: "ConnectedSystemId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectedSystemPasswordPolicies");
        }
    }
}
